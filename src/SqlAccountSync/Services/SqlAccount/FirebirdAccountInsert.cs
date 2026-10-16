using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Services.SqlAccount;

/// <summary>
/// Direct Firebird INSERT for GL_ACC when SQL Acc API POST /account times out.
/// </summary>
public interface IFirebirdAccountInsert
{
    Task<WriteResult> CreateAsync(
        RegisteredDatabase dest,
        MasterRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class FirebirdAccountInsert : IFirebirdAccountInsert
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdAccountInsert> _logger;

    public FirebirdAccountInsert(CompanyConnectionFactory factory, ILogger<FirebirdAccountInsert> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<WriteResult> CreateAsync(
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

                if (await CodeExists(conn, tx, code, cancellationToken).ConfigureAwait(false))
                {
                    var existingKey = await ResolveDocKey(conn, tx, code, cancellationToken).ConfigureAwait(false);
                    return Ok(code, existingKey, "Already present on destination (Firebird).");
                }

                var cols = await Columns(conn, tx, "GL_ACC", cancellationToken).ConfigureAwait(false);
                if (!Has(cols, "DOCKEY") || !Has(cols, "CODE"))
                    return Fail("GL_ACC is missing DOCKEY/CODE.");

                var dockey = await NextGen(conn, tx, "GL_ACC", cancellationToken).ConfigureAwait(false);
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DOCKEY"] = dockey,
                    ["PARENT"] = destParent,
                    ["CODE"] = code,
                    ["DESCRIPTION"] = string.IsNullOrWhiteSpace(row.Description) ? code : row.Description.Trim(),
                    ["DESCRIPTION2"] = (row.Description2 ?? "").Trim(),
                    ["ACCTYPE"] = (row.AccType ?? "").Trim(),
                    ["SPECIALACCTYPE"] = (row.SpecialAccType ?? "").Trim(),
                    ["TAX"] = (row.Tax ?? "").Trim(),
                    ["CASHFLOWTYPE"] = row.CashFlowType,
                    ["SIC"] = (row.Sic ?? "").Trim()
                };
                await InsertRow(conn, tx, cols, values, cancellationToken).ConfigureAwait(false);
                return Ok(code, dockey, "Created via Firebird fallback after API timeout.");
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
            _logger.LogWarning(ex, "Firebird account insert failed for {Code} on {Db}", code, dest.DbCode);
            return Fail($"Firebird insert failed: {ex.Message}");
        }
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

    private static async Task InsertRow(
        FbConnection conn, FbTransaction tx, IReadOnlyCollection<string> cols,
        Dictionary<string, object?> candidates, CancellationToken ct)
    {
        // Keep empty strings for OleStr-like fields (API rejects null; FB often wants '').
        var pairs = candidates
            .Where(kv => Has(cols, kv.Key) && kv.Value is not null)
            .ToList();
        if (pairs.Count == 0)
            throw new InvalidOperationException("No insertable columns for GL_ACC.");

        var colNames = pairs.Select(p => p.Key).ToList();
        var paramNames = colNames.Select((_, i) => $"P{i}").ToList();
        var sql = $"INSERT INTO GL_ACC ({string.Join(", ", colNames)}) VALUES ({string.Join(", ", paramNames.Select(p => "@" + p))})";
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pairs.Count; i++)
            args[paramNames[i]] = pairs[i].Value;
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static WriteResult Ok(string code, long dockey, string message) =>
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
