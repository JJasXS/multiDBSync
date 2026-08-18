using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private readonly IErrorLogService _errors;
    private readonly IAuditService _audit;

    public LogsModel(IErrorLogService errors, IAuditService audit)
    {
        _errors = errors;
        _audit = audit;
    }

    public List<ErrorEntry> Errors { get; set; } = [];
    public List<AuditEntry> Audits { get; set; } = [];
    public string Tab { get; set; } = "errors";

    public async Task OnGetAsync(string? tab)
    {
        Tab = string.Equals(tab, "audit", StringComparison.OrdinalIgnoreCase) ? "audit" : "errors";
        Errors = await _errors.ListAsync();
        Audits = await _audit.ListAsync();
    }

    public async Task<IActionResult> OnPostResolveAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return RedirectToPage(new { tab = "errors" });
    }
}
