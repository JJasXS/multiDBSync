using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Audit;

public interface IAuditService
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
    Task<List<AuditEntry>> ListAsync(int take = 200, CancellationToken cancellationToken = default);
}

public sealed class AuditService : IAuditService
{
    private readonly CentralDb _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(CentralDb db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return;
        try
        {
            var id = await _db.NextIdAsync("GEN_SYNC_AUDIT", cancellationToken).ConfigureAwait(false);
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_AUDIT (AUDITID, ATUTC, USEREMAIL, ACTION, DBID, ENTITY, RECORDID, RESULT, IPADDR, SESSIONID, REQUESTID, BEFOREJSON, AFTERJSON)
VALUES (@AUDITID, CURRENT_TIMESTAMP, @USEREMAIL, @ACTION, @DBID, @ENTITY, @RECORDID, @RESULT, @IPADDR, @SESSIONID, @REQUESTID, @BEFOREJSON, @AFTERJSON)",
                new
                {
                    AUDITID = id,
                    USEREMAIL = entry.UserEmail,
                    ACTION = entry.Action,
                    DBID = (object?)entry.DbId ?? DBNull.Value,
                    ENTITY = entry.Entity,
                    RECORDID = entry.RecordId,
                    RESULT = entry.Result,
                    IPADDR = entry.Ip,
                    SESSIONID = entry.SessionId,
                    REQUESTID = entry.RequestId,
                    BEFOREJSON = CredentialRedactor.Redact(entry.BeforeJson),
                    AFTERJSON = CredentialRedactor.Redact(entry.AfterJson)
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry {Action}", entry.Action);
        }
    }

    public Task<List<AuditEntry>> ListAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<AuditEntry>());
        return _db.QueryAsync($"SELECT FIRST {Math.Clamp(take, 1, 500)} * FROM SYNC_AUDIT ORDER BY AUDITID DESC", Map, cancellationToken: cancellationToken);
    }

    private static AuditEntry Map(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        AuditId = CentralDb.Int(r, "AUDITID"),
        AtUtc = CentralDb.TimeReq(r, "ATUTC"),
        UserEmail = CentralDb.Str(r, "USEREMAIL"),
        Action = CentralDb.Str(r, "ACTION"),
        DbId = CentralDb.IntN(r, "DBID"),
        Entity = CentralDb.Str(r, "ENTITY"),
        RecordId = CentralDb.Str(r, "RECORDID"),
        Result = CentralDb.Str(r, "RESULT"),
        Ip = CentralDb.Str(r, "IPADDR"),
        SessionId = CentralDb.Str(r, "SESSIONID"),
        RequestId = CentralDb.Str(r, "REQUESTID"),
        BeforeJson = CentralDb.Str(r, "BEFOREJSON"),
        AfterJson = CentralDb.Str(r, "AFTERJSON"),
    };
}

public interface IErrorLogService
{
    Task<int> WriteAsync(ErrorEntry entry, CancellationToken cancellationToken = default);
    Task<List<ErrorEntry>> ListAsync(bool unresolvedOnly = false, int take = 200, CancellationToken cancellationToken = default);
    Task ResolveAsync(int errorId, CancellationToken cancellationToken = default);
}

public sealed class ErrorLogService : IErrorLogService
{
    private readonly CentralDb _db;
    private readonly ILogger<ErrorLogService> _logger;

    public ErrorLogService(CentralDb db, ILogger<ErrorLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> WriteAsync(ErrorEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return 0;
        try
        {
            var id = await _db.NextIdAsync("GEN_SYNC_ERROR", cancellationToken).ConfigureAwait(false);
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_ERROR (ERRORID, ATUTC, TXNPK, DBID, ERRORTYPE, USERMESSAGE, STACKTRACE, RETRYCOUNT, RESOLVED)
VALUES (@ERRORID, CURRENT_TIMESTAMP, @TXNPK, @DBID, @ERRORTYPE, @USERMESSAGE, @STACKTRACE, @RETRYCOUNT, 0)",
                new
                {
                    ERRORID = id,
                    TXNPK = (object?)entry.TxnPk ?? DBNull.Value,
                    DBID = (object?)entry.DbId ?? DBNull.Value,
                    ERRORTYPE = entry.ErrorType,
                    USERMESSAGE = entry.UserMessage,
                    STACKTRACE = entry.StackTrace,
                    RETRYCOUNT = entry.RetryCount
                }, cancellationToken).ConfigureAwait(false);
            return (int)id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write error log");
            return 0;
        }
    }

    public Task<List<ErrorEntry>> ListAsync(bool unresolvedOnly = false, int take = 200, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<ErrorEntry>());
        var where = unresolvedOnly ? "WHERE RESOLVED = 0" : "";
        return _db.QueryAsync($"SELECT FIRST {Math.Clamp(take, 1, 500)} * FROM SYNC_ERROR {where} ORDER BY ERRORID DESC", Map, cancellationToken: cancellationToken);
    }

    public Task ResolveAsync(int errorId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_ERROR SET RESOLVED = 1 WHERE ERRORID = @ERRORID", new { ERRORID = errorId }, cancellationToken);

    private static ErrorEntry Map(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        ErrorId = CentralDb.Int(r, "ERRORID"),
        AtUtc = CentralDb.TimeReq(r, "ATUTC"),
        TxnPk = CentralDb.IntN(r, "TXNPK"),
        DbId = CentralDb.IntN(r, "DBID"),
        ErrorType = CentralDb.Str(r, "ERRORTYPE"),
        UserMessage = CentralDb.Str(r, "USERMESSAGE"),
        StackTrace = CentralDb.Str(r, "STACKTRACE"),
        RetryCount = CentralDb.Int(r, "RETRYCOUNT"),
        Resolved = CentralDb.Flag(r, "RESOLVED")
    };
}
