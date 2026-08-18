using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages.Transactions;

public class IndexModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;
    private readonly IDatabaseRegistry _registry;

    public IndexModel(ITransactionEngine engine, IDatabaseRegistry registry)
    {
        _engine = engine;
        _registry = registry;
    }

    public List<SyncTransaction> Items { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<(string Code, string Name, string Src, string Dst, string DstPath)> Types { get; set; } = [];
    public DryRunResult? DryRun { get; set; }
    public string Message { get; set; } = "";
    [BindProperty] public int SourceDbId { get; set; }
    [BindProperty] public int DestDbId { get; set; }
    [BindProperty] public string TxnType { get; set; } = "SI_TO_PI";
    [BindProperty] public string SrcDoc { get; set; } = "";
    [BindProperty] public bool Confirmed { get; set; }

    public async Task OnGetAsync()
    {
        Items = await _engine.ListAsync();
        Databases = await _registry.ListAsync();
        Types = await _engine.ListTypesAsync();
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        DryRun = await _engine.DryRunAsync(SourceDbId, DestDbId, TxnType, SrcDoc);
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!CanOperate) return Forbid();
        var (_, txnId, msg) = await _engine.EnqueueAsync(SourceDbId, DestDbId, TxnType, SrcDoc, dryRun: false, CurrentEmail, Confirmed);
        Message = string.IsNullOrEmpty(txnId) ? msg : $"{txnId}: {msg}";
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage();
    }
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
