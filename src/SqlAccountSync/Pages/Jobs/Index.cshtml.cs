using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages.Jobs;

public class IndexModel : SyncPageModel
{
    private readonly IJobQueue _jobs;
    private readonly IDatabaseRegistry _registry;
    private readonly ISyncOrchestrator _sync;
    private readonly IAuditService _audit;

    public IndexModel(IJobQueue jobs, IDatabaseRegistry registry, ISyncOrchestrator sync, IAuditService audit)
    {
        _jobs = jobs;
        _registry = registry;
        _sync = sync;
        _audit = audit;
    }

    public List<SyncJob> Jobs { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public DryRunResult? DryRun { get; set; }
    public string Message { get; set; } = "";
    [BindProperty] public int MasterDbId { get; set; }
    [BindProperty] public int SubDbId { get; set; }
    [BindProperty] public string EntityCode { get; set; } = "ITEM";
    [BindProperty] public string MasterCode { get; set; } = "";

    public async Task OnGetAsync(string? status)
    {
        Jobs = await _jobs.ListAsync(status);
        Databases = await _registry.ListAsync();
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        DryRun = await _sync.DryRunMasterAsync(MasterDbId, SubDbId, EntityCode, MasterCode);
        await OnGetAsync(null);
        return Page();
    }

    public async Task<IActionResult> OnPostEnqueueAsync()
    {
        if (!CanOperate) return Forbid();
        var (id, msg) = await _sync.EnqueueMasterAsync(MasterDbId, SubDbId, EntityCode, MasterCode, dryRun: false, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER EXECUTED SYNC", RecordId = id.ToString(), Result = "QUEUED" });
        Message = msg;
        await OnGetAsync(null);
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(int jobId)
    {
        if (!CanOperate) return Forbid();
        await _jobs.CancelAsync(jobId);
        return RedirectToPage();
    }
}
