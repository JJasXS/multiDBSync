using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Sync;
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

    public async Task OnGetAsync()
    {
        CentralConfigured = _db.IsConfigured;
        Stats = await _registry.DashboardAsync();
        Databases = await _registry.ListAsync();
        Links = await _registry.ListLinksAsync();
        RecentJobs = await _jobs.ListAsync(take: 8);
        await LoadSummariesAsync();
    }

    private async Task LoadSummariesAsync()
    {
        MappedCount = await _mapping.CountMappedAsync();
        Conflicts = await _mapping.ConflictsAsync();
        EnabledRuleCount = await _sync.CountEnabledRulesAsync();
        RecentTxns = await _txns.ListAsync(take: 5);
        UnresolvedErrors = await _errors.ListAsync(unresolvedOnly: true, take: 5);
        RecentAudit = await _audit.ListAsync(take: 5);
        Users = await _users.ListAsync();
        if (!CentralConfigured)
            return;
        SettingsKeyCount = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_SETTING") ?? 0);
    }
}
