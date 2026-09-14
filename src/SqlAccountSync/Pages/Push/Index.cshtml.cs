using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages.Push;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISyncOrchestrator _sync;
    private readonly IJobQueue _jobs;
    private readonly IAuditService _audit;

    public IndexModel(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        IJobQueue jobs,
        IAuditService audit)
    {
        _registry = registry;
        _read = read;
        _sync = sync;
        _jobs = jobs;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "push";
    [BindProperty(SupportsGet = true)] public int SourceDbId { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = "CUSTOMER";

    [BindProperty] public List<string> SelectedCodes { get; set; } = [];
    [BindProperty] public List<int> SelectedSubDbIds { get; set; } = [];

    [BindProperty] public int MasterDbId { get; set; }
    [BindProperty] public int SubDbId { get; set; }
    [BindProperty] public string JobEntityCode { get; set; } = "CUSTOMER";
    [BindProperty] public string MasterCode { get; set; } = "";

    public List<RegisteredDatabase> SourceDatabases { get; set; } = [];
    public List<RegisteredDatabase> TargetSubs { get; set; } = [];
    public List<MasterRecord> Records { get; set; } = [];
    public Dictionary<int, string> TargetEntityHints { get; set; } = new();
    public RegisteredDatabase? SelectedSource { get; set; }

    public List<SyncJob> Jobs { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public DryRunResult? DryRun { get; set; }

    public FlashToastModel? FlashToast { get; set; }

    public bool IsJobsTab => string.Equals(NormalizeTab(Tab), "jobs", StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync(string? status)
    {
        Tab = NormalizeTab(Tab);
        if (IsJobsTab)
        {
            await LoadJobsAsync(status);
            return;
        }

        await LoadPushAsync();
    }

    public async Task<IActionResult> OnPostPushAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "push";
        EntityCode = NormalizeEntity(EntityCode);

        var (queued, failed, messages) = await _sync.EnqueueMasterBatchAsync(
            SourceDbId,
            SelectedSubDbIds,
            EntityCode,
            SelectedCodes,
            dryRun: false,
            CurrentEmail);

        if (queued == 0 && failed == 0)
        {
            FlashToast = new FlashToastModel
            {
                Title = messages.FirstOrDefault() ?? "Nothing queued.",
                Ok = false
            };
        }
        else
        {
            var lines = messages;
            if (lines.Count > 12)
            {
                lines = [.. lines.Take(12), $"+{messages.Count - 12} more"];
            }

            FlashToast = new FlashToastModel
            {
                Title = $"Queued {queued} job{(queued == 1 ? "" : "s")}."
                    + (failed > 0 ? $" {failed} could not be queued." : ""),
                Lines = lines,
                Ok = queued > 0 && failed == 0
            };
        }

        await LoadPushAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "jobs";
        JobEntityCode = NormalizeEntity(JobEntityCode);
        DryRun = await _sync.DryRunMasterAsync(MasterDbId, SubDbId, JobEntityCode, MasterCode);
        await LoadJobsAsync(null);
        return Page();
    }

    public async Task<IActionResult> OnPostEnqueueAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "jobs";
        JobEntityCode = NormalizeEntity(JobEntityCode);
        var (id, msg) = await _sync.EnqueueMasterAsync(MasterDbId, SubDbId, JobEntityCode, MasterCode, dryRun: false, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = "USER EXECUTED SYNC",
            RecordId = id.ToString(CultureInfo.InvariantCulture),
            Result = "QUEUED",
            RequestId = id.ToString(CultureInfo.InvariantCulture)
        });
        FlashToast = new FlashToastModel
        {
            Title = msg,
            Ok = id > 0
        };
        await LoadJobsAsync(null);
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(int jobId)
    {
        if (!CanOperate) return Forbid();
        await _jobs.CancelAsync(jobId);
        return RedirectToPage(new { tab = "jobs" });
    }

    public async Task<IActionResult> OnPostUnlockAsync(int jobId)
    {
        if (!CanOperate) return Forbid();
        await _jobs.UnlockProcessingAsync(jobId);
        return RedirectToPage(new { tab = "jobs" });
    }

    private async Task LoadPushAsync()
    {
        EntityCode = NormalizeEntity(EntityCode);
        var databases = await _registry.ListAsync();
        SourceDatabases = databases.OrderBy(d => d.DisplayName).ToList();

        if (SourceDbId == 0)
            SourceDbId = SourceDatabases.FirstOrDefault(d => d.DbRole is "MASTER" or "BOTH")?.DbId
                         ?? SourceDatabases.FirstOrDefault()?.DbId ?? 0;

        SelectedSource = await _registry.GetAsync(SourceDbId);
        TargetSubs = await ResolveTargetsAsync(SourceDbId, databases);
        TargetEntityHints = BuildTargetHints(SelectedSource, TargetSubs);

        if (SourceDbId <= 0 || SelectedSource is null)
            return;

        Records = await LoadRecordsAsync(SelectedSource, EntityCode);
    }

    private async Task LoadJobsAsync(string? status)
    {
        Jobs = await _jobs.ListAsync(status);
        Databases = await _registry.ListAsync();
    }

    private async Task<List<RegisteredDatabase>> ResolveTargetsAsync(int sourceDbId, List<RegisteredDatabase> databases)
    {
        if (sourceDbId <= 0)
            return [];

        var source = databases.FirstOrDefault(d => d.DbId == sourceDbId);
        if (source is null)
            return [];

        var links = await _registry.ListLinksAsync();
        return SyncTargetResolver.ResolveLinkedSubs(source, databases, links);
    }

    private Dictionary<int, string> BuildTargetHints(RegisteredDatabase? source, List<RegisteredDatabase> targets)
    {
        var hints = new Dictionary<int, string>();
        if (source is null || !PartnerSyncRules.IsPartyEntity(EntityCode))
            return hints;

        foreach (var dest in targets)
        {
            var destEntity = PartnerSyncRules.ResolveTargetEntity(source, dest, EntityCode);
            if (!string.Equals(destEntity, EntityCode, StringComparison.OrdinalIgnoreCase))
                hints[dest.DbId] = $"Creates {destEntity} on {dest.DisplayName}";
        }
        return hints;
    }

    private static string NormalizeTab(string? tab) =>
        string.Equals(tab, "jobs", StringComparison.OrdinalIgnoreCase) ? "jobs" : "push";

    private static string NormalizeEntity(string entity) =>
        PushableEntityCodes.NormalizePush(entity, EntityCodes.Customer);

    private async Task<List<MasterRecord>> LoadRecordsAsync(RegisteredDatabase db, string entity)
    {
        switch (entity.ToUpperInvariant())
        {
            case "ITEM":
                return await _read.GetItemsAsync(db);
            case "CUSTOMER":
                return await _read.GetCustomersAsync(db);
            case "SUPPLIER":
                return await _read.GetSuppliersAsync(db);
            default:
                return [];
        }
    }
}
