using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Data;

namespace SqlAccountSync.Pages.Settings;

public class SecurityModel : SyncPageModel
{
    private readonly CentralDb _db;
    public SecurityModel(CentralDb db) => _db = db;

    public Dictionary<string, string> Settings { get; set; } = new();
    [BindProperty] public string Key { get; set; } = "";
    [BindProperty] public string Value { get; set; } = "";
    public string Message { get; set; } = "";
    public bool MessageOk { get; set; }
    public bool DbConfigured => _db.IsConfigured;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSaveSecretAsync()
    {
        if (!IsAdmin) return Forbid();
        if (!_db.IsConfigured)
        {
            Message = "Central database is not configured.";
            MessageOk = false;
            await LoadAsync();
            return Page();
        }

        var key = (Key ?? "").Trim();
        var value = (Value ?? "").Trim();
        if (key.Length == 0)
        {
            Message = "Key is required.";
            MessageOk = false;
            await LoadAsync();
            return Page();
        }

        await _db.ExecuteAsync(@"
UPDATE OR INSERT INTO SYNC_SETTING (SETKEY, SETVALUE, UPDATEDAT)
VALUES (@SETKEY, @SETVALUE, CURRENT_TIMESTAMP)
MATCHING (SETKEY)", new { SETKEY = key, SETVALUE = value });

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        if (!_db.IsConfigured) return;
        var rows = await _db.QueryAsync("SELECT SETKEY, SETVALUE FROM SYNC_SETTING ORDER BY SETKEY",
            r => (CentralDb.Str(r, "SETKEY"), CentralDb.Str(r, "SETVALUE")));
        Settings = rows.ToDictionary(x => x.Item1, x => x.Item2);
    }
}
