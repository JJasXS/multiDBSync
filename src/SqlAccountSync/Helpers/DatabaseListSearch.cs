using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Match company list rows by display name, .FDB file name, and role.</summary>
public static class DatabaseListSearch
{
    public static bool Matches(
        string? displayName,
        string? fbPath,
        string? dbRole,
        string? query,
        string? roleFilter = null)
        => MatchesText(displayName, fbPath, query) && MatchesRole(dbRole, roleFilter);

    public static bool MatchesText(string? displayName, string? fbPath, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return true;

        var name = (displayName ?? "").Trim();
        var file = FdbFileName(fbPath);
        return name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || file.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesRole(string? dbRole, string? roleFilter)
    {
        var filter = (roleFilter ?? "").Trim();
        if (filter.Length == 0 || filter.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return true;

        var role = (dbRole ?? "").Trim();
        if (filter.Equals(DatabaseRoles.Master, StringComparison.OrdinalIgnoreCase))
            return role.Equals(DatabaseRoles.Master, StringComparison.OrdinalIgnoreCase)
                || role.Equals(DatabaseRoles.Both, StringComparison.OrdinalIgnoreCase);

        if (filter.Equals(DatabaseRoles.Sub, StringComparison.OrdinalIgnoreCase))
            return role.Equals(DatabaseRoles.Sub, StringComparison.OrdinalIgnoreCase)
                || role.Equals(DatabaseRoles.Both, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    public static string FdbFileName(string? path)
    {
        var raw = (path ?? "").Trim().Replace('\\', '/');
        if (raw.Length == 0)
            return "";
        var i = raw.LastIndexOf('/');
        return i >= 0 ? raw[(i + 1)..] : raw;
    }
}
