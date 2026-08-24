using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Mappings;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IMappingService _mapping;
    private readonly ISqlAccountReadAdapter _read;
    private readonly IAuditService _audit;

    public IndexModel(IDatabaseRegistry registry, IMappingService mapping, ISqlAccountReadAdapter read, IAuditService audit)
    {
        _registry = registry;
        _mapping = mapping;
        _read = read;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)] public string Entity { get; set; } = "ITEM";
    [BindProperty(SupportsGet = true)] public int MasterDbId { get; set; }
    [BindProperty(SupportsGet = true)] public int SubDbId { get; set; }
    public string EntityTitle => Entity switch
    {
        "ITEM" => "Item",
        "CUSTOMER" => "Customer",
        "SUPPLIER" => "Supplier",
        "ACCOUNT" => "Account",
        "TAXCODE" => "Tax",
        "WAREHOUSE" => "Warehouse",
        "UOM" => "UOM",
        _ => Entity
    };
    [BindProperty] public string MasterCode { get; set; } = "";
    [BindProperty] public string SubCode { get; set; } = "";
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<EntityMapping> Mappings { get; set; } = [];
    public List<MasterRecord> MasterRecords { get; set; } = [];
    public List<MasterRecord> SubRecords { get; set; } = [];
    public List<EntityMapping> Conflicts { get; set; } = [];
    public string Message { get; set; } = "";

    public async Task OnGetAsync()
    {
        Entity = NormalizeEntity(Entity);
        Databases = await _registry.ListAsync();
        if (MasterDbId == 0)
            MasterDbId = Databases.FirstOrDefault(d => d.DbRole is "MASTER" or "BOTH")?.DbId ?? Databases.FirstOrDefault()?.DbId ?? 0;
        if (SubDbId == 0)
            SubDbId = Databases.FirstOrDefault(d => d.DbId != MasterDbId)?.DbId ?? 0;
        if (MasterDbId > 0 && SubDbId > 0)
        {
            Mappings = await _mapping.ListAsync(MasterDbId, SubDbId, Entity);
            var master = await _registry.GetAsync(MasterDbId);
            var sub = await _registry.GetAsync(SubDbId);
            if (master is not null && sub is not null)
            {
                MasterRecords = await Load(master, Entity);
                SubRecords = await Load(sub, Entity);
            }
        }
        Conflicts = await _mapping.ConflictsAsync();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (!IsAdmin) return Forbid();
        Entity = NormalizeEntity(Entity);
        Databases = await _registry.ListAsync();
        var masterDb = await _registry.GetAsync(MasterDbId);
        var subDb = await _registry.GetAsync(SubDbId);
        if (masterDb is not null) MasterRecords = await Load(masterDb, Entity);
        if (subDb is not null) SubRecords = await Load(subDb, Entity);
        var masterRec = MasterRecords.FirstOrDefault(r => r.Code == MasterCode);
        var subRec = SubRecords.FirstOrDefault(r => r.Code == SubCode);
        var srcEntity = ResolvePartyType(masterRec, Entity);
        var dstEntity = ResolvePartyType(subRec, Entity);
        var (ok, msg, _) = await _mapping.ConfirmAsync(new EntityMapping
        {
            MasterDbId = MasterDbId,
            SubDbId = SubDbId,
            EntityCode = srcEntity,
            SrcEntityCode = srcEntity,
            DstEntityCode = dstEntity,
            MasterCode = MasterCode,
            SubCode = SubCode,
            MasterDesc = masterRec?.Description ?? "",
            SubDesc = subRec?.Description ?? ""
        }, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = "USER CREATED ITEM MAPPING",
            Entity = Entity,
            RecordId = $"{MasterCode}->{SubCode}",
            Result = ok ? "SUCCESS" : "CONFLICT"
        });
        Message = msg;
        await OnGetAsync();
        return Page();
    }

    private static string NormalizeEntity(string entity) =>
        PushableEntityCodes.Normalize(entity, EntityCodes.Item);

    private async Task<List<MasterRecord>> Load(RegisteredDatabase db, string entity)
    {
        switch (entity.ToUpperInvariant())
        {
            case "ITEM":
                return await _read.GetItemsAsync(db);
            case "CUSTOMER":
                return MergeParty(await _read.GetCustomersAsync(db), await _read.GetSuppliersAsync(db), "customer", "supplier");
            case "SUPPLIER":
                return MergeParty(await _read.GetSuppliersAsync(db), await _read.GetCustomersAsync(db), "supplier", "customer");
            default:
                return [];
        }
    }

    private static List<MasterRecord> MergeParty(
        List<MasterRecord> primary, List<MasterRecord> secondary, string primaryLabel, string secondaryLabel)
    {
        var list = new List<MasterRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in primary)
        {
            if (!seen.Add(r.Code)) continue;
            list.Add(new MasterRecord
            {
                Code = r.Code,
                Description = string.IsNullOrWhiteSpace(r.Description) ? $"({primaryLabel})" : $"{r.Description} ({primaryLabel})",
                Key = r.Key,
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["partyType"] = primaryLabel.Equals("customer", StringComparison.OrdinalIgnoreCase)
                        ? EntityCodes.Customer
                        : EntityCodes.Supplier
                }
            });
        }
        foreach (var r in secondary)
        {
            if (!seen.Add(r.Code)) continue;
            list.Add(new MasterRecord
            {
                Code = r.Code,
                Description = string.IsNullOrWhiteSpace(r.Description) ? $"({secondaryLabel})" : $"{r.Description} ({secondaryLabel})",
                Key = r.Key,
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["partyType"] = secondaryLabel.Equals("customer", StringComparison.OrdinalIgnoreCase)
                        ? EntityCodes.Customer
                        : EntityCodes.Supplier
                }
            });
        }
        return list.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolvePartyType(MasterRecord? record, string fallbackEntity)
    {
        if (record?.Fields.TryGetValue("partyType", out var t) == true && !string.IsNullOrWhiteSpace(t))
            return t.Trim().ToUpperInvariant();
        return fallbackEntity.Trim().ToUpperInvariant();
    }
}
