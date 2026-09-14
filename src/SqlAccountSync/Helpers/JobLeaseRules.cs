using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Pure rules for job lease reclaim and retry terminal status (unit-testable).</summary>
public static class JobLeaseRules
{
    public const int DefaultProcessingLeaseSeconds = 900;

    public static int ClampLeaseSeconds(int leaseSeconds) =>
        leaseSeconds <= 0 ? DefaultProcessingLeaseSeconds : Math.Clamp(leaseSeconds, 60, 86_400);

    public static bool IsLeaseExpired(DateTime? lockedAt, DateTime utcNow, int leaseSeconds)
    {
        if (lockedAt is null)
            return false;
        var lease = ClampLeaseSeconds(leaseSeconds);
        return lockedAt.Value.ToUniversalTime() <= utcNow.ToUniversalTime().AddSeconds(-lease);
    }

    /// <summary>
    /// When completing with Retrying, promote to Failed once Attempts &gt;= MaxAttempts.
    /// </summary>
    public static string ResolveCompleteStatus(string requestedStatus, int attempts, int maxAttempts)
    {
        if (requestedStatus == JobStatuses.Retrying && maxAttempts > 0 && attempts >= maxAttempts)
            return JobStatuses.Failed;
        return requestedStatus;
    }

    public static int BackoffSeconds(int attempts)
    {
        var n = Math.Max(0, attempts);
        return Math.Min(3600, (int)Math.Pow(2, n) * 15 + Random.Shared.Next(0, 10));
    }
}
