using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Data;

/// <summary>
/// Logs-page data access. Reads use SELECT-only SQL (read-only Firebird txn when remote).
/// Allowed writes: resolve error, queue transaction retry on the active Logs database.
/// </summary>
public interface ILogsReadGateway
{
    bool IsRemote { get; }
    bool IsConfigured { get; }
    string DescribeSource();
    Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken cancellationToken = default);
    Task<List<T>> QueryAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default);
    Task ResolveErrorAsync(int errorId, CancellationToken cancellationToken = default);
    Task QueueTxnRetryAsync(
        SyncTransaction txn,
        string payloadJson,
        string idemKey,
        string user,
        int maxAttempts,
        CancellationToken cancellationToken = default);
}

public sealed class LogsReadGateway : ILogsReadGateway
{
    private readonly CentralDb _local;
    private readonly LogsRemoteReadOptions _logs;
    private readonly CentralRegistryOptions _registry;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<LogsReadGateway> _logger;
    private readonly bool _remote;

    public LogsReadGateway(
        CentralDb local,
        IOptions<LogsRemoteReadOptions> logs,
        IOptions<CentralRegistryOptions> registry,
        ISecretResolver secrets,
        ILogger<LogsReadGateway> logger)
    {
        _local = local;
        _logs = logs.Value;
        _registry = registry.Value;
        _secrets = secrets;
        _logger = logger;
        _remote = ResolveRemote();
        if (_remote)
            _logger.LogInformation("Logs page uses remote SYNC.FDB at {Desc}", DescribeSource());
        else if (_logs.Enabled)
            _logger.LogWarning("LogsRemoteRead is Enabled but no remote target is configured; Logs stays on local CentralDb.");
    }

    public bool IsRemote => _remote;

    public bool IsConfigured => _remote || _local.IsConfigured;

    public string DescribeSource()
    {
        if (!_remote)
            return "local CentralDb";
        var (host, port, path, _, _) = ResolveTarget();
        return $"{host}:{port} {path}";
    }

    public Task ResolveErrorAsync(int errorId, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE SYNC_ERROR SET RESOLVED = 1 WHERE ERRORID = @ERRORID";
        if (!_remote)
            return _local.ExecuteAsync(sql, new { ERRORID = errorId }, cancellationToken);
        return ExecuteRemoteAsync(sql, new { ERRORID = errorId }, cancellationToken);
    }

    public Task QueueTxnRetryAsync(
        SyncTransaction txn,
        string payloadJson,
        string idemKey,
        string user,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (!_remote)
            return QueueTxnRetryLocalAsync(txn, payloadJson, idemKey, user, maxAttempts, cancellationToken);
        return QueueTxnRetryRemoteAsync(txn, payloadJson, idemKey, user, maxAttempts, cancellationToken);
    }

