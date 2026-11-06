using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Master-data jobs must source from MASTER/BOTH only.
/// SUB edits must never enqueue push jobs (avoids SUB→master loops).
/// </summary>
public static class MasterEnqueueGuards
{
    public const string RejectSubSourceMessage =
        "Source must be MASTER or BOTH. SUB databases cannot drive create/update/delete sync.";

    public static bool IsAllowedSource(RegisteredDatabase? source) =>
        SyncTargetResolver.IsMasterOrBoth(source);

    /// <summary>Null when allowed; otherwise a user-facing reject reason.</summary>
    public static string? RejectReason(RegisteredDatabase? source) =>
        IsAllowedSource(source) ? null : RejectSubSourceMessage;
}
