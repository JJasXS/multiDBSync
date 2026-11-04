using System.Globalization;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount;

public interface IFirebirdPartyMutate
{
    Task<WriteResult> UpdateAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        MasterRecord record,
        string txnStamp,
        RegisteredDatabase? sourceDb,
        string? sourceEntityCode,
        CancellationToken cancellationToken = default);

    Task<WriteResult> DeleteAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        string code,
        CancellationToken cancellationToken = default);
}

public sealed class FirebirdPartyMutate : IFirebirdPartyMutate
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdPartyMutate> _logger;

    public FirebirdPartyMutate(CompanyConnectionFactory factory, ILogger<FirebirdPartyMutate> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<WriteResult> UpdateAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        MasterRecord record,
        string txnStamp,
        RegisteredDatabase? sourceDb,
        string? sourceEntityCode,
        CancellationToken cancellationToken = default)
    {
        if (!PartnerSyncRules.IsPartyEntity(targetEntityCode))
            return Fail($"Firebird party update does not support {targetEntityCode}.");

        var code = (record.Code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Party code is required.");

        var sourceEntity = string.IsNullOrWhiteSpace(sourceEntityCode) ? targetEntityCode : sourceEntityCode!;
        var source = sourceDb ?? dest;
        var partner = BusinessPartnerMapper.FromMasterRecord(record, source, sourceEntity);
        var control = ResolveControlAccount(partner, targetEntityCode);

        try
        {
            var result = await WithWriteTx(dest, async (conn, tx) =>
            {
                var partyTable = await ResolvePartyTable(conn, tx, targetEntityCode, cancellationToken).ConfigureAwait(false);
                if (partyTable is null)
                    return Fail($"Party table not found on {dest.DisplayName}.");
                if (!await CodeExists(conn, tx, partyTable, code, cancellationToken).ConfigureAwait(false))
                    return Fail($"Party {code} was not found on {dest.DisplayName}.");

                await UpdateHeader(conn, tx, partyTable, code, control, partner, txnStamp, cancellationToken)
                    .ConfigureAwait(false);

                var branchTable = await ResolveBranchTable(conn, tx, targetEntityCode, cancellationToken).ConfigureAwait(false);
                if (branchTable is not null)
                    await UpsertBranch(conn, tx, branchTable, code, partner, targetEntityCode, cancellationToken)
                        .ConfigureAwait(false);

                return Ok(code, "Updated via Firebird fallback after API timeout.");
            }, cancellationToken).ConfigureAwait(false);

            await DiscardPool(dest, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird party update failed for {Entity} {Code} on {Db}",
                targetEntityCode, code, dest.DbCode);
            return Fail($"Firebird update failed: {ex.Message}");
        }
    }

    public async Task<WriteResult> DeleteAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!PartnerSyncRules.IsPartyEntity(targetEntityCode))
            return Fail($"Firebird party delete does not support {targetEntityCode}.");

        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Fail("Party code is required.");

        try
        {
            var result = await WithWriteTx(dest, async (conn, tx) =>
            {
                var partyTable = await ResolvePartyTable(conn, tx, targetEntityCode, cancellationToken).ConfigureAwait(false);
                if (partyTable is null)
                    return Fail($"Party table not found on {dest.DisplayName}.");

                if (!await CodeExists(conn, tx, partyTable, key, cancellationToken).ConfigureAwait(false))
                    return Ok(key, "Already absent on destination (Firebird).");

                if (await PartyHasOutstanding(conn, tx, partyTable, key, cancellationToken).ConfigureAwait(false))
                    return Fail($"Party {key} has outstanding balance on {dest.DisplayName}; delete blocked.");

                var branchTable = await ResolveBranchTable(conn, tx, targetEntityCode, cancellationToken).ConfigureAwait(false);
                if (branchTable is not null)
                    await DeleteByCode(conn, tx, branchTable, key, cancellationToken).ConfigureAwait(false);

                await DeleteByCode(conn, tx, partyTable, key, cancellationToken).ConfigureAwait(false);

                if (await TableExists(conn, tx, "GL_ACC", cancellationToken).ConfigureAwait(false)
                    && await CodeExists(conn, tx, "GL_ACC", key, cancellationToken).ConfigureAwait(false)
                    && !await GlHasActivity(conn, tx, key, cancellationToken).ConfigureAwait(false))
                {
                    await DeleteByCode(conn, tx, "GL_ACC", key, cancellationToken).ConfigureAwait(false);
                }

                return Ok(key, "Deleted via Firebird fallback after API timeout.");
            }, cancellationToken).ConfigureAwait(false);

            await DiscardPool(dest, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird party delete failed for {Entity} {Code} on {Db}",
                targetEntityCode, key, dest.DbCode);
            return Fail($"Firebird delete failed: {ex.Message}");
        }
    }

    private async Task DiscardPool(RegisteredDatabase dest, CancellationToken ct)
    {
        try { await _factory.DiscardPooledConnectionAsync(dest, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not discard pooled connection for {Db}", dest.DbCode); }
    }

    private static string ResolveControlAccount(BusinessPartner partner, string targetEntityCode)
    {
        var isCustomer = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase);
        var control = isCustomer ? "300-000" : "400-000";
        if (string.Equals(partner.SourceType, targetEntityCode, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(partner.ControlAccount))
            control = partner.ControlAccount.Trim();
        return control;
    }

    private static async Task UpdateHeader(
        FbConnection conn, FbTransaction tx, string table, string code, string control,
        BusinessPartner partner, string txnStamp, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
        var remark = BuildRemark(partner.Remark, txnStamp);
        var values = HeaderValues(code, control, partner, remark);
        await UpdateRow(conn, tx, table, cols, values, code, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> HeaderValues(
        string code, string control, BusinessPartner partner, string remark) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CONTROLACCOUNT"] = control,
            ["COMPANYNAME"] = NullIfEmpty(partner.CompanyName) ?? code,
            ["COMPANYNAME2"] = NullIfEmpty(partner.CompanyName2),
            ["COMPANYCATEGORY"] = NullIfEmpty(partner.CompanyCategory) ?? "----",
            ["AREA"] = NullIfEmpty(partner.Area) ?? "----",
            ["AGENT"] = NullIfEmpty(partner.Agent) ?? "----",
            ["BIZNATURE"] = NullIfEmpty(partner.BizNature),
            ["CREDITTERM"] = NullIfEmpty(partner.CreditTerm) ?? "30 Days",
            ["CREDITLIMIT"] = ParseDecimal(partner.CreditLimit),
            ["OVERDUELIMIT"] = ParseDecimal(partner.OverdueLimit),
            ["STATEMENTTYPE"] = NullIfEmpty(partner.StatementType) ?? "O",
            ["CURRENCYCODE"] = NullIfEmpty(partner.CurrencyCode) ?? "----",
            ["ALLOWEXCEEDCREDITLIMIT"] = partner.AllowExceedCreditLimit ? 1 : 0,
            ["ADDPDCTOCRLIMIT"] = partner.AddPdcToCrLimit ? 1 : 0,
            ["AGINGON"] = NullIfEmpty(partner.AgingOn) ?? "I",
            ["STATUS"] = NullIfEmpty(partner.Status) ?? "A",
            ["PRICETAG"] = NullIfEmpty(partner.PriceTag),
            ["TAX"] = NullIfEmpty(partner.Tax),
            ["TAXEXEMPTNO"] = NullIfEmpty(partner.TaxExemptNo),
            ["BRN"] = NullIfEmpty(partner.Brn),
            ["BRN2"] = NullIfEmpty(partner.Brn2),
            ["GSTNO"] = NullIfEmpty(partner.GstNo),
            ["SALESTAXNO"] = NullIfEmpty(partner.SalesTaxNo),
            ["SERVICETAXNO"] = NullIfEmpty(partner.ServiceTaxNo),
            ["TIN"] = NullIfEmpty(partner.Tin),
            ["IDTYPE"] = partner.IdType,
            ["IDNO"] = NullIfEmpty(partner.IdNo),
            ["SIC"] = NullIfEmpty(partner.Sic) ?? "00000",
            ["SUBMISSIONTYPE"] = partner.SubmissionType,
            ["IRBM_CLASSIFICATION"] = NullIfEmpty(partner.IrbmClassification) ?? "022",
            ["BUSINESSUNIT"] = NullIfEmpty(partner.BusinessUnit),
            ["TAXAREA"] = NullIfEmpty(partner.TaxArea),
            ["REMARK"] = NullIfEmpty(remark),
            ["NOTE"] = NullIfEmpty(partner.Note),
            ["UDF_EMAIL01"] = NullIfEmpty(partner.UdfEmail01),
            ["UDF_LOCATION"] = NullIfEmpty(partner.UdfLocation),
            ["UDF_DEPARTMENT01"] = NullIfEmpty(partner.UdfDepartment01)
        };

    private static async Task UpsertBranch(
        FbConnection conn, FbTransaction tx, string table, string code, BusinessPartner partner,
        string targetEntityCode, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
        var branch = partner.Branches.Count > 0 ? partner.Branches[0] : new PartnerBranch();
        var values = BranchValues(code, branch);

        if (await CodeExists(conn, tx, table, code, ct).ConfigureAwait(false))
        {
            await UpdateRow(conn, tx, table, cols, values, code, ct).ConfigureAwait(false);
            return;
        }

        var genName = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
            ? "AR_CUSTOMERBRANCH" : "AP_SUPPLIERBRANCH";
        values["DTLKEY"] = await NextGen(conn, tx, genName, ct).ConfigureAwait(false);
        await InsertRow(conn, tx, table, cols, values, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> BranchValues(string code, PartnerBranch branch) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CODE"] = code,
            ["BRANCHTYPE"] = NullIfEmpty(branch.BranchType) ?? "B",
            ["BRANCHNAME"] = NullIfEmpty(branch.BranchName) ?? "BILLING",
            ["ADDRESS1"] = NullIfEmpty(branch.Address1),
            ["ADDRESS2"] = NullIfEmpty(branch.Address2),
            ["ADDRESS3"] = NullIfEmpty(branch.Address3),
            ["ADDRESS4"] = NullIfEmpty(branch.Address4),
            ["POSTCODE"] = NullIfEmpty(branch.Postcode),
            ["CITY"] = NullIfEmpty(branch.City),
            ["STATE"] = NullIfEmpty(branch.State),
            ["COUNTRY"] = NullIfEmpty(branch.Country) ?? "MY",
            ["ATTENTION"] = NullIfEmpty(branch.Attention),
            ["PHONE1"] = NullIfEmpty(branch.Phone1),
            ["PHONE2"] = NullIfEmpty(branch.Phone2),
            ["MOBILE"] = NullIfEmpty(branch.Mobile),
            ["FAX1"] = NullIfEmpty(branch.Fax1),
            ["FAX2"] = NullIfEmpty(branch.Fax2),
            ["EMAIL"] = NullIfEmpty(branch.Email)
        };

    private static async Task UpdateRow(
        FbConnection conn, FbTransaction tx, string table, IReadOnlyCollection<string> cols,
        Dictionary<string, object?> candidates, string code, CancellationToken ct)
    {
        var pairs = candidates.Where(kv => Has(cols, kv.Key) && kv.Value is not null
            && !kv.Key.Equals("CODE", StringComparison.OrdinalIgnoreCase)
            && !kv.Key.Equals("DTLKEY", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pairs.Count == 0)
            return;

        var sets = new StringBuilder();
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["C"] = code };
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sets.Append(", ");
            sets.Append(pairs[i].Key).Append("=@P").Append(i);
            args["P" + i] = pairs[i].Value;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {table} SET {sets} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertRow(
        FbConnection conn, FbTransaction tx, string table, IReadOnlyCollection<string> cols,
        Dictionary<string, object?> candidates, CancellationToken ct)
    {
        var pairs = candidates.Where(kv => Has(cols, kv.Key) && kv.Value is not null).ToList();
        if (pairs.Count == 0)
            throw new InvalidOperationException($"No insertable columns for {table}.");

        var colNames = pairs.Select(p => p.Key).ToList();
        var paramNames = colNames.Select((_, i) => $"P{i}").ToList();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {table} ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames.Select(p => "@" + p))})";
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Count; i++)
            args[paramNames[i]] = pairs[i].Value;
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteByCode(
        FbConnection conn, FbTransaction tx, string table, string code, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> PartyHasOutstanding(
        FbConnection conn, FbTransaction tx, string table, string code, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
        if (!Has(cols, "OUTSTANDING"))
            return false;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT FIRST 1 OUTSTANDING FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null or DBNull)
            return false;
        return Convert.ToDecimal(raw, CultureInfo.InvariantCulture) != 0m;
    }

    private static async Task<bool> GlHasActivity(
        FbConnection conn, FbTransaction tx, string code, CancellationToken ct)
    {
        var dockeyCmd = conn.CreateCommand();
        dockeyCmd.Transaction = tx;
        dockeyCmd.CommandText = "SELECT FIRST 1 DOCKEY FROM GL_ACC WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(dockeyCmd, new Dictionary<string, object?> { ["C"] = code });
        var dockeyRaw = await dockeyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        await dockeyCmd.DisposeAsync().ConfigureAwait(false);
        if (dockeyRaw is null or DBNull)
            return false;
        var dockey = Convert.ToInt32(dockeyRaw, CultureInfo.InvariantCulture);

        await using var childCmd = conn.CreateCommand();
        childCmd.Transaction = tx;
        childCmd.CommandText = "SELECT COUNT(*) FROM GL_ACC WHERE PARENT=@D";
        CentralDb.Bind(childCmd, new Dictionary<string, object?> { ["D"] = dockey });
        if (Convert.ToInt32(await childCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0)
            return true;

        if (!await TableExists(conn, tx, "GL_TRANS", ct).ConfigureAwait(false))
            return false;
        await using var transCmd = conn.CreateCommand();
        transCmd.Transaction = tx;
        transCmd.CommandText = "SELECT COUNT(*) FROM GL_TRANS WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(transCmd, new Dictionary<string, object?> { ["C"] = code });
        return Convert.ToInt32(await transCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private async Task<T> WithWriteTx<T>(
        RegisteredDatabase db, Func<FbConnection, FbTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        var options = new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Write
                                | FbTransactionBehavior.ReadCommitted
                                | FbTransactionBehavior.RecVersion
        };
        await using var tx = await conn.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await work(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            throw;
        }
    }

    private static async Task<string?> ResolvePartyTable(
        FbConnection conn, FbTransaction tx, string entityCode, CancellationToken ct)
    {
        if (string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase))
            return await TableExists(conn, tx, "AR_CUSTOMER", ct).ConfigureAwait(false) ? "AR_CUSTOMER" : null;
        foreach (var t in new[] { "AP_SUPPLIER", "AP_VENDOR", "CREDITOR" })
        {
            if (await TableExists(conn, tx, t, ct).ConfigureAwait(false))
                return t;
        }
        return null;
    }

    private static async Task<string?> ResolveBranchTable(
        FbConnection conn, FbTransaction tx, string entityCode, CancellationToken ct)
    {
        var candidates = string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
            ? new[] { "AR_CUSTOMERBRANCH", "AR_CUSTBRANCH", "AR_BRANCH" }
            : new[] { "AP_SUPPLIERBRANCH", "AP_SUPPBRANCH", "AP_BRANCH", "AP_VENDORBRANCH" };
        foreach (var table in candidates)
        {
            if (await TableExists(conn, tx, table, ct).ConfigureAwait(false))
                return table;
        }
        return null;
    }

    private static async Task<bool> CodeExists(
        FbConnection conn, FbTransaction tx, string table, string code, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT FIRST 1 1 FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is not null and not DBNull;
    }

    private static async Task<long> NextGen(FbConnection conn, FbTransaction tx, string generator, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT GEN_ID({generator}, 1) FROM RDB$DATABASE";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExists(FbConnection conn, FbTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME)=@N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private static async Task<List<string>> Columns(FbConnection conn, FbTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT TRIM(RDB$FIELD_NAME) AS C FROM RDB$RELATION_FIELDS WHERE TRIM(RDB$RELATION_NAME)=@N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Convert.ToString(r["C"])?.Trim() ?? "");
        return list;
    }

    private static bool Has(IReadOnlyCollection<string> cols, string name) =>
        cols.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string BuildRemark(string? remark, string txnStamp)
    {
        var baseRemark = (remark ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txnStamp))
            return baseRemark;
        return string.IsNullOrWhiteSpace(baseRemark) ? $"SYNC|{txnStamp}" : $"{baseRemark} | SYNC|{txnStamp}";
    }

    private static object ParseDecimal(string? raw) =>
        decimal.TryParse((raw ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static string? NullIfEmpty(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 ? null : v;
    }

    private static WriteResult Ok(string code, string message) =>
        new() { Success = true, DocNo = code, DocKey = code, Message = message };

    private static WriteResult Fail(string message) =>
        new() { Success = false, Message = message };
}
