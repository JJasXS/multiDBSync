using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Queue;

public readonly record struct JobEnqueueResult(int JobId, bool IsNew, bool LeftCancelled = false);

public interface IJobQueue
{
    Task<JobEnqueueResult> EnqueueAsync(SyncJob job, CancellationToken cancellationToken = default, bool reviveCancelled = true);
    Task<SyncJob?> ClaimAsync(string workerId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Claim one specific PENDING/RETRYING job by id (Development single-job path).
    /// Does not scan or claim other queued jobs.
    /// </summary>
    Task<SyncJob?> ClaimExactAsync(int jobId, string workerId, CancellationToken cancellationToken = default);
    Task CompleteAsync(int jobId, string status, string? error, CancellationToken cancellationToken = default);
    Task<List<SyncJob>> ListAsync(string? status = null, int take = 200, CancellationToken cancellationToken = default);
    Task<SyncJob?> GetAsync(int jobId, CancellationToken cancellationToken = default);
    Task CancelAsync(int jobId, CancellationToken cancellationToken = default);
    /// <summary>Cancel every PENDING, RETRYING, and PROCESSING job. Returns how many rows were updated.</summary>
    Task<int> CancelAllActiveAsync(CancellationToken cancellationToken = default);
    /// <summary>Cancel PENDING jobs that have never started and have waited longer than MaxQueueWaitSeconds.</summary>
    Task<int> CancelUnstartedWaitingAsync(CancellationToken cancellationToken = default);
    /// <summary>Reclaim PROCESSING jobs whose LOCKEDAT is older than the configured lease.</summary>
    Task<int> ReclaimStaleProcessingAsync(CancellationToken cancellationToken = default);
    /// <summary>Operator unlock: PROCESSING → RETRYING immediately.</summary>
    Task UnlockProcessingAsync(int jobId, CancellationToken cancellationToken = default);
    Task<QueueHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class QueueHealthSnapshot
{
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Failed { get; set; }
    public double? OldestProcessingAgeSec { get; set; }
}

public sealed class JobQueue : IJobQueue
{
    private readonly CentralDb _db;
    private readonly SyncOptions _options;
    private readonly IRegistryReadGateway? _registryReads;
    private readonly IHostEnvironment? _environment;

    public JobQueue(
        CentralDb db,
        IOptions<SyncOptions> options,
        IRegistryReadGateway? registryReads = null,
        IHostEnvironment? environment = null)
    {
        _db = db;
        _options = options.Value;
        _registryReads = registryReads;
        _environment = environment;
    }

    private bool UseLiveDashboardReads =>
        _environment?.IsDevelopment() == true && _registryReads?.ReadsLiveRegistry == true;

    public async Task<JobEnqueueResult> EnqueueAsync(
        SyncJob job, CancellationToken cancellationToken = default, bool reviveCancelled = true)
    {
        var id = (int)await _db.NextIdAsync("GEN_SYNC_JOB", cancellationToken).ConfigureAwait(false);
        try
        {
            await InsertJobAsync(job, id, cancellationToken).ConfigureAwait(false);
            return new JobEnqueueResult(id, true);
        }
        catch (FirebirdSql.Data.FirebirdClient.FbException ex) when (ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            return await ReuseExistingJobAsync(job, id, reviveCancelled, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task InsertJobAsync(SyncJob job, int id, CancellationToken cancellationToken) =>
        _db.ExecuteAsync(@"
INSERT INTO SYNC_JOB (JOBID, JOBKIND, TXNPK, SOURCEDBID, DESTDBID, ENTITYCODE, PAYLOADJSON, STATUS, PRIORITY, ATTEMPTS, MAXATTEMPTS, NEXTRUNAT, DRYRUN, IDEMKEY, CREATEDAT, CREATEDBY)
VALUES (@JOBID, @JOBKIND, @TXNPK, @SOURCEDBID, @DESTDBID, @ENTITYCODE, @PAYLOADJSON, 'PENDING', @PRIORITY, 0, @MAXATTEMPTS, CURRENT_TIMESTAMP, @DRYRUN, @IDEMKEY, CURRENT_TIMESTAMP, @CREATEDBY)",
            new
            {
                JOBID = id,
                JOBKIND = job.JobKind,
                TXNPK = (object?)job.TxnId ?? DBNull.Value,
                SOURCEDBID = job.SourceDbId,
                DESTDBID = job.DestDbId,
                ENTITYCODE = job.EntityCode,
                PAYLOADJSON = job.PayloadJson,
                PRIORITY = job.Priority,
                MAXATTEMPTS = job.MaxAttempts <= 0 ? _options.MaxAttempts : job.MaxAttempts,
                DRYRUN = job.DryRun ? 1 : 0,
                IDEMKEY = job.IdemKey,
                CREATEDBY = job.CreatedBy
            }, cancellationToken);

    private async Task<JobEnqueueResult> ReuseExistingJobAsync(
        SyncJob job, int unusedId, bool reviveCancelled, CancellationToken cancellationToken)
    {
        var existing = await _db.QuerySingleAsync(
            "SELECT FIRST 1 * FROM SYNC_JOB WHERE IDEMKEY=@IDEMKEY",
            Map, new { IDEMKEY = job.IdemKey }, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return new JobEnqueueResult(unusedId, false);

        if (existing.Status is JobStatuses.Cancelled && !reviveCancelled)
            return new JobEnqueueResult(existing.JobId, false, true);

        if (existing.Status is JobStatuses.Failed or JobStatuses.Cancelled)
        {
            await ResetFailedJobAsync(job, existing.JobId, cancellationToken).ConfigureAwait(false);
            return new JobEnqueueResult(existing.JobId, true);
        }

        if (existing.Status is JobStatuses.Retrying && !AutoPushJobRules.IsAutoPushUser(job.CreatedBy))
            await BumpRetryJobAsync(job, existing.JobId, cancellationToken).ConfigureAwait(false);

        return new JobEnqueueResult(existing.JobId, false);
    }

    private Task ResetFailedJobAsync(SyncJob job, int jobId, CancellationToken cancellationToken) =>
        _db.ExecuteAsync(@"
UPDATE SYNC_JOB SET STATUS='PENDING', ATTEMPTS=0, ERRORMESSAGE='', LOCKEDBY=NULL, LOCKEDAT=NULL,
NEXTRUNAT=CURRENT_TIMESTAMP, CREATEDAT=CURRENT_TIMESTAMP, PAYLOADJSON=@PAYLOADJSON, CREATEDBY=@CREATEDBY, PRIORITY=@PRIORITY
WHERE JOBID=@JOBID",
            new { PAYLOADJSON = job.PayloadJson, CREATEDBY = job.CreatedBy, PRIORITY = job.Priority, JOBID = jobId },
            cancellationToken);

    private Task BumpRetryJobAsync(SyncJob job, int jobId, CancellationToken cancellationToken) =>
        _db.ExecuteAsync(@"
UPDATE SYNC_JOB SET NEXTRUNAT=CURRENT_TIMESTAMP, PAYLOADJSON=@PAYLOADJSON
WHERE JOBID=@JOBID AND STATUS='RETRYING'",
            new { PAYLOADJSON = job.PayloadJson, JOBID = jobId },
            cancellationToken);

    public async Task<SyncJob?> ClaimAsync(string workerId, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return null;
        var candidate = await _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_JOB
WHERE STATUS IN ('PENDING','RETRYING') AND NEXTRUNAT <= CURRENT_TIMESTAMP
ORDER BY PRIORITY DESC, JOBID", Map, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (candidate is null)
            return null;

        var rows = await _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='PROCESSING', LOCKEDBY=@LOCKEDBY, LOCKEDAT=CURRENT_TIMESTAMP, ATTEMPTS=ATTEMPTS+1
WHERE JOBID=@JOBID AND STATUS IN ('PENDING','RETRYING') AND NEXTRUNAT<=CURRENT_TIMESTAMP",
            new { LOCKEDBY = workerId, JOBID = candidate.JobId }, cancellationToken).ConfigureAwait(false);
        if (rows != 1)
            return null;
        return await GetAsync(candidate.JobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncJob?> ClaimExactAsync(int jobId, string workerId, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured || jobId <= 0)
            return null;

        var rows = await _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='PROCESSING', LOCKEDBY=@LOCKEDBY, LOCKEDAT=CURRENT_TIMESTAMP, ATTEMPTS=ATTEMPTS+1
WHERE JOBID=@JOBID AND STATUS IN ('PENDING','RETRYING') AND NEXTRUNAT<=CURRENT_TIMESTAMP",
            new { LOCKEDBY = workerId, JOBID = jobId }, cancellationToken).ConfigureAwait(false);
        if (rows != 1)
            return null;
        return await GetAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(int jobId, string status, string? error, CancellationToken cancellationToken = default)
    {
        if (status is JobStatuses.Failed or JobStatuses.Retrying)
        {
            var job = await GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job?.Status == JobStatuses.Cancelled)
                return;
            status = JobLeaseRules.ResolveCompleteStatus(status, job?.Attempts ?? 0, job?.MaxAttempts ?? 0);
            var delaySeconds = job is null ? 30 : JobLeaseRules.BackoffSeconds(job.Attempts);
            await _db.ExecuteAsync(@"
UPDATE SYNC_JOB SET STATUS=@STATUS, ERRORMESSAGE=@ERR, LOCKEDBY=NULL, LOCKEDAT=NULL,
NEXTRUNAT=DATEADD(SECOND, @DELAY, CURRENT_TIMESTAMP)
WHERE JOBID=@JOBID AND STATUS<>'CANCELLED'",
                new { STATUS = status, ERR = error ?? "", DELAY = delaySeconds, JOBID = jobId }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _db.ExecuteAsync(@"
UPDATE SYNC_JOB SET STATUS=@STATUS, ERRORMESSAGE=@ERR, LOCKEDBY=NULL, LOCKEDAT=NULL
WHERE JOBID=@JOBID AND STATUS<>'CANCELLED'",
            new { STATUS = status, ERR = error ?? "", JOBID = jobId }, cancellationToken).ConfigureAwait(false);
    }

    public Task<List<SyncJob>> ListAsync(string? status = null, int take = 200, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 500);
        if (UseLiveDashboardReads)
        {
            if (string.IsNullOrWhiteSpace(status))
                return _registryReads!.QueryAsync($"SELECT FIRST {take} * FROM SYNC_JOB ORDER BY JOBID DESC", Map, cancellationToken: cancellationToken);
            return _registryReads!.QueryAsync(
                $"SELECT FIRST {take} * FROM SYNC_JOB WHERE STATUS=@STATUS ORDER BY JOBID DESC",
                Map, new { STATUS = status }, cancellationToken);
        }
        if (!_db.IsConfigured)
            return Task.FromResult(new List<SyncJob>());
        if (string.IsNullOrWhiteSpace(status))
            return _db.QueryAsync($"SELECT FIRST {take} * FROM SYNC_JOB ORDER BY JOBID DESC", Map, cancellationToken: cancellationToken);
        return _db.QueryAsync($"SELECT FIRST {take} * FROM SYNC_JOB WHERE STATUS=@STATUS ORDER BY JOBID DESC", Map, new { STATUS = status }, cancellationToken);
    }

    public Task<SyncJob?> GetAsync(int jobId, CancellationToken cancellationToken = default) =>
        _db.QuerySingleAsync("SELECT FIRST 1 * FROM SYNC_JOB WHERE JOBID=@JOBID", Map, new { JOBID = jobId }, cancellationToken);

    public Task CancelAsync(int jobId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync(@"
UPDATE SYNC_JOB SET STATUS='CANCELLED', LOCKEDBY=NULL, LOCKEDAT=NULL
WHERE JOBID=@JOBID AND STATUS IN ('PENDING','RETRYING','FAILED','PROCESSING')",
            new { JOBID = jobId }, cancellationToken);

    public async Task<int> CancelAllActiveAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return 0;

        return await _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='CANCELLED', LOCKEDBY=NULL, LOCKEDAT=NULL,
    ERRORMESSAGE='Stopped by operator.'
WHERE STATUS IN ('PENDING','RETRYING','PROCESSING')",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CancelUnstartedWaitingAsync(CancellationToken cancellationToken = default)
    {
        var waitSeconds = _options.MaxQueueWaitSeconds;
        if (!_db.IsConfigured || waitSeconds <= 0)
            return 0;

        return await _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='CANCELLED', LOCKEDBY=NULL, LOCKEDAT=NULL,
    ERRORMESSAGE='Stopped: waited more than 2 minutes.'
WHERE STATUS='PENDING' AND ATTEMPTS=0
  AND DATEDIFF(SECOND, CREATEDAT, CURRENT_TIMESTAMP) > @WAIT",
            new { WAIT = waitSeconds }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReclaimStaleProcessingAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return 0;

        var lease = JobLeaseRules.ClampLeaseSeconds(_options.ProcessingLeaseSeconds);
        // Use DATEDIFF — Firebird rejects DATEADD(SECOND, -@LEASE, ...) when LEASE is a bound param.
        return await _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='RETRYING', LOCKEDBY=NULL, LOCKEDAT=NULL, NEXTRUNAT=CURRENT_TIMESTAMP,
    ERRORMESSAGE='Reclaimed: processing lease expired'
WHERE STATUS='PROCESSING'
  AND LOCKEDAT IS NOT NULL
  AND DATEDIFF(SECOND, LOCKEDAT, CURRENT_TIMESTAMP) > @LEASE",
            new { LEASE = lease }, cancellationToken).ConfigureAwait(false);
    }

    public Task UnlockProcessingAsync(int jobId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync(@"
UPDATE SYNC_JOB
SET STATUS='RETRYING', LOCKEDBY=NULL, LOCKEDAT=NULL, NEXTRUNAT=CURRENT_TIMESTAMP,
    ERRORMESSAGE='Unlocked by operator'
WHERE JOBID=@JOBID AND STATUS='PROCESSING'",
            new { JOBID = jobId }, cancellationToken);

    public async Task<QueueHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var snap = new QueueHealthSnapshot();
        if (!_db.IsConfigured)
            return snap;

        snap.Pending = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('PENDING','RETRYING')", cancellationToken: cancellationToken).ConfigureAwait(false) ?? 0);
        snap.Processing = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS='PROCESSING'", cancellationToken: cancellationToken).ConfigureAwait(false) ?? 0);
        snap.Failed = Convert.ToInt32(await _db.ScalarAsync(
            "SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('FAILED','CONFLICT','WAITING_FOR_MAPPING')", cancellationToken: cancellationToken).ConfigureAwait(false) ?? 0);

        var oldest = await _db.ScalarAsync(
            "SELECT MIN(LOCKEDAT) FROM SYNC_JOB WHERE STATUS='PROCESSING' AND LOCKEDAT IS NOT NULL",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (oldest is DateTime lockedAt)
            snap.OldestProcessingAgeSec = Math.Max(0, (DateTime.UtcNow - lockedAt.ToUniversalTime()).TotalSeconds);
        else if (oldest is not null && DateTime.TryParse(oldest.ToString(), out var parsed))
            snap.OldestProcessingAgeSec = Math.Max(0, (DateTime.UtcNow - parsed.ToUniversalTime()).TotalSeconds);

        return snap;
    }

    private static SyncJob Map(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        JobId = CentralDb.Int(r, "JOBID"),
        JobKind = CentralDb.Str(r, "JOBKIND"),
        TxnId = CentralDb.IntN(r, "TXNPK"),
        SourceDbId = CentralDb.Int(r, "SOURCEDBID"),
        DestDbId = CentralDb.Int(r, "DESTDBID"),
        EntityCode = CentralDb.Str(r, "ENTITYCODE"),
        PayloadJson = CentralDb.Str(r, "PAYLOADJSON"),
        Status = CentralDb.Str(r, "STATUS"),
        Priority = CentralDb.Int(r, "PRIORITY"),
        Attempts = CentralDb.Int(r, "ATTEMPTS"),
        MaxAttempts = CentralDb.Int(r, "MAXATTEMPTS"),
        NextRunAt = CentralDb.TimeReq(r, "NEXTRUNAT"),
        LockedBy = CentralDb.Str(r, "LOCKEDBY"),
        LockedAt = CentralDb.Time(r, "LOCKEDAT"),
        DryRun = CentralDb.Flag(r, "DRYRUN"),
        IdemKey = CentralDb.Str(r, "IDEMKEY"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY")
    };
}
