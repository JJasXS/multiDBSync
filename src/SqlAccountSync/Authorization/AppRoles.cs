namespace SqlAccountSync.Authorization;

public static class AppRoles
{
    public const string Admin = "ADMIN";
    public const string Manager = "MANAGER";
    public const string Operator = "OPERATOR";
    public const string Viewer = "VIEWER";

    public static readonly string[] All = [Admin, Manager, Operator, Viewer];

    public static bool IsAtLeast(string? role, string required)
    {
        var rank = Rank(role);
        return rank >= Rank(required);
    }

    public static int Rank(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        Admin => 4,
        Manager => 3,
        Operator => 2,
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
