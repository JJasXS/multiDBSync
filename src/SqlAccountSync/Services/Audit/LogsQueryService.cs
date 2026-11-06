using System.Globalization;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Audit;

/// <summary>
/// Logs-page API. Reads go through <see cref="ILogsReadGateway"/> (SELECT-only).
/// Allowed writes: <see cref="ResolveErrorAsync"/> and <see cref="RetryTransactionAsync"/>.
/// </summary>
public interface ILogsQueryService
{
    bool IsRemote { get; }
    string DescribeSource();
    Task<(int Transaction, int Audit, int Error)> CountAllAsync(
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default);
    Task<List<SyncTransaction>> ListTransactionsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default);
    Task<List<AuditListItem>> ListAuditsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default);
    Task<List<ErrorEntry>> ListErrorsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default);
    Task ResolveErrorAsync(int errorId, CancellationToken cancellationToken = default);
    Task RetryTransactionAsync(int txnPk, string user, CancellationToken cancellationToken = default);
}

public sealed class LogsQueryService : ILogsQueryService
{
    private readonly ILogsReadGateway _db;
    private readonly SyncOptions _sync;

    public LogsQueryService(ILogsReadGateway db, IOptions<SyncOptions> sync)
    {
        _db = db;
        _sync = sync.Value;
    }

    public bool IsRemote => _db.IsRemote;

    public string DescribeSource() => _db.DescribeSource();

    public Task ResolveErrorAsync(int errorId, CancellationToken cancellationToken = default) =>
        _db.ResolveErrorAsync(errorId, cancellationToken);

    public async Task RetryTransactionAsync(int txnPk, string user, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured || txnPk <= 0)
            return;

