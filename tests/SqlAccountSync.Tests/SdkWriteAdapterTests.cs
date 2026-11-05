using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Models;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkWriteAdapterTests
{
    [Fact]
    public async Task CreateMaster_Customer_UsesSession_WhenComAvailable()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);

        // Fake COM cannot run BizObject writer — expect failure from NativeApp guard after session opens.
        var result = await adapter.CreateMasterAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            "CUSTOMER",
            new MasterRecord { Code = "C1", Description = "Cust" },
            "JOB-1");

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
        Assert.Contains("SqlAccComApp", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateMaster_Account_OpensSession()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var result = await adapter.CreateMasterAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            "ACCOUNT",
            new MasterRecord
            {
                Code = "100-000",
                Description = "Cash",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["destparent"] = "1",
                    ["acctype"] = "SA"
                }
            },
            "JOB-1");

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
        Assert.Contains("SqlAccComApp", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDocument_PurchaseInvoice_OpensSession()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var result = await adapter.CreateDocumentAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            "/purchaseinvoice",
            new DocumentHeader { DocNo = "PI-1", PartyCode = "400-A" },
            "TXN-1");

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
    }

    [Fact]
    public async Task CreateDocument_GoodsReceived_OpensSession()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var result = await adapter.CreateDocumentAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            "/goodsreceived",
            new DocumentHeader { DocNo = "GR-1", PartyCode = "400-A" },
            "TXN-1");

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
        Assert.Contains("SqlAccComApp", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteMaster_Account_OpensSession()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var result = await adapter.DeleteMasterAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            "ACCOUNT",
            "100-000");

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
    }

    [Fact]
    public async Task CreateStockGroup_OpensSession()
    {
        var sessions = new CapturingSessionManager();
        var adapter = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var result = await adapter.CreateStockGroupAsync(
            new RegisteredDatabase { DbCode = "LAB", FbPath = @"C:\lab\ACC-0001.FDB" },
            new SqlAccountSync.Services.Items.StockGroupRecord { Code = "DEFAULT", Description = "Default" });

        Assert.False(result.Success);
        Assert.True(sessions.RunCount >= 1);
    }

    [Theory]
    [InlineData(@"C:\eStream\SQLAccounting\DB\ACC-0008.FDB", "ACC-0008.FDB")]
    [InlineData("D:/data/ACC-0040.FDB", "ACC-0040.FDB")]
    public void ResolveCompanyTarget_UsesFbPathFileName(string fbPath, string expectedFile)
    {
        var db = new RegisteredDatabase { DbCode = "X", FbPath = fbPath };
        var target = SqlAccSessionManager.ResolveCompanyTarget(db, @"C:\eStream\SQLAccounting\Share\Default.DCF");
        Assert.Equal(expectedFile, target.DatabaseFileName);
        Assert.EndsWith("Default.DCF", target.DcfPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCompanyTarget_RequiresDcfAndFbFile()
    {
        var db = new RegisteredDatabase { DbCode = "X", FbPath = "" };
        Assert.Throws<InvalidOperationException>(() =>
            SqlAccSessionManager.ResolveCompanyTarget(db, @"C:\a\Default.DCF"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlAccSessionManager.ResolveCompanyTarget(
                new RegisteredDatabase { FbPath = "ACC-1.FDB" }, ""));
    }

    [Fact]
    public void ResolveDocumentBizObject_MapsRestPaths()
    {
        Assert.Equal(SqlAccBizObjectWriter.PurchaseInvoiceBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchaseinvoice"));
        Assert.Equal(SqlAccBizObjectWriter.StockTransferBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/stocktransfer"));
        Assert.Equal(SqlAccBizObjectWriter.PurchaseReturnBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchasereturned"));
        Assert.Equal(SqlAccBizObjectWriter.SalesCreditNoteBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/salescreditnote"));
        Assert.Equal(SqlAccBizObjectWriter.SalesDebitNoteBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/salesdebitnote"));
        Assert.Equal(SqlAccBizObjectWriter.GoodsReceivedBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/goodsreceived"));
    }

    [Fact]
    public void Catalog_MarksCustomerItemAccountGrAsImplemented()
    {
        Assert.Contains(SdkBizObjectCatalog.Matrix, c =>
            c.SyncFeature.Contains("Customer", StringComparison.OrdinalIgnoreCase)
            && c.Status == SdkDocStatus.VerifiedInOfficialWiki);
        Assert.Contains(SdkBizObjectCatalog.Matrix, c =>
            c.LikelyBizObject == SdkBizObjectCatalog.StockItem
            && c.Status == SdkDocStatus.VerifiedInOfficialWiki);
        Assert.Contains(SdkBizObjectCatalog.Matrix, c =>
            c.SyncFeature.Contains("GL Account", StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.LikelyBizObject, "GL_ACC", StringComparison.OrdinalIgnoreCase)
            && c.Status == SdkDocStatus.VerifiedInOfficialWiki);
        Assert.Contains(SdkBizObjectCatalog.Matrix, c =>
            c.SyncFeature.Contains("DO → GR", StringComparison.OrdinalIgnoreCase)
            && c.LikelyBizObject == "PH_GR"
            && c.Status == SdkDocStatus.VerifiedInOfficialWiki);
        Assert.Contains(SdkBizObjectCatalog.Matrix, c =>
            c.SyncFeature.Contains("Stock Group", StringComparison.OrdinalIgnoreCase)
            && c.LikelyBizObject == "ST_GROUP"
            && c.Status == SdkDocStatus.VerifiedInOfficialWiki);
    }

    [Theory]
    [InlineData("C:/eStream/SQLAccounting/DB/ACC-MIRACLE.FDB", "ACC-MIRACLE.FDB")]
    [InlineData("C:/eStream/SQLAccounting/DB/ACC-ONYAT.FDB", "ACC-ONYAT.FDB")]
    [InlineData("C:/eStream/SQLAccounting/DB/ACC-MAIN.FDB", "ACC-MAIN.FDB")]
    [InlineData("C:/eStream/SQLAccounting/DB/ACC-STEPPES.FDB", "ACC-STEPPES.FDB")]
    public void ResolveCompanyTarget_WorksForAnySyncFbPath(string fbPath, string expectedFile)
    {
        // Production targeting is SYNC_DATABASE.FbPath filename + configured DCF — no company allow-list.
        var db = new RegisteredDatabase { DbCode = "ANY", FbPath = fbPath };
        var target = SqlAccSessionManager.ResolveCompanyTarget(
            db, @"\\192.168.100.201\Share\SQLACC.DCF");
        Assert.Equal(expectedFile, target.DatabaseFileName);
        Assert.Equal(@"\\192.168.100.201\Share\SQLACC.DCF", target.DcfPath);
    }

    private sealed class CapturingSessionManager : ISqlAccSessionManager
    {
        public int RunCount { get; private set; }

        public Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(new Noop());

        public Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default)
        {
            RunCount++;
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
}
