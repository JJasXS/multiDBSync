using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;
    private readonly IDatabaseRegistry _registry;

    public LogsModel(ITransactionEngine engine, IDatabaseRegistry registry)
    {
        _engine = engine;
        _registry = registry;
    }

    public List<SyncTransaction> Transactions { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<(string Code, string Name, string Src, string Dst, string DstPath)> Types { get; set; } = [];
    public DryRunResult? DryRun { get; set; }
    public string Message { get; set; } = "";

    [BindProperty] public int SourceDbId { get; set; }
    [BindProperty] public int DestDbId { get; set; }
    [BindProperty] public string TxnType { get; set; } = "SI_TO_PI";
    [BindProperty] public string SrcDoc { get; set; } = "";
    [BindProperty] public bool Confirmed { get; set; }

    public async Task<IActionResult> OnGetAsync(string? tab)
    {
        var t = (tab ?? "").Trim().ToLowerInvariant();
        if (t is "errors")
            return RedirectToPage("/Settings/Errors");
        if (t is "audit")
            return RedirectToPage("/Settings/Audit");

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        DryRun = await _engine.DryRunAsync(SourceDbId, DestDbId, TxnType, SrcDoc);
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!CanOperate) return Forbid();
        try
        {
            var (_, txnId, msg) = await _engine.EnqueueAsync(
                SourceDbId, DestDbId, TxnType, SrcDoc, dryRun: false, CurrentEmail, Confirmed);
            Message = string.IsNullOrEmpty(txnId) ? msg : $"{txnId}: {msg}";
        }
        catch (Exception ex)
        {
            Message = Helpers.FirebirdErrorMapper.Describe(ex);
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Transactions = await _engine.ListAsync();
        Databases = await _registry.ListAsync();
        Types = await _engine.ListTypesAsync();
    }
}