        var rows = await _db.QueryAsync(@"
SELECT FIRST 1 T.*, S.DISPLAYNAME AS SOURCENAME, D.DISPLAYNAME AS DESTNAME
FROM SYNC_TXN T
JOIN SYNC_DATABASE S ON S.DBID=T.SOURCEDBID
JOIN SYNC_DATABASE D ON D.DBID=T.DESTDBID
WHERE T.TXNPK=@TXNPK", MapTxn, new { TXNPK = txnPk }, cancellationToken).ConfigureAwait(false);
        var txn = rows.FirstOrDefault();
        if (txn is null)
            return;
        if (txn.Status is not (
            JobStatuses.Failed or JobStatuses.Retrying or JobStatuses.Conflict
            or JobStatuses.WaitingForMapping or JobStatuses.PartiallyCompleted))
            return;

        var types = await _db.QueryAsync(
            "SELECT TXNTYPECODE, DSTAPIPATH FROM SYNC_TXNTYPE WHERE ISACTIVE=1",
            r => (Code: CentralDb.Str(r, "TXNTYPECODE"), DstPath: CentralDb.Str(r, "DSTAPIPATH")),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var dstPath = types.FirstOrDefault(t => string.Equals(t.Code, txn.TxnTypeCode, StringComparison.OrdinalIgnoreCase)).DstPath ?? "";
        var payload = JsonSerializer.Serialize(new
        {
            TxnPk = txn.TxnPk,
            TxnType = txn.TxnTypeCode,
            DstApiPath = dstPath,
            SrcDoc = txn.SrcDocNo
        });
        var idemKey = IdempotencyKey.Hash(txn.IdemKey + "|RETRY|" + Guid.NewGuid().ToString("N")[..8]);
        var maxAttempts = _sync.MaxAttempts <= 0 ? 5 : _sync.MaxAttempts;
        await _db.QueueTxnRetryAsync(txn, payload, idemKey, user, maxAttempts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Transaction, int Audit, int Error)> CountAllAsync(
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return (0, 0, 0);
        var bounds = NormalizeBounds(fromInclusive, toExclusive);
        var txn = await CountAsync($@"
SELECT COUNT(*) FROM SYNC_TXN T
WHERE (@HASFROM = 0 OR (CASE WHEN T.UPDATEDAT IS NOT NULL AND EXTRACT(YEAR FROM T.UPDATEDAT) > 2000 THEN T.UPDATEDAT ELSE T.CREATEDAT END) >= @FROMAT)
  AND (@HASTO = 0 OR (CASE WHEN T.UPDATEDAT IS NOT NULL AND EXTRACT(YEAR FROM T.UPDATEDAT) > 2000 THEN T.UPDATEDAT ELSE T.CREATEDAT END) < @TOEX)",
            bounds, cancellationToken).ConfigureAwait(false);
        var audit = await CountAsync(
            "SELECT COUNT(*) FROM SYNC_AUDIT WHERE (@HASFROM = 0 OR ATUTC >= @FROMAT) AND (@HASTO = 0 OR ATUTC < @TOEX)",
            bounds, cancellationToken).ConfigureAwait(false);
        var error = await CountAsync(
            "SELECT COUNT(*) FROM SYNC_ERROR WHERE (@HASFROM = 0 OR ATUTC >= @FROMAT) AND (@HASTO = 0 OR ATUTC < @TOEX)",
            bounds, cancellationToken).ConfigureAwait(false);
        return (txn, audit, error);
    }

    public Task<List<SyncTransaction>> ListTransactionsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<SyncTransaction>());
        var limit = Math.Clamp(take, 1, 500);
        var bounds = NormalizeBounds(fromInclusive, toExclusive);
        return _db.QueryAsync($@"
SELECT FIRST {limit} * FROM (
    SELECT T.*, S.DISPLAYNAME AS SOURCENAME, D.DISPLAYNAME AS DESTNAME,
        CASE WHEN T.UPDATEDAT IS NOT NULL AND EXTRACT(YEAR FROM T.UPDATEDAT) > 2000 THEN T.UPDATEDAT ELSE T.CREATEDAT END AS LOGAT
    FROM SYNC_TXN T
    JOIN SYNC_DATABASE S ON S.DBID=T.SOURCEDBID
    JOIN SYNC_DATABASE D ON D.DBID=T.DESTDBID
) X
WHERE (@HASFROM = 0 OR LOGAT >= @FROMAT)
  AND (@HASTO = 0 OR LOGAT < @TOEX)
  AND (@HASCURSOR = 0
   OR LOGAT < @CURSORAT
   OR (LOGAT = @CURSORAT AND @CURSORKIND = 'transaction' AND TXNPK < @CURSORID))
ORDER BY LOGAT DESC, TXNPK DESC", MapTxn, MergeArgs(bounds, new
        {
            HASCURSOR = before.HasValue ? 1 : 0,
            CURSORAT = before ?? DateTime.MinValue,
            CURSORKIND = beforeKind ?? "",
            CURSORID = beforeId
        }), cancellationToken);
    }

    public async Task<List<AuditListItem>> ListAuditsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return [];

        var limit = Math.Clamp(take, 1, 500);
        var bounds = NormalizeBounds(fromInclusive, toExclusive);
        var rows = await _db.QueryAsync($@"
SELECT FIRST {limit}
    a.AUDITID, a.ATUTC, a.USEREMAIL, a.ACTION, a.DBID, a.ENTITY, a.RECORDID, a.RESULT,
    a.IPADDR, a.SESSIONID, a.REQUESTID,
    COALESCE(d.DISPLAYNAME, d.DBCODE, '') AS DESTNAME
FROM SYNC_AUDIT a
LEFT JOIN SYNC_DATABASE d ON d.DBID = a.DBID
WHERE (@HASFROM = 0 OR a.ATUTC >= @FROMAT)
  AND (@HASTO = 0 OR a.ATUTC < @TOEX)
  AND (@HASCURSOR = 0
   OR a.ATUTC < @CURSORAT
   OR (a.ATUTC = @CURSORAT AND @CURSORKIND = 'audit' AND a.AUDITID < @CURSORID))
ORDER BY a.ATUTC DESC, a.AUDITID DESC", MapAuditWithDest, MergeArgs(bounds, new
        {
            HASCURSOR = before.HasValue ? 1 : 0,
            CURSORAT = before ?? DateTime.MinValue,
            CURSORKIND = beforeKind ?? "",
            CURSORID = beforeId
        }), cancellationToken).ConfigureAwait(false);

        var entries = rows.Select(r => r.Entry).ToList();
        var jobs = await LoadRecentJobsAsync(Math.Min(limit * 3, 500), cancellationToken).ConfigureAwait(false);
        jobs.AddRange(await LoadMissingJobsAsync(entries, jobs, cancellationToken).ConfigureAwait(false));
        var txns = await _db.QueryAsync($@"
SELECT FIRST {Math.Min(limit * 2, 300)} TXNPK, TXNID, STATUS, ERRORMESSAGE
FROM SYNC_TXN ORDER BY TXNPK DESC", MapAuditTxn, cancellationToken: cancellationToken).ConfigureAwait(false);

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

        return rows.Select(r =>
        {
            var item = ResolveItem(r.Entry, jobs, jobsById, jobsByTxnPk, txnById, txnByTxnId);
            if (string.IsNullOrWhiteSpace(item.DestName) && !string.IsNullOrWhiteSpace(r.DestName))
                item.DestName = r.DestName;
            return item;
        }).ToList();
    }

    public Task<List<ErrorEntry>> ListErrorsPageAsync(
        DateTime? before, string? beforeKind, int beforeId, int take,
        DateTime? fromInclusive = null, DateTime? toExclusive = null, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<ErrorEntry>());
        var limit = Math.Clamp(take, 1, 500);
        var bounds = NormalizeBounds(fromInclusive, toExclusive);
        return _db.QueryAsync($@"
SELECT FIRST {limit} * FROM SYNC_ERROR
WHERE (@HASFROM = 0 OR ATUTC >= @FROMAT)
  AND (@HASTO = 0 OR ATUTC < @TOEX)
  AND (@HASCURSOR = 0
   OR ATUTC < @CURSORAT
   OR (ATUTC = @CURSORAT AND @CURSORKIND = 'error' AND ERRORID < @CURSORID))
ORDER BY ATUTC DESC, ERRORID DESC", MapError, MergeArgs(bounds, new
        {
            HASCURSOR = before.HasValue ? 1 : 0,
            CURSORAT = before ?? DateTime.MinValue,
            CURSORKIND = beforeKind ?? "",
            CURSORID = beforeId
        }), cancellationToken);
    }

    private async Task<int> CountAsync(string sql, object args, CancellationToken cancellationToken)
    {
        var value = await _db.ScalarAsync(sql, args, cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static object NormalizeBounds(DateTime? fromInclusive, DateTime? toExclusive) => new
    {
        HASFROM = fromInclusive.HasValue ? 1 : 0,
        FROMAT = fromInclusive ?? DateTime.MinValue,
        HASTO = toExclusive.HasValue ? 1 : 0,
        TOEX = toExclusive ?? DateTime.MaxValue
    };

    private static Dictionary<string, object?> MergeArgs(object bounds, object cursor)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in bounds.GetType().GetProperties())
            map[prop.Name] = prop.GetValue(bounds);
        foreach (var prop in cursor.GetType().GetProperties())
            map[prop.Name] = prop.GetValue(cursor);
        return map;
    }

    private Task<List<AuditJobRow>> LoadRecentJobsAsync(int take, CancellationToken cancellationToken) =>
        _db.QueryAsync(JobSelectSql($"SELECT FIRST {take}") + " ORDER BY j.JOBID DESC", MapJob, cancellationToken: cancellationToken);

    private async Task<List<AuditJobRow>> LoadMissingJobsAsync(
        List<AuditEntry> entries, List<AuditJobRow> loaded, CancellationToken cancellationToken)
    {
        var known = loaded.Select(j => j.JobId).ToHashSet();
        var missing = entries
            .Select(e => TryParseJobId(e.RequestId, out var id) ? id : 0)
            .Where(id => id > 0 && !known.Contains(id))
            .Distinct()
            .ToList();
        if (missing.Count == 0)
            return [];

        return await _db.QueryAsync(
            JobSelectSql("SELECT") + $" WHERE j.JOBID IN ({string.Join(",", missing)})",
            MapJob, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string JobSelectSql(string select) => $@"
{select} j.JOBID, j.JOBKIND, j.STATUS, j.ERRORMESSAGE, j.TXNPK, j.ENTITYCODE, j.CREATEDBY, j.CREATEDAT, j.PAYLOADJSON,
       COALESCE(d.DISPLAYNAME, '') AS DESTNAME
FROM SYNC_JOB j
LEFT JOIN SYNC_DATABASE d ON d.DBID = j.DESTDBID";

    // --- mapping / audit enrichment (mirrors AuditService read path) ---

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
        {
            item.JobId = job.JobId;
            item.DestName = job.DestName;
        }
        if (txn is not null)
            item.TxnPk = txn.TxnPk;
        item.DisplayResult = ResolveDisplayResult(entry, job, txn);
        item.StatusDetail = ResolveStatusDetail(entry, job, txn);
        (item.LinkUrl, item.LinkLabel) = txn is not null
            ? ($"/Transactions/Details/{txn.TxnPk}", "View transaction")
            : (null, "");
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
        if (entry.Action.StartsWith("TXN_", StringComparison.OrdinalIgnoreCase))
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

        if (!string.Equals(entry.Action, "ENQUEUE_MASTER", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.Action, "DRY_RUN_MASTER", StringComparison.OrdinalIgnoreCase))
            return null;

        var code = entry.RecordId.Trim();
        if (code.Length == 0)
            return null;

        return jobs
            .Where(j => j.JobKind.StartsWith("MASTER", StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.EntityCode, entry.Entity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.CreatedBy, entry.UserEmail, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((j.CreatedAt - entry.AtUtc).TotalSeconds) <= 20
                && (j.PayloadJson.Contains($"\"{code}\"", StringComparison.OrdinalIgnoreCase)
                    || j.PayloadJson.Contains($"\"Code\":\"{code}\"", StringComparison.OrdinalIgnoreCase)))
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
        if (entry.Action.StartsWith("TXN_", StringComparison.OrdinalIgnoreCase)
            && txnByTxnId.TryGetValue(entry.RecordId.Trim(), out var byTxnId))
            return byTxnId;
        if (job?.TxnPk > 0 && txnById.TryGetValue(job.TxnPk, out var byJob))
            return byJob;
        return null;
    }

    private static string ResolveDisplayResult(AuditEntry entry, AuditJobRow? job, AuditTxnRow? txn)
    {
        if (!string.Equals(entry.Result, "QUEUED", StringComparison.OrdinalIgnoreCase))
            return entry.Result;
        if (job is not null) return job.Status;
        if (txn is not null) return txn.Status;
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

    private static bool TryParseJobId(string? value, out int jobId)
    {
        jobId = 0;
        var text = (value ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith("job:", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobId) && jobId > 0;
    }

    private static SyncTransaction MapTxn(FbDataReader r) => new()
    {
        TxnPk = CentralDb.Int(r, "TXNPK"),
        TxnId = CentralDb.Str(r, "TXNID"),
        TxnTypeCode = CentralDb.Str(r, "TXNTYPECODE"),
        SourceDbId = CentralDb.Int(r, "SOURCEDBID"),
        DestDbId = CentralDb.Int(r, "DESTDBID"),
        SrcDocType = CentralDb.Str(r, "SRCDOCTYPE"),
        SrcDocKey = CentralDb.Str(r, "SRCDOCKEY"),
        SrcDocNo = CentralDb.Str(r, "SRCDOCNO"),
        DstDocType = CentralDb.Str(r, "DSTDOCTYPE"),
        DstDocKey = CentralDb.Str(r, "DSTDOCKEY"),
        DstDocNo = CentralDb.Str(r, "DSTDOCNO"),
        IdemKey = CentralDb.Str(r, "IDEMKEY"),
        Status = CentralDb.Str(r, "STATUS"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
        RetryCount = CentralDb.Int(r, "RETRYCOUNT"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        StartedAt = CentralDb.Time(r, "STARTEDAT"),
        CompletedAt = CentralDb.Time(r, "COMPLETEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        UpdatedAt = CentralDb.TimeReq(r, "UPDATEDAT"),
        SourceName = Has(r, "SOURCENAME") ? CentralDb.Str(r, "SOURCENAME") : "",
        DestName = Has(r, "DESTNAME") ? CentralDb.Str(r, "DESTNAME") : ""
    };

    private static AuditEntry MapAudit(FbDataReader r) => new()
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

    private static (AuditEntry Entry, string DestName) MapAuditWithDest(FbDataReader r) =>
        (MapAudit(r), Has(r, "DESTNAME") ? CentralDb.Str(r, "DESTNAME") : "");

    private static ErrorEntry MapError(FbDataReader r) => new()
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

    private static AuditJobRow MapJob(FbDataReader r) => new()
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
        DestName = CentralDb.Str(r, "DESTNAME"),
    };

    private static AuditTxnRow MapAuditTxn(FbDataReader r) => new()
    {
        TxnPk = CentralDb.Int(r, "TXNPK"),
        TxnId = CentralDb.Str(r, "TXNID"),
        Status = CentralDb.Str(r, "STATUS"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
    };

    private static bool Has(FbDataReader r, string col)
    {
        try { return r.GetOrdinal(col) >= 0; } catch { return false; }
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
        public string DestName { get; set; } = "";
    }

    private sealed class AuditTxnRow
    {
        public int TxnPk { get; set; }
        public string TxnId { get; set; } = "";
        public string Status { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }
}
