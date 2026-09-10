using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;
    private readonly IAuditService _audit;
    private readonly IErrorLogService _errors;

    public LogsModel(
        ITransactionEngine engine,
        IAuditService audit,
        IErrorLogService errors)
    {
        _engine = engine;
        _audit = audit;
        _errors = errors;
    }

    public List<SyncTransaction> Transactions { get; set; } = [];
    public List<AuditListItem> Audits { get; set; } = [];
    public List<ErrorEntry> Errors { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(string? tab)
    {
        var t = (tab ?? "").Trim().ToLowerInvariant();
        if (t is "transactions" or "transfer" or "docs" or "documents")
            return RedirectToPage("/Transactions/Index");
        if (t is "errors")
            return Redirect("/logs#error-logs");
        if (t is "audit")
            return Redirect("/logs#audit-logs");

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResolveErrorAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return Redirect("/logs#error-logs");
    }

    private async Task LoadAsync()
    {
        Transactions = await _engine.ListAsync();
        Audits = await _audit.ListDetailedAsync();
        Errors = await _errors.ListAsync();
    }
}
