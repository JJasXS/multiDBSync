using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Data;

namespace SqlAccountSync.Pages.Settings;

public class IndexModel : SyncPageModel
{
    private readonly CentralDb _db;
    public IndexModel(CentralDb db) => _db = db;

    public Dictionary<string, string> Settings { get; set; } = new();
    [BindProperty] public string Key { get; set; } = "";
    [BindProperty] public string Value { get; set; } = "";
    public bool DbConfigured => _db.IsConfigured;

    public async Task OnGetAsync()
    {
        if (!_db.IsConfigured) return;
        var rows = await _db.QueryAsync("SELECT SETKEY, SETVALUE FROM SYNC_SETTING ORDER BY SETKEY",
            r => (CentralDb.Str(r, "SETKEY"), CentralDb.Str(r, "SETVALUE")));
        Settings = rows.ToDictionary(x => x.Item1, x => x.Item2);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        if (!_db.IsConfigured) return Page();
        await _db.ExecuteAsync(@"
UPDATE OR INSERT INTO SYNC_SETTING (SETKEY, SETVALUE, UPDATEDAT)
VALUES (@SETKEY, @SETVALUE, CURRENT_TIMESTAMP)
MATCHING (SETKEY)", new { SETKEY = Key.Trim(), SETVALUE = Value.Trim() });
        return RedirectToPage();
    }
}
