using Microsoft.AspNetCore.Mvc;

using SqlAccountSync.Helpers;

using SqlAccountSync.Models;

using SqlAccountSync.Services.Databases;

using SqlAccountSync.Services.SqlAccount;

using SqlAccountSync.Services.Sync;



namespace SqlAccountSync.Pages.Push;



public class IndexModel : SyncPageModel

{

    private readonly IDatabaseRegistry _registry;

    private readonly ISqlAccountReadAdapter _read;

    private readonly ISyncOrchestrator _sync;



    public IndexModel(IDatabaseRegistry registry, ISqlAccountReadAdapter read, ISyncOrchestrator sync)

    {

        _registry = registry;

        _read = read;

        _sync = sync;

    }



    [BindProperty(SupportsGet = true)] public int SourceDbId { get; set; }

    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = "CUSTOMER";

    [BindProperty] public List<string> SelectedCodes { get; set; } = [];

    [BindProperty] public List<int> SelectedSubDbIds { get; set; } = [];



    public List<RegisteredDatabase> SourceDatabases { get; set; } = [];

    public List<RegisteredDatabase> TargetSubs { get; set; } = [];

    public List<MasterRecord> Records { get; set; } = [];

    public Dictionary<int, string> TargetEntityHints { get; set; } = new();

    public RegisteredDatabase? SelectedSource { get; set; }

    public string Message { get; set; } = "";

    public bool MessageOk { get; set; }



    public async Task OnGetAsync()

    {

        await LoadAsync();

    }



    public async Task<IActionResult> OnPostPushAsync()

    {

        if (!CanOperate) return Forbid();

        EntityCode = NormalizeEntity(EntityCode);

        var (queued, failed, messages) = await _sync.EnqueueMasterBatchAsync(

            SourceDbId,

            SelectedSubDbIds,

            EntityCode,

            SelectedCodes,

            dryRun: false,

            CurrentEmail);



        MessageOk = queued > 0 && failed == 0;

        Message = queued == 0 && failed == 0

            ? string.Join(" ", messages)

            : $"Queued {queued} job(s)" + (failed > 0 ? $", {failed} failed." : ".")

              + (messages.Count == 0 ? "" : " " + string.Join(" ", messages.Take(8)));

        if (messages.Count > 8)

            Message += $" … (+{messages.Count - 8} more)";



        await LoadAsync();

        return Page();

    }



    private async Task LoadAsync()

    {

        EntityCode = NormalizeEntity(EntityCode);

        var databases = await _registry.ListAsync();

        SourceDatabases = databases

            .OrderBy(d => d.DisplayName)

            .ToList();



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



    private async Task<List<RegisteredDatabase>> ResolveTargetsAsync(int sourceDbId, List<RegisteredDatabase> databases)

    {

        if (sourceDbId <= 0)

            return [];



        var source = databases.FirstOrDefault(d => d.DbId == sourceDbId);

        if (source is null)

            return [];



        var links = await _registry.ListLinksAsync();

        if (source.DbRole is "MASTER" or "BOTH")

        {

            var linkedSubIds = links

                .Where(l => l.MasterDbId == sourceDbId && l.IsActive)

                .Select(l => l.SubDbId)

                .ToHashSet();

            if (linkedSubIds.Count > 0)

            {

                return databases

                    .Where(d => linkedSubIds.Contains(d.DbId))

                    .OrderBy(d => d.DisplayName)

                    .ToList();

            }

        }



        return databases

            .Where(d => d.DbId != sourceDbId && d.DbRole is "SUB" or "BOTH" or "MASTER")

            .OrderBy(d => d.DisplayName)

            .ToList();

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
        PushableEntityCodes.Normalize(entity, EntityCodes.Customer);

    private Task<List<MasterRecord>> LoadRecordsAsync(RegisteredDatabase db, string entity) =>
        entity.ToUpperInvariant() switch
        {
            "ITEM" => _read.GetItemsAsync(db),
            "CUSTOMER" => _read.GetCustomersAsync(db),
            "SUPPLIER" => _read.GetSuppliersAsync(db),
            _ => Task.FromResult(new List<MasterRecord>())
        };

}


