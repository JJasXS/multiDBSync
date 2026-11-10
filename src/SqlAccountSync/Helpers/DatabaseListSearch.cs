namespace SqlAccountSync.Helpers;

/// <summary>Match company list rows by display name or .FDB file name.</summary>
public static class DatabaseListSearch
{
    public static bool Matches(string? displayName, string? fbPath, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return true;

        var name = (displayName ?? "").Trim();
        var file = FdbFileName(fbPath);
        return name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || file.Contains(q, StringComparison.OrdinalIgnoreCase);
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
