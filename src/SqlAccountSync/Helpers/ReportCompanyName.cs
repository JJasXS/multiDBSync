namespace SqlAccountSync.Helpers;

/// <summary>Company labels shown on Master Report pages.</summary>
public static class ReportCompanyName
{
    public static string Label(string? displayName) => (displayName ?? "").Trim();
}
