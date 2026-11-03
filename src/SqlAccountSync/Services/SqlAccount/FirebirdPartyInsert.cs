using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount;

/// <summary>
/// Direct Firebird INSERT for customer/supplier when SQL Acc API create times out.
/// Creates GL child + party header + one BILLING branch in a single read-write transaction.
/// </summary>
public interface IFirebirdPartyInsert
{
    Task<WriteResult> CreateAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        MasterRecord record,
        string txnStamp,
        RegisteredDatabase? sourceDb,
        string? sourceEntityCode,
        CancellationToken cancellationToken = default);
}

public sealed class FirebirdPartyInsert : IFirebirdPartyInsert
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdPartyInsert> _logger;

    public FirebirdPartyInsert(CompanyConnectionFactory factory, ILogger<FirebirdPartyInsert> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<WriteResult> CreateAsync(
        RegisteredDatabase dest,
        string targetEntityCode,
        MasterRecord record,
        string txnStamp,
        RegisteredDatabase? sourceDb,
        string? sourceEntityCode,
        CancellationToken cancellationToken = default)
    {
        if (!PartnerSyncRules.IsPartyEntity(targetEntityCode))
            return Fail($"Firebird party insert does not support {targetEntityCode}.");

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

                if (await CodeExists(conn, tx, partyTable, code, cancellationToken).ConfigureAwait(false))
                    return Ok(code, "Already present on destination (Firebird).");

                var controlDocKey = await ResolveControlDocKey(conn, tx, control, cancellationToken).ConfigureAwait(false);
                if (controlDocKey <= 0)
                    return Fail($"Control account '{control}' was not found on {dest.DisplayName}.");

                var glErr = await EnsureGlChild(conn, tx, code, partner.CompanyName, controlDocKey, targetEntityCode, cancellationToken)
                    .ConfigureAwait(false);
                if (glErr is not null)
                    return Fail(glErr);

                await InsertHeader(conn, tx, partyTable, code, control, partner, txnStamp, cancellationToken)
                    .ConfigureAwait(false);

                var branchTable = await ResolveBranchTable(conn, tx, targetEntityCode, cancellationToken).ConfigureAwait(false);
                if (branchTable is not null)
                {
                    await InsertBranch(conn, tx, branchTable, code, partner, targetEntityCode, cancellationToken)
                        .ConfigureAwait(false);
                }

                return Ok(code, "Created via Firebird fallback after API timeout.");
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                await _factory.DiscardPooledConnectionAsync(dest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception discardEx)
            {
                _logger.LogDebug(discardEx, "Could not discard pooled connection for {Db}", dest.DbCode);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird party insert failed for {Entity} {Code} on {Db}",
                targetEntityCode, code, dest.DbCode);
            return Fail($"Firebird insert failed: {ex.Message}");
        }
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

    private async Task<string?> EnsureGlChild(
        FbConnection conn, FbTransaction tx, string code, string companyName, int parentDocKey,
        string targetEntityCode, CancellationToken ct)
    {
        if (!await TableExists(conn, tx, "GL_ACC", ct).ConfigureAwait(false))
            return "GL_ACC table not found.";
        if (await CodeExists(conn, tx, "GL_ACC", code, ct).ConfigureAwait(false))
            return null;

        var cols = await Columns(conn, tx, "GL_ACC", ct).ConfigureAwait(false);
        if (!Has(cols, "DOCKEY") || !Has(cols, "CODE") || !Has(cols, "PARENT"))
            return "GL_ACC is missing DOCKEY/CODE/PARENT.";

        var dockey = await NextGen(conn, tx, "GL_ACC", ct).ConfigureAwait(false);
        var isCustomer = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOCKEY"] = dockey,
            ["PARENT"] = parentDocKey,
            ["CODE"] = code,
            ["DESCRIPTION"] = string.IsNullOrWhiteSpace(companyName) ? code : companyName.Trim(),
            ["ACCTYPE"] = "CA",
            ["SPECIALACCTYPE"] = isCustomer ? "DR" : "CR",
            ["CASHFLOWTYPE"] = 0
        };
        await InsertRow(conn, tx, "GL_ACC", cols, values, ct).ConfigureAwait(false);
        return null;
    }

    private async Task InsertHeader(
        FbConnection conn, FbTransaction tx, string table, string code, string control,
        BusinessPartner partner, string txnStamp, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
        var remark = BuildRemark(partner.Remark, txnStamp);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CODE"] = code,
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
        await InsertRow(conn, tx, table, cols, values, ct).ConfigureAwait(false);
    }

    private async Task InsertBranch(
        FbConnection conn, FbTransaction tx, string table, string code, BusinessPartner partner,
        string targetEntityCode, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
        var branch = partner.Branches.Count > 0 ? partner.Branches[0] : new PartnerBranch();
        var genName = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
            ? "AR_CUSTOMERBRANCH"
            : "AP_SUPPLIERBRANCH";
        var dtlKey = await NextGen(conn, tx, genName, ct).ConfigureAwait(false);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DTLKEY"] = dtlKey,
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
        await InsertRow(conn, tx, table, cols, values, ct).ConfigureAwait(false);
    }

    private static async Task InsertRow(
        FbConnection conn, FbTransaction tx, string table, IReadOnlyCollection<string> cols,
        Dictionary<string, object?> candidates, CancellationToken ct)
    {
        var pairs = candidates
            .Where(kv => Has(cols, kv.Key) && kv.Value is not null)
            .ToList();
        if (pairs.Count == 0)
            throw new InvalidOperationException($"No insertable columns for {table}.");

        var colNames = pairs.Select(p => p.Key).ToList();
        var paramNames = colNames.Select((_, i) => $"P{i}").ToList();
        var sql = $"INSERT INTO {table} ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames.Select(p => "@" + p))})";
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Count; i++)
            args[paramNames[i]] = pairs[i].Value;
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static async Task<int> ResolveControlDocKey(
        FbConnection conn, FbTransaction tx, string controlCode, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT FIRST 1 DOCKEY FROM GL_ACC WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = controlCode });
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
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
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
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

    private static object ParseDecimal(string? raw)
    {
        if (decimal.TryParse((raw ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
    }

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
