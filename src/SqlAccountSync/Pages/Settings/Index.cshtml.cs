namespace SqlAccountSync.Pages.Settings;

public class IndexModel : SyncPageModel
{
    public IReadOnlyList<SettingsNavItem> Items { get; private set; } = SettingsCatalog.Items;

    public void OnGet()
    {
        Items = SettingsCatalog.Items;
    }
}
