using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private readonly IErrorLogService _errors;
    private readonly IAuditService _audit;
    private readonly ITransactionEngine _engine;
    private readonly IDatabaseRegistry _registry;

    public LogsModel(
        IErrorLogService errors,
        IAuditService audit,
        ITransactionEngine engine,
        IDatabaseRegistry registry)
    {
        _errors = errors;
        _audit = audit;
        _engine = engine;
        _registry = registry;
    }

    public List<ErrorEntry> Errors { get; set; } = [];
    public List<AuditEntry> Audits { get; set; } = [];
    public List<SyncTransaction> Transactions { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<(string Code, string Name, string Src, string Dst, string DstPath)> Types { get; set; } = [];
    public DryRunResult? DryRun { get; set; }
    public string Message { get; set; } = "";
    public string Tab { get; set; } = "errors";

    [BindProperty] public int SourceDbId { get; set; }
    [BindProperty] public int DestDbId { get; set; }
    [BindProperty] public string TxnType { get; set; } = "SI_TO_PI";
    [BindProperty] public string SrcDoc { get; set; } = "";
    [BindProperty] public bool Confirmed { get; set; }

    public async Task OnGetAsync(string? tab)
    {
        Tab = NormalizeTab(tab);
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostResolveAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return RedirectToPage(new { tab = "errors" });
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "transactions";
        DryRun = await _engine.DryRunAsync(SourceDbId, DestDbId, TxnType, SrcDoc);
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "transactions";
        var (_, txnId, msg) = await _engine.EnqueueAsync(
            SourceDbId, DestDbId, TxnType, SrcDoc, dryRun: false, CurrentEmail, Confirmed);
        Message = string.IsNullOrEmpty(txnId) ? msg : $"{txnId}: {msg}";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage(new { tab = "transactions" });
    }

    private async Task LoadAsync()
    {
        Errors = await _errors.ListAsync();
        Audits = await _audit.ListAsync();
        Transactions = await _engine.ListAsync();
        Databases = await _registry.ListAsync();
        Types = await _engine.ListTypesAsync();
    }

    private static string NormalizeTab(string? tab)
    {
        var t = (tab ?? "").Trim().ToLowerInvariant();
        return t is "audit" or "transactions" ? t : "errors";
    }
}
