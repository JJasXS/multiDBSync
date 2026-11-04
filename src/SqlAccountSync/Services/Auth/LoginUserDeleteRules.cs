using SqlAccountSync.Authorization;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Auth;

/// <summary>Pure guards for website login-user deletion (SY_USER VIEWER rows only).</summary>
public static class LoginUserDeleteRules
{
    public static void EnsureCanDelete(AppUser user, int currentUserId, string? currentEmail)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (currentUserId > 0 && user.UserId == currentUserId)
            throw new InvalidOperationException("You cannot delete your own account.");

        var me = (currentEmail ?? "").Trim();
        var them = (user.Email ?? "").Trim();
        if (me.Length > 0 && them.Length > 0
            && string.Equals(them, me, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("You cannot delete your own account.");

        if (AppRoles.Rank(user.Role) >= AppRoles.Rank(AppRoles.Admin)
            || !string.Equals(AppRoles.Normalize(user.Role), AppRoles.Viewer, StringComparison.Ordinal))
            throw new InvalidOperationException("Only VIEWER accounts can be deleted.");
    }

    public static bool IsDeletableViewer(AppUser user) =>
        user is not null
        && AppRoles.Rank(user.Role) < AppRoles.Rank(AppRoles.Admin)
        && string.Equals(AppRoles.Normalize(user.Role), AppRoles.Viewer, StringComparison.Ordinal);
}
