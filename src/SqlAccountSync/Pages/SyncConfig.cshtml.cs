using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages;

public class SyncConfigModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISyncOrchestrator _sync;
    private readonly IAuditService _audit;

    public SyncConfigModel(IDatabaseRegistry registry, ISyncOrchestrator sync, IAuditService audit)
    {
        _registry = registry;
        _sync = sync;
        _audit = audit;
    }

    public List<DatabaseLink> Links { get; set; } = [];
    public List<SyncRule> Rules { get; set; } = [];
    public int DatabaseCount { get; set; }

    [BindProperty] public int LinkId { get; set; }
    [BindProperty] public int RuleId { get; set; }
    [BindProperty] public string EntityCode { get; set; } = "ITEM";
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public string Direction { get; set; } = "MASTER_TO_SUB";
    [BindProperty] public string Mode { get; set; } = "MANUAL";
    [BindProperty] public string RecordCode { get; set; } = "";
    [BindProperty] public bool ScopeEnabled { get; set; } = true;

    public async Task OnGetAsync(int? linkId)
    {
        Links = await _registry.ListLinksAsync();
        DatabaseCount = (await _registry.ListAsync()).Count;
        LinkId = linkId ?? Links.FirstOrDefault()?.LinkId ?? 0;
        if (LinkId > 0)
            Rules = await _sync.ListRulesAsync(LinkId);
    }

    public async Task<IActionResult> OnPostSaveRuleAsync()
    {
        if (!CanManage) return Forbid();
        await _sync.SaveRuleAsync(new SyncRule
        {
            RuleId = RuleId,
            LinkId = LinkId,
            EntityCode = EntityCode,
            Enabled = Enabled,
            Direction = Direction,
            Mode = Mode,
            ConflictStrategy = "REQUIRE_ADMIN"
        });
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CHANGED SYNC CONFIGURATION", Result = "SUCCESS", Entity = EntityCode });
        return RedirectToPage(new { linkId = LinkId });
    }

    public async Task<IActionResult> OnPostSaveScopeAsync()
    {
        if (!CanManage) return Forbid();
        await _sync.SaveScopeAsync(RuleId, RecordCode, ScopeEnabled);
        return RedirectToPage(new { linkId = LinkId });
    }
}
