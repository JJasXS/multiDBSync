namespace SqlAccountSync.Pages.Settings;

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsNavItem> Items { get; } =
    [
        new(
            "User Management",
            "Manage who can sign in. Users are stored on the AWS login database SY_USER.",
            "users",
            "/Settings/Users",
            "accounts people login otp email"),
    ];
}
