namespace SqlAccountSync.Services.Sync;

public sealed class AutoPushScheduleSnapshot
{
    public bool Active { get; init; }
    public int IntervalSeconds { get; init; }
    public long? NextDueUnixMs { get; init; }
    public int RemainingSeconds { get; init; }
}

public interface IAutoPushSchedule
{
    int IntervalSeconds { get; }
    void SetIntervalSeconds(int seconds);
    void ArmAfter(TimeSpan delay);
    void ArmNextFromNow();
    AutoPushScheduleSnapshot Snapshot(bool anyEnabled);
}

/// <summary>Shared next-run clock for the Push-page auto-push countdown.</summary>
public sealed class AutoPushSchedule : IAutoPushSchedule
{
    private readonly object _gate = new();
    private int _intervalSeconds = 30;
    private DateTimeOffset? _nextDueUtc;

    public int IntervalSeconds
    {
        get { lock (_gate) return _intervalSeconds; }
    }

    public void SetIntervalSeconds(int seconds)
    {
        var clamped = Math.Clamp(seconds <= 0 ? 30 : seconds, 15, 3600);
        lock (_gate)
            _intervalSeconds = clamped;
    }

    public void ArmAfter(TimeSpan delay)
    {
        lock (_gate)
            _nextDueUtc = DateTimeOffset.UtcNow.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    public void ArmNextFromNow() => ArmAfter(TimeSpan.FromSeconds(IntervalSeconds));

    public AutoPushScheduleSnapshot Snapshot(bool anyEnabled)
    {
        lock (_gate)
        {
            if (_nextDueUtc is null)
            {
                return new AutoPushScheduleSnapshot
                {
                    Active = anyEnabled,
                    IntervalSeconds = _intervalSeconds,
                    NextDueUnixMs = null,
                    RemainingSeconds = 0
                };
            }

            var remaining = (int)Math.Ceiling((_nextDueUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);
            if (remaining < 0)
                remaining = 0;

            return new AutoPushScheduleSnapshot
            {
                Active = anyEnabled,
                IntervalSeconds = _intervalSeconds,
                NextDueUnixMs = _nextDueUtc.Value.ToUnixTimeMilliseconds(),
                RemainingSeconds = remaining
            };
        }
    }
}
