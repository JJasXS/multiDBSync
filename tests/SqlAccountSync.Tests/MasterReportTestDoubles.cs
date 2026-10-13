using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Tests;

internal sealed class ReportRegistry(params RegisteredDatabase[] databases) : IDatabaseRegistry
{
    public Task<DashboardRegistrySnapshot> ReadDashboardRegistryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DashboardRegistrySnapshot
        {
            Configured = true,
            Databases = databases.ToList()
        });

    public Task<List<RegisteredDatabase>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(databases.ToList());

    public Task<RegisteredDatabase?> GetAsync(int dbId, CancellationToken cancellationToken = default) =>
        Task.FromResult(databases.FirstOrDefault(db => db.DbId == dbId));

    public Task<int> SaveAsync(RegisteredDatabase db, string user, CancellationToken cancellationToken = default) =>
        Task.FromResult(db.DbId);

    public Task DeactivateAsync(int dbId, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(int dbId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetActiveAsync(int dbId, bool active, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PointSqlApiCredRefsAtBundleAsync(string tenantCode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateStatusAsync(int dbId, string status, bool success, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkSyncOkAsync(int dbId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<List<DatabaseLink>> ListLinksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<DatabaseLink>());
    public Task<int> AddLinkAsync(int masterId, int subId, string user, CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task DeactivateLinkAsync(int linkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<DashboardStats> DashboardAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DashboardStats());
}

internal sealed class ReportReadAdapter : ISqlAccountReadAdapter
{
    public Func<RegisteredDatabase, List<MomCollectionSourceRow>?>? Collection { get; init; }
    public Func<RegisteredDatabase, List<SoListingRow>?>? SalesOrders { get; init; }

    public Task<List<MomCollectionSourceRow>?> ListMomCollectionAsync(
        RegisteredDatabase db, DateTime dateFrom, DateTime dateTo, string? companySearch = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<List<MomCollectionSourceRow>?>(Collection?.Invoke(db) ?? []);

    public Task<List<SoListingRow>?> ListSoListingAsync(
        RegisteredDatabase db, string? filterColumn = null, string? filterValue = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<List<SoListingRow>?>(SalesOrders?.Invoke(db) ?? []);

    public Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<Services.Accounts.GlAccountRow>> GetGlChartAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        Empty<Services.Accounts.GlAccountRow>();
    public Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<List<MasterRecord>> GetTermsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Empty<MasterRecord>();
    public Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
    public Task<MasterRecord?> GetPartyDetailAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
    public Task<MasterRecord?> GetItemDetailAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
    public Task<bool> PartyExistsByStampAsync(RegisteredDatabase db, string entityCode, string txnStamp, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> MasterExistsByCodeAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> GlAccountHasActivityAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<List<GlTransLine>?> ListGlTransactionsAsync(RegisteredDatabase db, DateTime? dateFrom = null, DateTime? dateTo = null, string? codeSearch = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<List<GlTransLine>?>([]);
    public Task<List<GlTransLine>?> ListPlByProjectLinesAsync(RegisteredDatabase db, DateTime? dateFrom = null, DateTime? dateTo = null, string? codeSearch = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<List<GlTransLine>?>([]);
    public Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) => Task.FromResult<StockGroupRecord?>(null);
    public Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default) => Task.FromResult<DocumentHeader?>(null);
    public Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default) => Empty<DocumentListItem>();
    public Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
    public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
    public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new SchemaDiscoveryReport());

    private static Task<List<T>> Empty<T>() => Task.FromResult(new List<T>());
}
