using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.SqlAccount.Sdk;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SqlAccountSync.Tests;

/// <summary>
/// Verifies Program.cs-style Provider selection without starting the web host or live COM.
/// </summary>
public class SdkWriteProviderDiTests
{
    [Fact]
    public void Provider_Rest_Resolves_ApiWriteAdapter()
    {
        using var sp = BuildWriteProvider(SqlAccountingWriteProviders.Rest);
        Assert.IsType<SqlAccountApiWriteAdapter>(sp.GetRequiredService<ISqlAccountWriteAdapter>());
    }

    [Fact]
    public void Provider_Sdk_Resolves_SdkWriteAdapter()
    {
        using var sp = BuildWriteProvider(SqlAccountingWriteProviders.Sdk);
        Assert.IsType<SqlAccountSdkWriteAdapter>(sp.GetRequiredService<ISqlAccountWriteAdapter>());
    }

    [Fact]
    public async Task Provider_Sdk_AccountAndGoodsReceived_Fail_WithoutLiveComSession()
    {
        using var sp = BuildWriteProvider(SqlAccountingWriteProviders.Sdk);
        var write = sp.GetRequiredService<ISqlAccountWriteAdapter>();
        var db = new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" };

        var account = await write.CreateMasterAsync(db, "ACCOUNT", new MasterRecord { Code = "100-000" }, "DRY");
        var gr = await write.CreateDocumentAsync(db, "/goodsreceived", new DocumentHeader { DocNo = "GR-1" }, "DRY");
        var group = await write.CreateStockGroupAsync(db, new StockGroupRecord { Code = "DEFAULT" });

        Assert.False(account.Success);
        Assert.False(gr.Success);
        Assert.False(group.Success);
        Assert.Contains("COM", account.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COM", gr.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COM", group.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowLiveCom_False_Blocks_ComFactory()
    {
        var factory = new SqlAccComAppFactory(MsOptions.Create(new SqlAccSdkOptions
        {
            AllowLiveCom = false,
            ProgId = "SQLAcc.BizApp"
        }));
        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Contains("AllowLiveCom is false", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_Sdk_ApiWriteAdapter_Refuses_Rest_Calls()
    {
        using var sp = BuildWriteProvider(SqlAccountingWriteProviders.Sdk);
        var rest = sp.GetRequiredService<SqlAccountApiWriteAdapter>();
        var db = new RegisteredDatabase
        {
            DbCode = "LAB",
            ApiBaseUrl = "https://api.sql.my",
            ApiCredRef = "sm:test",
            FbPath = @"C:\lab\ACC-0001.FDB"
        };

        var create = await rest.CreateMasterAsync(db, "CUSTOMER", new MasterRecord { Code = "C1", Description = "X" }, "DRY");
        Assert.False(create.Success);
        Assert.Contains("Provider=Sdk", create.Message, StringComparison.OrdinalIgnoreCase);

        var update = await rest.UpdateMasterAsync(
            db, "SUPPLIER", new MasterRecord { Code = "S1", Description = "Y" }, "DRY", destDocKey: 0, destParentDocKey: 0);
        Assert.False(update.Success);
        Assert.Contains("Provider=Sdk", update.Message, StringComparison.OrdinalIgnoreCase);

        var delete = await rest.DeleteMasterAsync(db, "ACCOUNT", "A1");
        Assert.False(delete.Success);
        Assert.Contains("Provider=Sdk", delete.Message, StringComparison.OrdinalIgnoreCase);

        var stockGroup = await rest.CreateStockGroupAsync(db, new StockGroupRecord { Code = "G1", Description = "G" });
        Assert.False(stockGroup.Success);
        Assert.Contains("Provider=Sdk", stockGroup.Message, StringComparison.OrdinalIgnoreCase);

        var doc = await rest.CreateDocumentAsync(db, "/goodsreceived", new DocumentHeader { DocNo = "G1" }, "DRY");
        Assert.False(doc.Success);
        Assert.Contains("Provider=Sdk", doc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_Sdk_ApiClient_Refuses_Rest_Calls()
    {
        var client = new SqlAccountApiClient(
            new HttpClient(),
            new NoopSecrets(),
            new NoopBundle(),
            MsOptions.Create(new SyncOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlAccountApiClient>.Instance,
            MsOptions.Create(new SqlAccountingWriteOptions { Provider = SqlAccountingWriteProviders.Sdk }));

        var db = new RegisteredDatabase
        {
            ApiBaseUrl = "https://api.sql.my",
            ApiCredRef = "sm:test"
        };
        var get = await client.GetAsync(db, "/account/*");
        Assert.False(get.Available);
        var post = await client.PostAsync(db, "/uom", "{}");
        Assert.False(post.Success);
    }

    [Fact]
    public async Task BlockRestHttp_Refuses_Even_When_Provider_Rest()
    {
        var opts = new SqlAccountingWriteOptions
        {
            Provider = SqlAccountingWriteProviders.Rest,
            BlockRestHttp = true
        };
        Assert.True(opts.ShouldRefuseRestHttp());

        var rest = new SqlAccountApiWriteAdapter(
            new HttpClient(new BlockSqlAccountingRestHandler(new HttpClientHandler())),
            new NoopSecrets(),
            new NoopBundle(),
            MsOptions.Create(new SyncOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlAccountApiWriteAdapter>.Instance,
            MsOptions.Create(opts));
        var db = new RegisteredDatabase
        {
            DbCode = "LAB",
            ApiBaseUrl = "https://api.sql.my",
            ApiCredRef = "sm:test",
            FbPath = @"C:\lab\ACC-0001.FDB"
        };

        var create = await rest.CreateMasterAsync(db, "CUSTOMER", new MasterRecord { Code = "C1", Description = "X" }, "DRY");
        Assert.False(create.Success);
        Assert.Contains("BlockRestHttp", create.Message, StringComparison.OrdinalIgnoreCase);

        var client = new SqlAccountApiClient(
            new HttpClient(new BlockSqlAccountingRestHandler(new HttpClientHandler())),
            new NoopSecrets(),
            new NoopBundle(),
            MsOptions.Create(new SyncOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlAccountApiClient>.Instance,
            MsOptions.Create(opts));
        var get = await client.GetAsync(db, "/account/*");
        Assert.False(get.Available);
    }

    [Fact]
    public async Task BlockSqlAccountingRestHandler_Throws_Before_Network()
    {
        using var http = new HttpClient(new BlockSqlAccountingRestHandler(new HttpClientHandler()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            http.GetAsync("https://api.sql.my/account"));
        Assert.Contains("REST HTTP blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

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
            if (string.Equals(p, SqlAccountingWriteProviders.Sdk, StringComparison.OrdinalIgnoreCase))
                return sp.GetRequiredService<SqlAccountSdkWriteAdapter>();
            return sp.GetRequiredService<SqlAccountApiWriteAdapter>();
        });
        return services.BuildServiceProvider();
    }

    private sealed class CapturingSessions : ISqlAccSessionManager
    {
        public Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(new Noop());

        public Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test session must not run live COM.");

        public SqlAccSessionStatus GetStatus() => new(false, null, false);

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoopSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoopBundle : ISqlApiCredentialBundle
    {
        public Task<SqlApiCredentials?> ResolveAsync(
            string tenantCode, RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult<SqlApiCredentials?>(null);

        public Task<(bool Ok, string Message)> SaveAsync(
            string tenantCode, string dbCode, string accessKey, string secretKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((false, "noop"));

        public Task<bool> RemoveAsync(string tenantCode, string dbCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task MigrateLegacyAsync(string tenantCode, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
