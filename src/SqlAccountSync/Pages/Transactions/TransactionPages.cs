using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages.Transactions;

public class IndexModel : SyncPageModel
{
    public IActionResult OnGet() => Redirect("/logs?tab=transactions");
}

public class DetailsModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;

    public DetailsModel(ITransactionEngine engine) => _engine = engine;

    public SyncTransaction? Txn { get; set; }
    public List<TxnEvent> Events { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Txn = await _engine.GetAsync(id);
        if (Txn is null) return RedirectToPage("Index");
        Events = await _engine.TimelineAsync(id);
        return Page();
    }
}
