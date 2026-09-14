namespace SqlAccountSync.Pages.Settings;

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsNavItem> Items { get; } =
    [
        new(
            "User Management",
            "Manage who can sign in (SYNC roles). Login emails must exist on LOGIN.FDB SY_USER.",
            "users",
            "/Settings/Users",
            "accounts people login otp email"),
    ];
}
