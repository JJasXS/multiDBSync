namespace SqlAccountSync.Pages.Settings;

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsNavItem> Items { get; } =
    [
        new(
            "Secret keys",
            "Manage secret key references used for SMTP, API credentials and other secure settings.",
            "lock",
            "/Settings/Security",
            "secrets password auth otp key security"),
        new(
            "User Management",
            "Add, edit and manage system users.",
            "users",
            "/Settings/Users",
            "accounts people"),
        new(
            "Error logs",
            "Review unresolved and past sync errors.",
            "alert",
            "/Settings/Errors",
            "failures exceptions troubleshooting"),
        new(
            "Audit logs",
            "See who changed configuration, users, and sync actions.",
            "clipboard",
            "/Settings/Audit",
            "history activity trail"),
    ];
}
