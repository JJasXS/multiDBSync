using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages.Push;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly ISyncOrchestrator _sync;
    private readonly IAutoPushSettings _autoPush;
    private readonly IAutoPushSchedule _autoPushSchedule;

    public IndexModel(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        ISyncOrchestrator sync,
        IAutoPushSettings autoPush,
        IAutoPushSchedule autoPushSchedule)
    {
        _registry = registry;
        _read = read;
        _write = write;
        _sync = sync;
        _autoPush = autoPush;
        _autoPushSchedule = autoPushSchedule;
    }

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "push";
    [BindProperty(SupportsGet = true)] public int SourceDbId { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = "CUSTOMER";

    [BindProperty] public List<string> SelectedCodes { get; set; } = [];
    [BindProperty] public List<int> SelectedSubDbIds { get; set; } = [];

    [BindProperty] public string EditCode { get; set; } = "";
    [BindProperty] public string EditDescription { get; set; } = "";
    [BindProperty] public string EditDescription2 { get; set; } = "";
    [BindProperty] public string EditAccType { get; set; } = "";
    [BindProperty] public string EditSpecialAccType { get; set; } = "";
    [BindProperty] public string EditTax { get; set; } = "";
    [BindProperty] public int EditCashFlowType { get; set; }
    [BindProperty] public string EditSic { get; set; } = "";
    [BindProperty] public bool AutoPushCustomer { get; set; }
    [BindProperty] public bool AutoPushSupplier { get; set; }
    [BindProperty] public bool AutoPushAccount { get; set; }
    public bool AutoPushPanelOpen { get; set; }
    public bool AutoPushAny { get; set; }
    public AutoPushScheduleSnapshot AutoPushCountdown { get; set; } = new();

    public List<RegisteredDatabase> SourceDatabases { get; set; } = [];
    public List<RegisteredDatabase> TargetSubs { get; set; } = [];
    public List<MasterRecord> Records { get; set; } = [];
    public Dictionary<int, string> TargetEntityHints { get; set; } = new();
    public RegisteredDatabase? SelectedSource { get; set; }

    public FlashToastModel? FlashToast { get; set; }

    public bool IsAccountEntity =>
        string.Equals(EntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        Tab = "push";
        await LoadPushAsync();
    }

    public async Task<IActionResult> OnGetAutoPushStatusAsync()
    {
        var flags = await _autoPush.GetAsync().ConfigureAwait(false);
        if (_autoPushSchedule.Snapshot(true).NextDueUnixMs is null)
            _autoPushSchedule.ArmNextFromNow();
        var snap = _autoPushSchedule.Snapshot(flags.Any);
        return new JsonResult(new
        {
            active = snap.Active,
            intervalSeconds = snap.IntervalSeconds,
            nextDueUnixMs = snap.NextDueUnixMs,
            remainingSeconds = snap.RemainingSeconds
        });
    }

    public async Task<IActionResult> OnPostPushAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "push";
        EntityCode = NormalizeEntity(EntityCode);
        if (await AccountSourceRejectedAsync())
            return Page();

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

    public async Task<IActionResult> OnPostSaveAutoPushAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "push";
        try
        {
            var flags = new AutoPushFlags
            {
                Customer = AutoPushCustomer,
                Supplier = AutoPushSupplier,
                Account = AutoPushAccount
            };
            await _autoPush.SaveAsync(flags);
            if (flags.Any)
                _autoPushSchedule.ArmNextFromNow();
            FlashToast = FlashToasts.Ok("Auto-push settings saved.");
        }
        catch (Exception ex)
        {
            FlashToast = FlashToasts.Fail("Could not save auto-push settings.", [ex.Message]);
        }

        AutoPushPanelOpen = true;
        await LoadPushAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveEditAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "push";
        EntityCode = EntityCodes.Account;
        await LoadPushAsync();
        if (SelectedSource is null || !SyncTargetResolver.IsMasterOrBoth(SelectedSource))
        {
            FlashToast = FlashToasts.Fail("Select a Master (or BOTH) database as the source.");
            return Page();
        }

        FlashToast = await AccountChartEdit.SaveAsync(
            _read, _write, _sync, SelectedSource, TargetSubs, EditInput(), CurrentEmail);
        await LoadPushAsync();
        return Page();
    }

    private async Task LoadPushAsync()
    {
        await LoadAutoPushAsync();
        EntityCode = NormalizeEntity(EntityCode);
        var databases = await _registry.ListAsync();
        SourceDatabases = ListSources(databases);
        SourceDbId = PickSourceId(SourceDbId, SourceDatabases);

        SelectedSource = await _registry.GetAsync(SourceDbId);
        TargetSubs = await ResolveTargetsAsync(SourceDbId, databases);
        TargetEntityHints = BuildTargetHints(SelectedSource, TargetSubs);

        if (SourceDbId <= 0 || SelectedSource is null)
            return;

        Records = await LoadRecordsAsync(SelectedSource, EntityCode);
    }

    private async Task LoadAutoPushAsync()
    {
        var flags = await _autoPush.GetAsync();
        AutoPushCustomer = flags.Customer;
        AutoPushSupplier = flags.Supplier;
        AutoPushAccount = flags.Account;
        AutoPushAny = flags.Any;
        // Always expose the worker clock so the header timer can show even when toggles are off.
        if (_autoPushSchedule.Snapshot(true).NextDueUnixMs is null)
            _autoPushSchedule.ArmNextFromNow();
        AutoPushCountdown = _autoPushSchedule.Snapshot(flags.Any);
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
            case "ACCOUNT":
                return await LoadAccountsAsync(db);
            default:
                return [];
        }
    }

    private async Task<List<MasterRecord>> LoadAccountsAsync(RegisteredDatabase db)
    {
        try
        {
            return GlAccountMapper.ToMasterRecords(await _read.GetGlChartAsync(db));
        }
        catch
        {
            return [];
        }
    }

    private async Task<bool> AccountSourceRejectedAsync()
    {
        if (!IsAccountEntity)
            return false;
        var source = await _registry.GetAsync(SourceDbId);
        if (SyncTargetResolver.IsMasterOrBoth(source))
            return false;
        FlashToast = FlashToasts.Fail("Select a Master (or BOTH) database as the source.");
        await LoadPushAsync();
        return true;
    }

    private static List<RegisteredDatabase> ListSources(IEnumerable<RegisteredDatabase> databases) =>
        databases
            .Where(SyncTargetResolver.IsMasterOrBoth)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int PickSourceId(int current, List<RegisteredDatabase> sources)
    {
        if (current > 0 && sources.Any(d => d.DbId == current))
            return current;
        return sources.FirstOrDefault(SyncTargetResolver.IsMasterOrBoth)?.DbId
            ?? sources.FirstOrDefault()?.DbId
            ?? 0;
    }

    private AccountEditInput EditInput() => new()
    {
        Code = EditCode,
        Description = EditDescription,
        Description2 = EditDescription2,
        AccType = EditAccType,
        SpecialAccType = EditSpecialAccType,
        Tax = EditTax,
        CashFlowType = EditCashFlowType,
        Sic = EditSic
    };
}
