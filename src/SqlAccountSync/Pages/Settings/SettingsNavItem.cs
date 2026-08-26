namespace SqlAccountSync.Pages.Settings;

public sealed record SettingsNavItem(
    string Title,
    string Description,
    string Icon,
    string Href,
    string? SearchExtra = null);
