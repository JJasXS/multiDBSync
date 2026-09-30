namespace SqlAccountSync.Authorization;

public static class AppRoles
{
    public const string Admin = "ADMIN";
    public const string Viewer = "VIEWER";

    public static readonly string[] All = [Admin, Viewer];

    public static string Normalize(string? role) =>
        string.Equals(role?.Trim(), Admin, StringComparison.OrdinalIgnoreCase) ? Admin : Viewer;

    public static bool IsAtLeast(string? role, string required)
    {
        var rank = Rank(role);
        return rank >= Rank(required);
    }

    public static int Rank(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        Admin => 2,
        Viewer => 1,
        _ => 0
    };
}

public static class PolicyNames
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
}
