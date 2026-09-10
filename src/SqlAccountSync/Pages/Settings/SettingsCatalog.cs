namespace SqlAccountSync.Pages.Settings;

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsNavItem> Items { get; } =
    [
        new(
            "User Management",
            "Add, edit and manage system users.",
            "users",
            "/Settings/Users",
            "accounts people"),
    ];
}
