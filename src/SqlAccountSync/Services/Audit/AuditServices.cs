using System.Globalization;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Audit;

public interface IAuditService
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
    Task<List<AuditEntry>> ListAsync(int take = 200, CancellationToken cancellationToken = default);
    Task<List<AuditListItem>> ListDetailedAsync(int take = 200, CancellationToken cancellationToken = default);
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
        return _db.QueryAsync(ListSql(take), Map, cancellationToken: cancellationToken);
    }

    public async Task<List<AuditListItem>> ListDetailedAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return [];

        var limit = Math.Clamp(take, 1, 500);
        var entries = await _db.QueryAsync(ListSql(limit), Map, cancellationToken: cancellationToken).ConfigureAwait(false);
        var jobs = await _db.QueryAsync($@"
SELECT FIRST {Math.Min(limit * 3, 500)} JOBID, JOBKIND, STATUS, ERRORMESSAGE, TXNPK, ENTITYCODE, CREATEDBY, CREATEDAT, PAYLOADJSON
FROM SYNC_JOB ORDER BY JOBID DESC", MapJob, cancellationToken: cancellationToken).ConfigureAwait(false);
        var txns = await _db.QueryAsync($@"
SELECT FIRST {Math.Min(limit * 2, 300)} TXNPK, TXNID, STATUS, ERRORMESSAGE
FROM SYNC_TXN ORDER BY TXNPK DESC", MapTxn, cancellationToken: cancellationToken).ConfigureAwait(false);

        var jobsById = jobs.ToDictionary(j => j.JobId);
        var txnById = txns.ToDictionary(t => t.TxnPk);
        var txnByTxnId = txns
            .Where(t => !string.IsNullOrWhiteSpace(t.TxnId))
            .GroupBy(t => t.TxnId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var jobsByTxnPk = jobs
            .Where(j => j.TxnPk > 0)
            .GroupBy(j => j.TxnPk)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.JobId).First());

        return entries.Select(e => ResolveItem(e, jobs, jobsById, jobsByTxnPk, txnById, txnByTxnId)).ToList();
    }

    private static string ListSql(int take) => $@"
SELECT FIRST {Math.Clamp(take, 1, 500)}
    AUDITID, ATUTC, USEREMAIL, ACTION, DBID, ENTITY, RECORDID, RESULT, IPADDR, SESSIONID, REQUESTID
