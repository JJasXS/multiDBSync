namespace SqlAccountSync.Helpers;

/// <summary>Rules for background auto-push job enqueue / retry reuse.</summary>
public static class AutoPushJobRules
{
    public const string PartyUser = "system:auto-push";
    public const string AccountUser = "system:account-auto-push";

    public static bool IsAutoPushUser(string? createdBy) =>
        string.Equals(createdBy, PartyUser, StringComparison.OrdinalIgnoreCase)
        || string.Equals(createdBy, AccountUser, StringComparison.OrdinalIgnoreCase);
}
