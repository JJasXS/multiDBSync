namespace SqlAccountSync.Helpers;

/// <summary>Company labels shown on Master Report pages.</summary>
public static class ReportCompanyName
{
    public static string Label(string? displayName)
    {
        var name = (displayName ?? "").Trim();
        const string suffix = " database";
        if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var stripped = name[..^suffix.Length].TrimEnd();
            if (stripped.Length > 0)
                return stripped;
        }
        return name;
    }
}
