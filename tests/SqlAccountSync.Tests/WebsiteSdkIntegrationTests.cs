using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.SqlAccount.Sdk;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SqlAccountSync.Tests;

/// <summary>
/// Isolated Website → SDK integration checks (mocks/fakes only; no live COM / no company DB writes).
/// </summary>
public class WebsiteSdkIntegrationTests
{
    [Fact]
    public void SyncPush_MasterToLinkedSubs_UsesRegistryLinks()
    {
        var master = Db(1, "MAIN", DatabaseRoles.Master, @"\\share\ACC-MAIN.FDB");
        var miracle = Db(2, "MIRACLE", DatabaseRoles.Sub, @"\\share\ACC-MIRACLE.FDB");
        var onyat = Db(3, "ONYAT", DatabaseRoles.Sub, @"\\share\ACC-ONYAT.FDB");
        var other = Db(4, "OTHER", DatabaseRoles.Sub, @"\\share\ACC-OTHER.FDB");
        var links = new[]
        {
            new DatabaseLink { MasterDbId = 1, SubDbId = 2, IsActive = true },
            new DatabaseLink { MasterDbId = 1, SubDbId = 3, IsActive = true }
        };

        var targets = SyncTargetResolver.ResolveLinkedSubs(master, [master, miracle, onyat, other], links);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, d => d.DbId == 2);
        Assert.Contains(targets, d => d.DbId == 3);
        Assert.DoesNotContain(targets, d => d.DbId == 4);
    }

    [Fact]
    public void SyncPush_AccountSource_RequiresMasterOrBoth()
    {
        var master = Db(1, "MAIN", DatabaseRoles.Master, @"\\share\ACC-MAIN.FDB");
        var sub = Db(2, "MIRACLE", DatabaseRoles.Sub, @"\\share\ACC-MIRACLE.FDB");

        Assert.True(SyncTargetResolver.IsMasterOrBoth(master));
        Assert.False(SyncTargetResolver.IsMasterOrBoth(sub));
        // Website Push/Accounts reject Sub as ACCOUNT source (Master → Sub only).
        Assert.Empty(new[] { sub }.Where(SyncTargetResolver.IsMasterOrBoth));
    }

    [Theory]
    [InlineData(DatabaseRoles.Master, DatabaseRoles.Sub, EntityCodes.Customer, EntityCodes.Customer)]
    [InlineData(DatabaseRoles.Master, DatabaseRoles.Sub, EntityCodes.Supplier, EntityCodes.Supplier)]
    [InlineData(DatabaseRoles.Both, DatabaseRoles.Sub, EntityCodes.Customer, EntityCodes.Customer)]
    [InlineData(DatabaseRoles.Sub, DatabaseRoles.Sub, EntityCodes.Customer, EntityCodes.Supplier)]
    [InlineData(DatabaseRoles.Sub, DatabaseRoles.Sub, EntityCodes.Supplier, EntityCodes.Customer)]
    public void SyncPush_PartyMapping_FollowsPartnerSyncRules(
        string srcRole, string dstRole, string sourceEntity, string expectedDestEntity)
    {
        var src = Db(1, "A", srcRole, @"\\share\A.FDB");
        var dst = Db(2, "B", dstRole, @"\\share\B.FDB");
        Assert.Equal(expectedDestEntity, PartnerSyncRules.ResolveTargetEntity(src, dst, sourceEntity));
    }

    [Fact]
    public void SyncPush_AccountEntity_DoesNotFlip()
    {
        var master = Db(1, "MAIN", DatabaseRoles.Master, @"\\share\ACC-MAIN.FDB");
        var sub = Db(2, "SUB", DatabaseRoles.Sub, @"\\share\ACC-SUB.FDB");
        Assert.Equal(EntityCodes.Account,
            PartnerSyncRules.ResolveTargetEntity(master, sub, EntityCodes.Account));
    }

    [Fact]
    public void MultiCompany_SdkTargets_UseEachRegisteredFbPath_NotHardcodedCompany()
    {
        var dcf = @"\\192.168.100.201\Share\SQLACC.DCF";
        var companies = new[]
        {
            Db(12, "MIRACLE", DatabaseRoles.Sub, @"\\192.168.100.201\Data\ACC-MIRACLE.FDB"),
            Db(14, "ONYAT", DatabaseRoles.Sub, @"\\192.168.100.201\Data\ACC-ONYAT.FDB"),
            Db(13, "STEPPES", DatabaseRoles.Sub, @"\\192.168.100.201\Data\ACC-STEPPES.FDB")
        };

        var files = companies
            .Select(c => SqlAccSessionManager.ResolveCompanyTarget(c, dcf).DatabaseFileName)
            .ToList();

        Assert.Equal(["ACC-MIRACLE.FDB", "ACC-ONYAT.FDB", "ACC-STEPPES.FDB"], files);
        Assert.All(companies.Select(c => SqlAccSessionManager.ResolveCompanyTarget(c, dcf)),
            t => Assert.Equal(dcf, t.DcfPath));
    }

    [Fact]
    public void MultiCompany_SdkWriteReady_RequiresFbPath_NotApiCred()
    {
        var withPath = Db(2, "MIRACLE", DatabaseRoles.Sub, @"\\share\ACC-MIRACLE.FDB");
        withPath.ApiBaseUrl = "";
        withPath.ApiCredRef = "";
        var withoutPath = Db(3, "BAD", DatabaseRoles.Sub, "");
        withoutPath.ApiBaseUrl = "https://api.sql.my";
        withoutPath.ApiCredRef = "sm:keys";

        Assert.True(PartnerAutoPushTargets.IsWriteReady(withPath, SqlAccountingWriteProviders.Sdk));
        Assert.False(PartnerAutoPushTargets.IsWriteReady(withoutPath, SqlAccountingWriteProviders.Sdk));
        Assert.True(PartnerAutoPushTargets.IsWriteReady(withoutPath, SqlAccountingWriteProviders.Rest));
    }

    [Fact]
    public async Task MultiCompany_SessionCapture_RoutesEachDbSeparately()
    {
        var sessions = new CapturingSessions();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var a = Db(1, "A", DatabaseRoles.Master, @"\\fileserver\Data\ACC-A.FDB");
        var b = Db(2, "B", DatabaseRoles.Sub, @"\\fileserver\Data\ACC-B.FDB");

        await adapter.CreateMasterAsync(a, EntityCodes.Customer, new MasterRecord { Code = "C1", Description = "A" }, "T");
        await adapter.CreateMasterAsync(b, EntityCodes.Supplier, new MasterRecord { Code = "S1", Description = "B" }, "T");

        Assert.Equal(2, sessions.Companies.Count);
        var dcf = @"C:\eStream\SQLAccounting\Share\Default.DCF";
        Assert.Equal("ACC-A.FDB",
            SqlAccSessionManager.ResolveCompanyTarget(sessions.Companies[0], dcf).DatabaseFileName);
        Assert.Equal("ACC-B.FDB",
            SqlAccSessionManager.ResolveCompanyTarget(sessions.Companies[1], dcf).DatabaseFileName);
        Assert.NotEqual(sessions.Companies[0].DbCode, sessions.Companies[1].DbCode);
    }

    [Fact]
    public async Task SdkWrite_DiSelectsSdkAdapter_AndRestAdapterRefuses()
    {
        using var sp = BuildWriteProvider(SqlAccountingWriteProviders.Sdk);
        Assert.IsType<SqlAccountSdkWriteAdapter>(sp.GetRequiredService<ISqlAccountWriteAdapter>());

        var rest = sp.GetRequiredService<SqlAccountApiWriteAdapter>();
        var refused = await rest.CreateMasterAsync(
            Db(1, "X", DatabaseRoles.Master, @"\\share\X.FDB", api: true),
            EntityCodes.Customer,
            new MasterRecord { Code = "C1", Description = "X" },
            "T");

        Assert.False(refused.Success);
        Assert.Contains("Provider=Sdk", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EntityCodes.Customer)]
    [InlineData(EntityCodes.Supplier)]
    [InlineData(EntityCodes.Account)]
    [InlineData(EntityCodes.Item)]
    public async Task SdkWrite_CreateUpdateDelete_OpenSessionForEntity(string entity)
    {
        var sessions = new CapturingSessions();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var db = Db(1, "LAB", DatabaseRoles.Master, @"\\share\ACC-LAB.FDB");
        var record = new MasterRecord
        {
            Code = "X1",
            Description = "Row",
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["destparent"] = "1",
                ["acctype"] = "SA",
                ["stockgroup"] = "DEFAULT",
                ["uom"] = "PCS"
            }
        };

        Assert.False((await adapter.CreateMasterAsync(db, entity, record, "T")).Success);
        Assert.False((await adapter.UpdateMasterAsync(db, entity, record, "T", destDocKey: 10, destParentDocKey: 1)).Success);
        Assert.False((await adapter.DeleteMasterAsync(db, entity, "X1")).Success);
        Assert.True(sessions.RunCount >= 3);
    }

    [Fact]
    public void SdkWrite_DocumentTransfer_MapsSixDestPaths()
    {
        Assert.Equal("PH_PI", SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchaseinvoice"));
        Assert.Equal("PH_GR", SqlAccBizObjectWriter.ResolveDocumentBizObject("/goodsreceived"));
        Assert.Equal("ST_XF", SqlAccBizObjectWriter.ResolveDocumentBizObject("/stocktransfer"));
        Assert.Equal("PH_SC", SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchasereturned"));
        Assert.Equal("SL_DN", SqlAccBizObjectWriter.ResolveDocumentBizObject("/salesdebitnote"));
        Assert.Equal("SL_CN", SqlAccBizObjectWriter.ResolveDocumentBizObject("/salescreditnote"));
    }

    [Fact]
    public async Task SdkWrite_Prerequisites_SkipRestUom_WhenSdk()
    {
        var api = new CountingApi();
        var svc = new ItemPrerequisiteService(
            api,
            new AlwaysHasStockGroupRead(),
            new NoopWrite(),
            MsOptions.Create(new SqlAccountingWriteOptions { Provider = SqlAccountingWriteProviders.Sdk }),
            NullLogger<ItemPrerequisiteService>.Instance);

        var ok = await svc.EnsureAsync(
            Db(2, "DEST", DatabaseRoles.Sub, @"\\share\DEST.FDB"),
            Db(1, "SRC", DatabaseRoles.Master, @"\\share\SRC.FDB"),
            new MasterRecord
            {
                Code = "I1",
                Description = "Item",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["stockgroup"] = "DEFAULT",
                    ["uom"] = "PCS"
                }
            });

        Assert.True(ok.Ok);
        Assert.Equal(0, api.Calls);
        Assert.Contains(ok.Actions, a => a.Contains("cdsUOM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Safety_DryRunAdapter_DoesNotWrite()
    {
        var adapter = new DryRunWriteAdapter();
        var result = await adapter.CreateMasterAsync(
            Db(1, "X", DatabaseRoles.Master, @"\\share\X.FDB"),
            EntityCodes.Customer,
            new MasterRecord { Code = "C1", Description = "C" },
            "DRY");

        Assert.True(result.Success);
        Assert.Contains("no data was modified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(adapter.Actions, a => a.Contains("WOULD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Safety_GlDelete_RefusesParents()
    {
        var err = SqlAccBizObjectWriter.ValidateGlDeleteHasNoChildren("1", 684, childCount: 2);
        Assert.NotNull(err);
        Assert.Contains("child", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safety_ControlAccount_HasNoHardcoded300Or400()
    {
        Assert.Null(SqlAccBizObjectWriter.ChooseControlAccount(null, null));
    }

    [Fact]
    public void Safety_SyncTargetResolver_IsCaseInsensitiveForRoles()
    {
        var master = Db(1, "MAIN", "master", @"\\share\ACC-MAIN.FDB");
        var sub = Db(2, "SUB", "sub", @"\\share\ACC-SUB.FDB");
        Assert.True(SyncTargetResolver.IsMasterOrBoth(master));
        var targets = SyncTargetResolver.ResolveLinkedSubs(master, [master, sub], []);
        Assert.Contains(targets, d => d.DbId == 2);
    }

    private static RegisteredDatabase Db(int id, string code, string role, string fbPath, bool api = false) =>
        new()
        {
            DbId = id,
            DbCode = code,
            DisplayName = code,
            DbRole = role,
            IsActive = true,
            FbPath = fbPath,
            ApiBaseUrl = api ? "https://api.sql.my" : "",
            ApiCredRef = api ? "sm:test" : ""
        };

    private static ServiceProvider BuildWriteProvider(string provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(MsOptions.Create(new SqlAccountingWriteOptions { Provider = provider }));
        services.AddSingleton(MsOptions.Create(new SqlAccSdkOptions { AllowLiveCom = false }));
        services.AddSingleton(MsOptions.Create(new SyncOptions()));
        services.AddSingleton<ISqlAccSessionManager, CapturingSessions>();
        services.AddSingleton<ISecretResolver, NoopSecrets>();
        services.AddSingleton<ISqlApiCredentialBundle, NoopBundle>();
        services.AddLogging();
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<SqlAccountApiWriteAdapter>();
        services.AddScoped<SqlAccountSdkWriteAdapter>();
        services.AddScoped<ISqlAccountWriteAdapter>(sp =>
        {
            var p = sp.GetRequiredService<IOptions<SqlAccountingWriteOptions>>().Value.Provider?.Trim();
            return SqlAccountingWriteProviders.IsSdk(p)
                ? sp.GetRequiredService<SqlAccountSdkWriteAdapter>()
                : sp.GetRequiredService<SqlAccountApiWriteAdapter>();
        });
        return services.BuildServiceProvider();
    }

    private sealed class CapturingSessions : ISqlAccSessionManager
    {
        public int RunCount { get; private set; }
        public List<RegisteredDatabase> Companies { get; } = [];

        public Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(new Noop());

        public Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default)
        {
            RunCount++;
            Companies.Add(db);
            return Task.FromResult(action(new FakeComApp()));
        }

        public SqlAccSessionStatus GetStatus() => new(false, null, false);

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class FakeComApp : ISqlAccComApp
        {
            public bool IsLogin => true;
            public void Login(string userName, string password, string dcfPath, string databaseFileName) { }
            public void Logout() { }
            public int QueryRecordCount(string selectSql) => 0;
            public bool AgentExists(string code) => false;
            public void AgentCreate(string code, string description) { }
            public void AgentUpdate(string code, string description) { }
            public string AgentReadDescription(string code) => "";
            public void AgentDelete(string code) { }
            public void Dispose() { }
        }
    }

    private sealed class CountingApi : ISqlAccountApiClient
    {
        public int Calls { get; private set; }
        public Task<SqlApiResponse> GetAsync(RegisteredDatabase db, string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(SqlApiResponse.Unavailable);
        }

        public Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WriteResult { Success = false });
        }

        public Task<bool> ExistsInListAsync(RegisteredDatabase db, string listPath, string codeField, string code, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }

    private sealed class AlwaysHasStockGroupRead : ISqlAccountReadAdapter
    {
        public Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
            => Task.FromResult<StockGroupRecord?>(new StockGroupRecord { Code = code, Description = code });

        public Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<GlAccountRow>> GetGlChartAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<GlAccountRow>());
        public Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetTermsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
        public Task<MasterRecord?> GetPartyDetailAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
        public Task<MasterRecord?> GetItemDetailAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
        public Task<bool> PartyExistsByStampAsync(RegisteredDatabase db, string entityCode, string txnStamp, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> MasterExistsByCodeAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> GlAccountHasActivityAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<List<GlTransLine>?> ListGlTransactionsAsync(RegisteredDatabase db, DateTime? dateFrom = null, DateTime? dateTo = null, string? codeSearch = null, CancellationToken cancellationToken = default) => Task.FromResult<List<GlTransLine>?>([]);
        public Task<List<MomCollectionSourceRow>?> ListMomCollectionAsync(RegisteredDatabase db, DateTime dateFrom, DateTime dateTo, string? companySearch = null, CancellationToken cancellationToken = default) => Task.FromResult<List<MomCollectionSourceRow>?>([]);
        public Task<List<GlTransLine>?> ListPlByProjectLinesAsync(RegisteredDatabase db, DateTime? dateFrom = null, DateTime? dateTo = null, string? codeSearch = null, CancellationToken cancellationToken = default) => Task.FromResult<List<GlTransLine>?>([]);
        public Task<List<SoListingRow>?> ListSoListingAsync(RegisteredDatabase db, string? filterColumn = null, string? filterValue = null, CancellationToken cancellationToken = default) => Task.FromResult<List<SoListingRow>?>([]);
        public Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default) => Task.FromResult<DocumentHeader?>(null);
        public Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<DocumentListItem>());
        public Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
        public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
        public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new SchemaDiscoveryReport());
    }

    private sealed class NoopWrite : ISqlAccountWriteAdapter
    {
        public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> UpdateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> DeleteMasterAsync(RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> CreateStockGroupAsync(RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> UpdateStockGroupAsync(RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
        public Task<WriteResult> DeleteStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });
    }

    private sealed class NoopSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoopBundle : ISqlApiCredentialBundle
    {
        public Task<SqlApiCredentials?> ResolveAsync(string tenantCode, RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult<SqlApiCredentials?>(null);
        public Task<(bool Ok, string Message)> SaveAsync(string tenantCode, string dbCode, string accessKey, string secretKey, CancellationToken cancellationToken = default) =>
            Task.FromResult((false, "noop"));
        public Task<bool> RemoveAsync(string tenantCode, string dbCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task MigrateLegacyAsync(string tenantCode, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
