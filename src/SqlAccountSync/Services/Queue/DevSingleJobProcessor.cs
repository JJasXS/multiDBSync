using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount.Sdk;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Services.Queue;

public sealed record DevSingleJobProcessResult(
    bool Ok,
    string Message,
    int JobId,
    string? StatusBefore = null,
    string? StatusAfter = null,
    string? DestDbCode = null,
    string? JobKind = null,
    bool Claimed = false,
    bool Processed = false);

public interface IDevSingleJobProcessor
{
    Task<DevSingleJobProcessResult> ProcessExactAsync(int jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Development-only: claim and process one explicitly selected SYNC_JOB without enabling
/// background workers and without scanning other pending jobs.
/// Destination must be an authorized project company (MIRACLE / ONYAT).
/// </summary>
public sealed class DevSingleJobProcessor : IDevSingleJobProcessor
{
    private readonly IHostEnvironment _env;
    private readonly SyncOptions _syncOptions;
    private readonly IJobQueue _queue;
    private readonly IDatabaseRegistry _registry;
    private readonly ISyncOrchestrator _sync;
    private readonly ITransactionEngine _txns;
    private readonly ILogger<DevSingleJobProcessor> _logger;

    public DevSingleJobProcessor(
        IHostEnvironment env,
        IOptions<SyncOptions> syncOptions,
        IJobQueue queue,
        IDatabaseRegistry registry,
        ISyncOrchestrator sync,
        ITransactionEngine txns,
        ILogger<DevSingleJobProcessor> logger)
    {
        _env = env;
        _syncOptions = syncOptions.Value;
        _queue = queue;
        _registry = registry;
        _sync = sync;
        _txns = txns;
        _logger = logger;
    }

    public async Task<DevSingleJobProcessResult> ProcessExactAsync(
        int jobId, CancellationToken cancellationToken = default)
    {
        if (!_env.IsDevelopment())
        {
            return new DevSingleJobProcessResult(
                false, "Dev single-job process is available only in Development.", jobId);
        }

        if (!_syncOptions.AllowDevSingleJobProcess)
        {
            return new DevSingleJobProcessResult(
                false,
                "Sync:AllowDevSingleJobProcess is false. Enable only for controlled local SDK testing.",
                jobId);
        }

        if (jobId <= 0)
        {
            return new DevSingleJobProcessResult(false, "jobId must be a positive integer.", jobId);
        }

        var existing = await _queue.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new DevSingleJobProcessResult(false, "Job not found.", jobId);
        }

        var before = existing.Status;
        if (before is not (JobStatuses.Pending or JobStatuses.Retrying))
        {
            return new DevSingleJobProcessResult(
                false,
                $"Job status is {before}; only PENDING or RETRYING jobs can be processed.",
                jobId,
                StatusBefore: before,
                JobKind: existing.JobKind);
        }

        var dest = await _registry.GetAsync(existing.DestDbId, cancellationToken).ConfigureAwait(false);
        if (dest is null)
        {
            return new DevSingleJobProcessResult(
                false,
                $"Destination database DbId={existing.DestDbId} was not found.",
                jobId,
                StatusBefore: before,
                JobKind: existing.JobKind);
        }

        if (!IsAuthorizedDevTarget(dest))
        {
            return new DevSingleJobProcessResult(
                false,
                "Dev single-job process is limited to authorized MIRACLE / ONYAT destinations. " +
                $"Refused dest DbCode={dest.DbCode}.",
                jobId,
                StatusBefore: before,
                DestDbCode: dest.DbCode,
                JobKind: existing.JobKind);
        }

        var workerId = $"dev-single:{Environment.MachineName}:{Environment.ProcessId}";
        var claimed = await _queue.ClaimExactAsync(jobId, workerId, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return new DevSingleJobProcessResult(
                false,
                "Could not claim job (status changed, NEXTRUNAT in the future, or already locked).",
                jobId,
                StatusBefore: before,
                DestDbCode: dest.DbCode,
                JobKind: existing.JobKind,
                Claimed: false);
        }

        _logger.LogInformation(
            "Dev single-job process claimed JobId={JobId} Kind={Kind} Dest={Dest}",
            claimed.JobId, claimed.JobKind, dest.DbCode);

        await SyncJobDispatch.ProcessAsync(claimed, _sync, _txns, cancellationToken).ConfigureAwait(false);

        var after = await _queue.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new DevSingleJobProcessResult(
            true,
            "Claimed and processed one job via SyncOrchestrator/TransactionEngine (workers remain off).",
            jobId,
            StatusBefore: before,
            StatusAfter: after?.Status,
            DestDbCode: dest.DbCode,
            JobKind: claimed.JobKind,
            Claimed: true,
            Processed: true);
    }

    /// <summary>Authorized local SDK test destinations only (DbCode or FDB file name).</summary>
    public static bool IsAuthorizedDevTarget(RegisteredDatabase dest)
    {
        var code = (dest.DbCode ?? "").Trim();
        if (code.Equals(SqlAccProjectTargets.MiracleDbCode, StringComparison.OrdinalIgnoreCase)
            || code.Equals(SqlAccProjectTargets.OnyatDbCode, StringComparison.OrdinalIgnoreCase))
            return true;

        var file = Path.GetFileName((dest.FbPath ?? "").Trim().Replace('/', '\\'));
        return SqlAccWritePocSafety.IsAuthorizedProjectWriteDatabase(file);
    }
}
