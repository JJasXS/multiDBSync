using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Tenants;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IJobQueue _jobs;
    private readonly IMappingService _mapping;
    private readonly ISyncOrchestrator _sync;
    private readonly ITransactionEngine _txns;
    private readonly IErrorLogService _errors;
    private readonly IAuditService _audit;
    private readonly ILoginUserStore _users;
    private readonly ITenantDatabaseBootstrap _tenants;
    private readonly SyncOptions _syncOptions;
    private readonly CentralDb _db;

    public IndexModel(
        IDatabaseRegistry registry,
        IJobQueue jobs,
        IMappingService mapping,
        ISyncOrchestrator sync,
        ITransactionEngine txns,
        IErrorLogService errors,
        IAuditService audit,
        ILoginUserStore users,
        ITenantDatabaseBootstrap tenants,
        IOptions<SyncOptions> syncOptions,
        CentralDb db)
    {
        _registry = registry;
        _jobs = jobs;
        _mapping = mapping;
        _sync = sync;
        _txns = txns;
        _errors = errors;
        _audit = audit;
        _users = users;
        _tenants = tenants;
        _syncOptions = syncOptions.Value;
        _db = db;
    }

    public DashboardStats Stats { get; set; } = new();
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
    public List<SyncJob> RecentJobs { get; set; } = [];
    public List<EntityMapping> Conflicts { get; set; } = [];
    public List<SyncTransaction> RecentTxns { get; set; } = [];
    public List<ErrorEntry> UnresolvedErrors { get; set; } = [];
    public List<AuditEntry> RecentAudit { get; set; } = [];
    public List<AppUser> Users { get; set; } = [];
    public int MappedCount { get; set; }
    public int EnabledRuleCount { get; set; }
    public int SettingsKeyCount { get; set; }
    public bool CentralConfigured { get; set; }
    public int? MaxDatabases { get; set; }
    public bool DatabaseCapReached => MaxDatabases is int cap && Databases.Count >= cap;
    public QueueHealthSnapshot QueueHealth { get; set; } = new();
    public FlashToastModel? FlashToast { get; set; }
    public int ActiveJobCount => QueueHealth.Pending + QueueHealth.Processing;

    public async Task OnGetAsync()
    {
        var tenant = (_syncOptions.TenantCode ?? "").Trim();
        var registryTask = _registry.ReadDashboardRegistryAsync();
        var maxTask = _tenants.GetMaxDatabasesAsync(tenant);
        var jobsTask = _jobs.ListAsync(take: 8);
        var healthTask = _jobs.GetHealthAsync();
        var mappedTask = _mapping.CountMappedAsync();
        var conflictsTask = _mapping.ConflictsAsync();
        var rulesTask = _sync.CountEnabledRulesAsync();
        var txnsTask = _txns.ListAsync(take: 5);
        var errorsTask = _errors.ListAsync(unresolvedOnly: true, take: 5);
        var auditTask = _audit.ListAsync(take: 5);
        var usersTask = _users.ListAsync();
        var settingsTask = _db.IsConfigured
            ? _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_SETTING")
            : Task.FromResult<object?>(0);

        await Task.WhenAll(
            registryTask, maxTask, jobsTask, healthTask,
            mappedTask, conflictsTask, rulesTask, txnsTask, errorsTask,
            auditTask, usersTask, settingsTask);

        var registry = await registryTask;
        CentralConfigured = registry.Configured;
        Stats = registry.Stats;
        Databases = registry.Databases;
        Links = registry.Links;
        MaxDatabases = await maxTask;
        RecentJobs = await jobsTask;
        QueueHealth = await healthTask;
        MappedCount = await mappedTask;
        Conflicts = await conflictsTask;
        EnabledRuleCount = await rulesTask;
        RecentTxns = await txnsTask;
        UnresolvedErrors = await errorsTask;
        RecentAudit = await auditTask;
        Users = await usersTask;
        SettingsKeyCount = Convert.ToInt32(await settingsTask ?? 0);
    }

    public async Task<IActionResult> OnPostStopAllJobsAsync()
    {
        if (!CanOperate)
            return Forbid();

        var stopped = await _jobs.CancelAllActiveAsync().ConfigureAwait(false);
        FlashToast = stopped == 0
            ? FlashToasts.Ok("No active jobs to stop.")
            : FlashToasts.Ok($"Stopped {stopped} job{(stopped == 1 ? "" : "s")}.");
        await OnGetAsync().ConfigureAwait(false);
        return Page();
    }
}
