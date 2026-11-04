using System.Globalization;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Services.SqlAccount;

public interface IFirebirdAccountMutate
{
    Task<WriteResult> UpdateAsync(
        RegisteredDatabase dest,
        MasterRecord record,
        CancellationToken cancellationToken = default);

    Task<WriteResult> DeleteAsync(
        RegisteredDatabase dest,
        string code,
        CancellationToken cancellationToken = default);
}

public sealed class FirebirdAccountMutate : IFirebirdAccountMutate
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdAccountMutate> _logger;

    public FirebirdAccountMutate(CompanyConnectionFactory factory, ILogger<FirebirdAccountMutate> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<WriteResult> UpdateAsync(
        RegisteredDatabase dest,
        MasterRecord record,
        CancellationToken cancellationToken = default)
    {
        var code = (record.Code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Account code is required.");

        var row = GlAccountMapper.FromMasterRecord(record);
        var destParent = 0;
        if (record.Fields.TryGetValue(GlAccountMapper.FieldDestParent, out var dp)
            && int.TryParse(dp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            destParent = parsed;

        try
        {
            var result = await WithWriteTx(dest, async (conn, tx) =>
            {
                if (!await TableExists(conn, tx, "GL_ACC", cancellationToken).ConfigureAwait(false))
                    return Fail("GL_ACC table not found.");
                if (!await CodeExists(conn, tx, code, cancellationToken).ConfigureAwait(false))
                    return Fail($"Account {code} was not found on {dest.DisplayName}.");

                var cols = await Columns(conn, tx, cancellationToken).ConfigureAwait(false);
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PARENT"] = destParent,
                    ["DESCRIPTION"] = string.IsNullOrWhiteSpace(row.Description) ? code : row.Description.Trim(),
                    ["DESCRIPTION2"] = (row.Description2 ?? "").Trim(),
                    ["ACCTYPE"] = (row.AccType ?? "").Trim(),
                    ["SPECIALACCTYPE"] = (row.SpecialAccType ?? "").Trim(),
                    ["TAX"] = (row.Tax ?? "").Trim(),
                    ["CASHFLOWTYPE"] = row.CashFlowType,
                    ["SIC"] = (row.Sic ?? "").Trim()
                };
                await UpdateRow(conn, tx, cols, values, code, cancellationToken).ConfigureAwait(false);
                var dockey = await ResolveDocKey(conn, tx, code, cancellationToken).ConfigureAwait(false);
                return Ok(code, dockey, "Updated via Firebird fallback after API timeout.");
            }, cancellationToken).ConfigureAwait(false);

            await DiscardPool(dest, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird account update failed for {Code} on {Db}", code, dest.DbCode);
            return Fail($"Firebird update failed: {ex.Message}");
        }
    }

    public async Task<WriteResult> DeleteAsync(
        RegisteredDatabase dest,
        string code,
        CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Fail("Account code is required.");

        try
        {
            var result = await WithWriteTx(dest, async (conn, tx) =>
            {
                if (!await TableExists(conn, tx, "GL_ACC", cancellationToken).ConfigureAwait(false))
                    return Fail("GL_ACC table not found.");
                if (!await CodeExists(conn, tx, key, cancellationToken).ConfigureAwait(false))
                    return Ok(key, 0, "Already absent on destination (Firebird).");

                if (await GlHasActivity(conn, tx, key, cancellationToken).ConfigureAwait(false))
                    return Fail($"Account {key} has postings or child accounts on {dest.DisplayName}; delete blocked.");

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM GL_ACC WHERE UPPER(TRIM(CODE))=UPPER(@C)";
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return Ok(key, 0, "Deleted via Firebird fallback after API timeout.");
            }, cancellationToken).ConfigureAwait(false);

            await DiscardPool(dest, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebird account delete failed for {Code} on {Db}", key, dest.DbCode);
            return Fail($"Firebird delete failed: {ex.Message}");
        }
    }

    private async Task DiscardPool(RegisteredDatabase dest, CancellationToken ct)
    {
        try { await _factory.DiscardPooledConnectionAsync(dest, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not discard pooled connection for {Db}", dest.DbCode); }
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

    private static async Task UpdateRow(
        FbConnection conn, FbTransaction tx, IReadOnlyCollection<string> cols,
        Dictionary<string, object?> candidates, string code, CancellationToken ct)
    {
        var pairs = candidates.Where(kv => Has(cols, kv.Key) && kv.Value is not null).ToList();
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
        cmd.CommandText = $"UPDATE GL_ACC SET {sets} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> GlHasActivity(
        FbConnection conn, FbTransaction tx, string code, CancellationToken ct)
    {
        var dockey = await ResolveDocKey(conn, tx, code, ct).ConfigureAwait(false);
        if (dockey <= 0)
            return false;

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

    private static async Task<bool> CodeExists(FbConnection conn, FbTransaction tx, string code, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT FIRST 1 1 FROM GL_ACC WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is not null and not DBNull;
    }

    private static async Task<int> ResolveDocKey(FbConnection conn, FbTransaction tx, string code, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT FIRST 1 DOCKEY FROM GL_ACC WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExists(FbConnection conn, FbTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME)=@N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private static async Task<List<string>> Columns(FbConnection conn, FbTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT TRIM(RDB$FIELD_NAME) AS C FROM RDB$RELATION_FIELDS WHERE TRIM(RDB$RELATION_NAME)='GL_ACC'";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Convert.ToString(r["C"])?.Trim() ?? "");
        return list;
    }

    private static bool Has(IReadOnlyCollection<string> cols, string name) =>
        cols.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static WriteResult Ok(string code, int dockey, string message) =>
        new()
        {
            Success = true,
            DocNo = code,
            DocKey = dockey > 0 ? dockey.ToString(CultureInfo.InvariantCulture) : code,
            Message = message
        };

    private static WriteResult Fail(string message) =>
        new() { Success = false, Message = message };
}