    private async Task QueueTxnRetryLocalAsync(
        SyncTransaction txn,
        string payloadJson,
        string idemKey,
        string user,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        await _local.ExecuteAsync(
            "UPDATE SYNC_TXN SET STATUS='RETRYING', RETRYCOUNT=RETRYCOUNT+1, UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK",
            new { TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
        var jobId = (int)await _local.NextIdAsync("GEN_SYNC_JOB", cancellationToken).ConfigureAwait(false);
        await InsertRetryJobAsync(
            async (sql, args) => { await _local.ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false); },
            jobId, txn, payloadJson, idemKey, user, maxAttempts).ConfigureAwait(false);
        var auditId = (int)await _local.NextIdAsync("GEN_SYNC_AUDIT", cancellationToken).ConfigureAwait(false);
        await _local.ExecuteAsync(@"
INSERT INTO SYNC_AUDIT (AUDITID, ATUTC, USEREMAIL, ACTION, DBID, ENTITY, RECORDID, RESULT, REQUESTID)
VALUES (@AUDITID, CURRENT_TIMESTAMP, @USEREMAIL, 'TXN_RETRY', @DBID, 'TXN', @RECORDID, 'QUEUED', @REQUESTID)",
            new
            {
                AUDITID = auditId,
                USEREMAIL = user ?? "",
                DBID = (object?)txn.DestDbId ?? DBNull.Value,
                RECORDID = txn.TxnId ?? "",
                REQUESTID = txn.TxnPk.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueTxnRetryRemoteAsync(
        SyncTransaction txn,
        string payloadJson,
        string idemKey,
        string user,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        await using var conn = await OpenRemoteAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                "UPDATE SYNC_TXN SET STATUS='RETRYING', RETRYCOUNT=RETRYCOUNT+1, UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK";
            CentralDb.Bind(upd, new { TXNPK = txn.TxnPk });
            await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var jobId = await NextIdOnAsync(conn, tx, "GEN_SYNC_JOB", cancellationToken).ConfigureAwait(false);
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = RetryJobInsertSql;
            CentralDb.Bind(ins, RetryJobArgs(jobId, txn, payloadJson, idemKey, user, maxAttempts));
            await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var auditId = await NextIdOnAsync(conn, tx, "GEN_SYNC_AUDIT", cancellationToken).ConfigureAwait(false);
        await using (var aud = conn.CreateCommand())
        {
            aud.Transaction = tx;
            aud.CommandText = @"
INSERT INTO SYNC_AUDIT (AUDITID, ATUTC, USEREMAIL, ACTION, DBID, ENTITY, RECORDID, RESULT, REQUESTID)
VALUES (@AUDITID, CURRENT_TIMESTAMP, @USEREMAIL, 'TXN_RETRY', @DBID, 'TXN', @RECORDID, 'QUEUED', @REQUESTID)";
            CentralDb.Bind(aud, new
            {
                AUDITID = auditId,
                USEREMAIL = user ?? "",
                DBID = (object?)txn.DestDbId ?? DBNull.Value,
                RECORDID = txn.TxnId ?? "",
                REQUESTID = txn.TxnPk.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            await aud.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string RetryJobInsertSql = @"
INSERT INTO SYNC_JOB (JOBID, JOBKIND, TXNPK, SOURCEDBID, DESTDBID, ENTITYCODE, PAYLOADJSON, STATUS, PRIORITY, ATTEMPTS, MAXATTEMPTS, NEXTRUNAT, DRYRUN, IDEMKEY, CREATEDAT, CREATEDBY)
VALUES (@JOBID, 'TXN', @TXNPK, @SOURCEDBID, @DESTDBID, @ENTITYCODE, @PAYLOADJSON, 'PENDING', @PRIORITY, 0, @MAXATTEMPTS, CURRENT_TIMESTAMP, 0, @IDEMKEY, CURRENT_TIMESTAMP, @CREATEDBY)";

    private static object RetryJobArgs(
        int jobId, SyncTransaction txn, string payloadJson, string idemKey, string user, int maxAttempts) => new
    {
        JOBID = jobId,
        TXNPK = txn.TxnPk,
        SOURCEDBID = txn.SourceDbId,
        DESTDBID = txn.DestDbId,
        ENTITYCODE = "",
        PAYLOADJSON = payloadJson ?? "",
        PRIORITY = 0,
        MAXATTEMPTS = maxAttempts <= 0 ? 5 : maxAttempts,
        IDEMKEY = idemKey ?? "",
        CREATEDBY = user ?? ""
    };

    private static async Task InsertRetryJobAsync(
        Func<string, object?, Task> execute,
        int jobId,
        SyncTransaction txn,
        string payloadJson,
        string idemKey,
        string user,
        int maxAttempts)
    {
        await execute(RetryJobInsertSql, RetryJobArgs(jobId, txn, payloadJson, idemKey, user, maxAttempts))
            .ConfigureAwait(false);
    }

    private static async Task<int> NextIdOnAsync(
        FbConnection conn, FbTransaction tx, string generator, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT GEN_ID({generator}, 1) FROM RDB$DATABASE";
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken cancellationToken = default)
    {
        SqlGuard.EnsureSelectOnly(sql);
        if (!_remote)
            return _local.ScalarAsync(sql, args, cancellationToken);
        return ScalarRemoteAsync(sql, args, cancellationToken);
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default)
    {
        SqlGuard.EnsureSelectOnly(sql);
        if (!_remote)
            return await _local.QueryAsync(sql, map, args, cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenRemoteAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            new FbTransactionOptions { TransactionBehavior = FbTransactionBehavior.Read },
            cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(map(reader));
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }

    private async Task<object?> ScalarRemoteAsync(string sql, object? args, CancellationToken cancellationToken)
    {
        await using var conn = await OpenRemoteAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            new FbTransactionOptions { TransactionBehavior = FbTransactionBehavior.Read },
            cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ExecuteRemoteAsync(string sql, object? args, CancellationToken cancellationToken)
    {
        await using var conn = await OpenRemoteAsync(cancellationToken).ConfigureAwait(false);
        // Read-write txn — only used for ResolveErrorAsync (SYNC_ERROR.RESOLVED).
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FbConnection> OpenRemoteAsync(CancellationToken cancellationToken)
    {
        var conn = new FbConnection(await BuildRemoteConnectionStringAsync(cancellationToken).ConfigureAwait(false));
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private async Task<string> BuildRemoteConnectionStringAsync(CancellationToken cancellationToken)
    {
        var (host, port, path, user, secretRef) = ResolveTarget();
        var password = await _secrets.ResolveAsync(secretRef, cancellationToken).ConfigureAwait(false);
        return new FbConnectionStringBuilder
        {
            DataSource = host,
            Port = port,
            Database = path.Replace('\\', '/'),
            UserID = user,
            Password = password ?? "",
            Charset = string.IsNullOrWhiteSpace(_logs.Charset) ? "UTF8" : _logs.Charset,
            Dialect = (byte)Math.Clamp(_logs.Dialect, 1, 3),
            Pooling = false
        }.ToString();
    }

    private bool ResolveRemote()
    {
        if (!_logs.Enabled)
            return false;
        var (host, _, path, _, _) = ResolveTarget();
        return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path);
    }

    private (string Host, int Port, string Path, string User, string SecretRef) ResolveTarget()
    {
        if (_logs.UseCentralRegistry
            && _registry.IsLiveRequested
            && _registry.HasLiveTarget)
        {
            return (
                _registry.Host.Trim(),
                _registry.Port <= 0 ? 3050 : _registry.Port,
                _registry.Path.Trim(),
                string.IsNullOrWhiteSpace(_registry.User) ? "SYSDBA" : _registry.User.Trim(),
                string.IsNullOrWhiteSpace(_registry.PasswordSecretRef)
                    ? "env:CENTRAL_FB_MASTERKEY"
                    : _registry.PasswordSecretRef);
        }

        return (
            (_logs.Host ?? "").Trim(),
            _logs.Port <= 0 ? 3050 : _logs.Port,
            (_logs.Path ?? "").Trim(),
            string.IsNullOrWhiteSpace(_logs.User) ? "SYSDBA" : _logs.User.Trim(),
            string.IsNullOrWhiteSpace(_logs.PasswordSecretRef)
                ? "env:CENTRAL_FB_MASTERKEY"
                : _logs.PasswordSecretRef);
    }
}
