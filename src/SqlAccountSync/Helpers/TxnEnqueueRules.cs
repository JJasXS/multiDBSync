namespace SqlAccountSync.Helpers;

/// <summary>Pure rules for enqueue against an existing COMPLETED SYNC_TXN row.</summary>
public static class TxnEnqueueRules
{
    public enum CompletedOutcome
    {
        AlreadyDone,
        ReopenStale
    }

    public static CompletedOutcome DecideCompleted(bool destinationReallyExists) =>
        destinationReallyExists ? CompletedOutcome.AlreadyDone : CompletedOutcome.ReopenStale;
}