FROM SYNC_AUDIT ORDER BY AUDITID DESC";

    private static AuditListItem ResolveItem(
        AuditEntry entry,
        List<AuditJobRow> jobs,
        Dictionary<int, AuditJobRow> jobsById,
        Dictionary<int, AuditJobRow> jobsByTxnPk,
        Dictionary<int, AuditTxnRow> txnById,
        Dictionary<string, AuditTxnRow> txnByTxnId)
    {
        var item = new AuditListItem { Entry = entry };
        var job = ResolveJob(entry, jobs, jobsById, jobsByTxnPk, txnById, txnByTxnId);
        var txn = ResolveTxn(entry, txnById, txnByTxnId, job);

        if (job is not null)
            item.JobId = job.JobId;
        if (txn is not null)
            item.TxnPk = txn.TxnPk;

        item.DisplayResult = ResolveDisplayResult(entry, job, txn);
        item.StatusDetail = ResolveStatusDetail(entry, job, txn);
        (item.LinkUrl, item.LinkLabel) = ResolveLink(entry, job, txn);
        return item;
    }

    private static AuditJobRow? ResolveJob(
        AuditEntry entry,
        List<AuditJobRow> jobs,
        Dictionary<int, AuditJobRow> jobsById,
        Dictionary<int, AuditJobRow> jobsByTxnPk,
        Dictionary<int, AuditTxnRow> txnById,
        Dictionary<string, AuditTxnRow> txnByTxnId)
    {
        if (IsTxnAction(entry.Action))
        {
            if (txnByTxnId.TryGetValue(entry.RecordId.Trim(), out var txn)
                && jobsByTxnPk.TryGetValue(txn.TxnPk, out var byTxn))
                return byTxn;

            if (TryParseJobId(entry.RequestId, out var txnPk)
                && txnById.ContainsKey(txnPk)
                && jobsByTxnPk.TryGetValue(txnPk, out var byTxnPk))
                return byTxnPk;

            return null;
        }

        if (TryParseJobId(entry.RequestId, out var requestJobId) && jobsById.TryGetValue(requestJobId, out var byRequest))
            return byRequest;

        if (string.Equals(entry.Action, "USER EXECUTED SYNC", StringComparison.OrdinalIgnoreCase)
            && TryParseJobId(entry.RecordId, out var recordJobId)
            && jobsById.TryGetValue(recordJobId, out var byRecord))
            return byRecord;

        if (!IsMasterEnqueueAction(entry.Action))
            return null;

        var code = entry.RecordId.Trim();
        if (code.Length == 0)
            return null;

        return jobs
            .Where(j => j.JobKind.StartsWith("MASTER", StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.EntityCode, entry.Entity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.CreatedBy, entry.UserEmail, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((j.CreatedAt - entry.AtUtc).TotalSeconds) <= 20
                && PayloadContainsCode(j.PayloadJson, code))
            .OrderBy(j => Math.Abs((j.CreatedAt - entry.AtUtc).TotalSeconds))
            .ThenByDescending(j => j.JobId)
            .FirstOrDefault();
    }

    private static AuditTxnRow? ResolveTxn(
        AuditEntry entry,
        Dictionary<int, AuditTxnRow> txnById,
        Dictionary<string, AuditTxnRow> txnByTxnId,
        AuditJobRow? job)
    {
        if (IsTxnAction(entry.Action) && txnByTxnId.TryGetValue(entry.RecordId.Trim(), out var byTxnId))
            return byTxnId;

        if (job?.TxnPk > 0 && txnById.TryGetValue(job.TxnPk, out var byJob))
            return byJob;

        return null;
    }

    private static string ResolveDisplayResult(AuditEntry entry, AuditJobRow? job, AuditTxnRow? txn)
    {
        if (!string.Equals(entry.Result, "QUEUED", StringComparison.OrdinalIgnoreCase))
            return entry.Result;

        if (job is not null)
            return job.Status;

        if (txn is not null)
            return txn.Status;

        return "QUEUED";
    }

    private static string? ResolveStatusDetail(AuditEntry entry, AuditJobRow? job, AuditTxnRow? txn)
    {
        if (!string.Equals(entry.Result, "QUEUED", StringComparison.OrdinalIgnoreCase))
            return null;

        var detail = job?.ErrorMessage;
        if (string.IsNullOrWhiteSpace(detail))
            detail = txn?.ErrorMessage;
        if (string.IsNullOrWhiteSpace(detail))
            return null;

        detail = detail.Trim();
        return detail.Length <= 120 ? detail : detail[..117] + "...";
    }

    private static (string? Url, string Label) ResolveLink(AuditEntry entry, AuditJobRow? job, AuditTxnRow? txn)
    {
        if (txn is not null)
            return ($"/Transactions/Details/{txn.TxnPk}", "View transaction");

        if (job is not null)
            return ($"/push?tab=jobs#job-{job.JobId}", "View job");

        if (IsTxnAction(entry.Action) && txn is null)
            return (null, "");

        if (string.Equals(entry.Action, "USER EXECUTED SYNC", StringComparison.OrdinalIgnoreCase)
            && TryParseJobId(entry.RecordId, out var jobId))
            return ($"/push?tab=jobs#job-{jobId}", "View job");

        return (null, "");
    }

    private static bool IsTxnAction(string action) =>
        action.StartsWith("TXN_", StringComparison.OrdinalIgnoreCase);

    private static bool IsMasterEnqueueAction(string action) =>
        string.Equals(action, "ENQUEUE_MASTER", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action, "DRY_RUN_MASTER", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJobId(string? value, out int jobId)
    {
        jobId = 0;
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith("job:", StringComparison.OrdinalIgnoreCase))
            text = text[4..];

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobId) && jobId > 0;
    }

    private static bool PayloadContainsCode(string payload, string code)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(code))
            return false;

        return payload.Contains($"\"{code}\"", StringComparison.OrdinalIgnoreCase)
            || payload.Contains($"\"Code\":\"{code}\"", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AuditJobRow
    {
        public int JobId { get; set; }
        public string JobKind { get; set; } = "";
        public string Status { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public int TxnPk { get; set; }
        public string EntityCode { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string PayloadJson { get; set; } = "";
    }

    private sealed class AuditTxnRow
    {
        public int TxnPk { get; set; }
        public string TxnId { get; set; } = "";
        public string Status { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    private static AuditJobRow MapJob(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        JobId = CentralDb.Int(r, "JOBID"),
        JobKind = CentralDb.Str(r, "JOBKIND"),
        Status = CentralDb.Str(r, "STATUS"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
        TxnPk = CentralDb.IntN(r, "TXNPK") ?? 0,
        EntityCode = CentralDb.Str(r, "ENTITYCODE"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        PayloadJson = CentralDb.Str(r, "PAYLOADJSON"),
    };

    private static AuditTxnRow MapTxn(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        TxnPk = CentralDb.Int(r, "TXNPK"),
        TxnId = CentralDb.Str(r, "TXNID"),
        Status = CentralDb.Str(r, "STATUS"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
    };

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
