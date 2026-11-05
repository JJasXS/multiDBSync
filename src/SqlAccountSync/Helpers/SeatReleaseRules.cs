namespace SqlAccountSync.Helpers;

/// <summary>
/// Pure decision for freeing the SQL Account concurrent-user seat after a write.
/// Never returns a "login home" action — keep-window means logout only.
/// </summary>
public static class SeatReleaseRules
{
    public enum Action
    {
        /// <summary>Do nothing (both LogoutAfterUse and ReleaseSqlAccAfterUse are off).</summary>
        None,

        /// <summary>Logout; leave the user's SQLACC window open (no kill).</summary>
        LogoutOnly,

        /// <summary>Logout and close SQLACC.exe that sync opened.</summary>
        LogoutAndClose
    }

    public static Action Decide(bool attachedToUserWindow, bool logoutAfterUse, bool releaseAfterUse)
    {
        if (attachedToUserWindow)
            return logoutAfterUse || releaseAfterUse ? Action.LogoutOnly : Action.None;

        if (logoutAfterUse && releaseAfterUse)
            return Action.LogoutAndClose;

        if (logoutAfterUse)
            return Action.LogoutOnly;

        if (releaseAfterUse)
            return Action.LogoutAndClose;

        return Action.None;
    }
}
