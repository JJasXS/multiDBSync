using SqlAccountSync.Models;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Services.Queue;

/// <summary>Shared MASTER/TXN routing used by <see cref="SyncWorker"/> and Dev single-job process.</summary>
public static class SyncJobDispatch
{
    public static Task ProcessAsync(
        SyncJob job,
        ISyncOrchestrator sync,
        ITransactionEngine txns,
        CancellationToken cancellationToken = default)
    {
        if (job.JobKind.StartsWith("TXN", StringComparison.OrdinalIgnoreCase))
            return txns.ProcessJobAsync(job, cancellationToken);
        return sync.ProcessJobAsync(job, cancellationToken);
    }
}
