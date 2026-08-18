using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Browse;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;

    public IndexModel(IDatabaseRegistry registry, ISqlAccountReadAdapter read)
    {
        _registry = registry;
        _read = read;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<MasterRecord> Records { get; set; } = [];
    public SchemaDiscoveryReport? Schema { get; set; }
    public int DbId { get; set; }
    public string Entity { get; set; } = "ITEM";

    public async Task OnGetAsync(int dbId, string? entity, bool discover = false)
    {
        Databases = await _registry.ListAsync();
        DbId = dbId;
        Entity = string.IsNullOrWhiteSpace(entity) ? "ITEM" : entity;
        var db = Databases.FirstOrDefault(d => d.DbId == dbId) ?? Databases.FirstOrDefault();
        if (db is null) return;
        DbId = db.DbId;
        Records = Entity.ToUpperInvariant() switch
        {
            "CUSTOMER" => await _read.GetCustomersAsync(db),
            "SUPPLIER" => await _read.GetSuppliersAsync(db),
            "WAREHOUSE" => await _read.GetWarehousesAsync(db),
            "TAXCODE" => await _read.GetTaxCodesAsync(db),
            "ACCOUNT" => await _read.GetAccountsAsync(db),
            "UOM" => await _read.GetUomsAsync(db),
            _ => await _read.GetItemsAsync(db)
        };
        if (discover)
            Schema = await _read.DiscoverSchemaAsync(db);
    }
}
