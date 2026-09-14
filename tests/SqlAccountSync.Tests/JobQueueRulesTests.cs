using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class JobLeaseRulesTests
{
    [Fact]
    public void ClampLeaseSeconds_defaults_when_non_positive()
    {
        Assert.Equal(JobLeaseRules.DefaultProcessingLeaseSeconds, JobLeaseRules.ClampLeaseSeconds(0));
        Assert.Equal(JobLeaseRules.DefaultProcessingLeaseSeconds, JobLeaseRules.ClampLeaseSeconds(-1));
    }

    [Fact]
    public void ClampLeaseSeconds_clamps_range()
    {
        Assert.Equal(60, JobLeaseRules.ClampLeaseSeconds(10));
        Assert.Equal(86_400, JobLeaseRules.ClampLeaseSeconds(999_999));
        Assert.Equal(900, JobLeaseRules.ClampLeaseSeconds(900));
    }

    [Fact]
    public void IsLeaseExpired_false_when_lockedAt_null()
    {
        Assert.False(JobLeaseRules.IsLeaseExpired(null, DateTime.UtcNow, 900));
    }

    [Fact]
    public void IsLeaseExpired_true_after_lease_window()
    {
        var now = DateTime.UtcNow;
        var locked = now.AddMinutes(-20);
        Assert.True(JobLeaseRules.IsLeaseExpired(locked, now, 900));
    }

    [Fact]
    public void IsLeaseExpired_false_inside_lease_window()
    {
        var now = DateTime.UtcNow;
        var locked = now.AddMinutes(-5);
        Assert.False(JobLeaseRules.IsLeaseExpired(locked, now, 900));
    }

    [Fact]
    public void ResolveCompleteStatus_promotes_retrying_to_failed_at_max_attempts()
    {
        Assert.Equal(JobStatuses.Failed,
            JobLeaseRules.ResolveCompleteStatus(JobStatuses.Retrying, attempts: 5, maxAttempts: 5));
        Assert.Equal(JobStatuses.Retrying,
            JobLeaseRules.ResolveCompleteStatus(JobStatuses.Retrying, attempts: 4, maxAttempts: 5));
        Assert.Equal(JobStatuses.Failed,
            JobLeaseRules.ResolveCompleteStatus(JobStatuses.Failed, attempts: 99, maxAttempts: 5));
    }

    [Fact]
    public void BackoffSeconds_is_bounded()
    {
        var delay = JobLeaseRules.BackoffSeconds(20);
        Assert.InRange(delay, 0, 3600);
    }
}

public class TxnEnqueueRulesTests
{
    [Fact]
    public void DecideCompleted_already_done_when_dest_exists()
    {
        Assert.Equal(TxnEnqueueRules.CompletedOutcome.AlreadyDone, TxnEnqueueRules.DecideCompleted(true));
    }

    [Fact]
    public void DecideCompleted_reopen_stale_when_dest_missing()
    {
        Assert.Equal(TxnEnqueueRules.CompletedOutcome.ReopenStale, TxnEnqueueRules.DecideCompleted(false));
    }
}

/// <summary>
/// Optional live checks against central SYNC.FDB. Skipped when CentralDb is not configured.
/// </summary>
public class JobQueueIntegrationTests
{
    [Fact]
    public async Task Reclaim_and_claim_behave_when_central_db_available()
    {
        var factory = CentralDbTestFactory.TryCreate();
        if (factory is null)
        {
            // No central DB in this environment — pure rules above still cover lease/max-attempts.
            return;
        }

        var (queue, db) = factory.Value;
        var idem = "test-lease-" + Guid.NewGuid().ToString("N");
        var jobId = await queue.EnqueueAsync(new SyncJob
        {
            JobKind = "TEST",
            SourceDbId = 0,
            DestDbId = 0,
            PayloadJson = "{}",
            IdemKey = idem,
            CreatedBy = "tests",
            MaxAttempts = 5
        });

        var claimed = await queue.ClaimAsync("test-worker-a");
        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed!.JobId);
        Assert.Equal(JobStatuses.Processing, claimed.Status);

        // Second claim must not steal the PROCESSING row (optimistic claim only PENDING/RETRYING).
        var stolen = await queue.ClaimAsync("test-worker-b");
        if (stolen is not null)
            Assert.NotEqual(jobId, stolen.JobId);

        // Force LOCKEDAT into the past, then reclaim.
        await db.ExecuteAsync(@"
UPDATE SYNC_JOB SET LOCKEDAT=DATEADD(SECOND, -1800, CURRENT_TIMESTAMP)
WHERE JOBID=@JOBID AND STATUS='PROCESSING'", new { JOBID = jobId });

        var reclaimed = await queue.ReclaimStaleProcessingAsync();
        Assert.True(reclaimed >= 1);

        var after = await queue.GetAsync(jobId);
        Assert.NotNull(after);
        Assert.Equal(JobStatuses.Retrying, after!.Status);
        Assert.True(string.IsNullOrEmpty(after.LockedBy));

        // Max attempts → FAILED via CompleteAsync(Retrying).
        await db.ExecuteAsync("UPDATE SYNC_JOB SET ATTEMPTS=5, MAXATTEMPTS=5 WHERE JOBID=@JOBID", new { JOBID = jobId });
        await queue.CompleteAsync(jobId, JobStatuses.Retrying, "force fail");
        var failed = await queue.GetAsync(jobId);
        Assert.Equal(JobStatuses.Failed, failed!.Status);

        await queue.CancelAsync(jobId);
    }
}
