using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;
using SqlAccountSync.Services.SqlAccount;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SqlAccountSync.Tests;

/// <summary>
/// Website item push / document ensure prerequisites under Provider=Sdk must not call paid REST UOM.
/// </summary>
public class ItemPrerequisiteSdkModeTests
{
    [Fact]
    public async Task Sdk_Ensure_skips_standalone_UOM_and_does_not_call_REST()
    {
        var api = new ThrowingApiClient();
        var read = new StubRead { StockGroup = new StockGroupRecord { Code = "DEFAULT", Description = "DEFAULT" } };
        var write = new StubWrite();
        var svc = new ItemPrerequisiteService(
            api,
            read,
            write,
            MsOptions.Create(new SqlAccountingWriteOptions { Provider = SqlAccountingWriteProviders.Sdk }),
            NullLogger<ItemPrerequisiteService>.Instance);

        var record = new MasterRecord
        {
            Code = "ITEM1",
            Description = "Widget",
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["stockgroup"] = "DEFAULT",
                ["uom"] = "PCS"
            }
        };

        var result = await svc.EnsureAsync(
            new RegisteredDatabase { DbCode = "DEST", FbPath = @"C:\x\ACC.FDB" },
            new RegisteredDatabase { DbCode = "SRC" },
            record);

        Assert.True(result.Ok);
        Assert.Contains(result.Actions, a => a.Contains("cdsUOM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, write.StockGroupCreates);
        Assert.False(api.Called);
    }

    [Fact]
    public async Task Sdk_Check_reports_cdsUOM_not_REST_UOM()
    {
        var svc = new ItemPrerequisiteService(
            new ThrowingApiClient(),
            new StubRead { StockGroup = new StockGroupRecord { Code = "DEFAULT", Description = "DEFAULT" } },
            new StubWrite(),
            MsOptions.Create(new SqlAccountingWriteOptions { Provider = SqlAccountingWriteProviders.Sdk }),
            NullLogger<ItemPrerequisiteService>.Instance);

        var checks = await svc.CheckAsync(
            new RegisteredDatabase { DbCode = "DEST", FbPath = @"C:\x\ACC.FDB" },
            null,
            new MasterRecord
            {
                Code = "ITEM1",
                Description = "Widget",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["stockgroup"] = "DEFAULT",
                    ["uom"] = "BOX"
                }
            });

        Assert.Contains(checks, c =>
            c.Name.Contains("UOM", StringComparison.OrdinalIgnoreCase)
            && c.Detail.Contains("cdsUOM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(checks, c =>
            c.Detail.Contains("POST /uom", StringComparison.OrdinalIgnoreCase)
            || c.Detail.Contains("GET /uom", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ThrowingApiClient : ISqlAccountApiClient
    {
        public bool Called { get; private set; }

        public Task<SqlApiResponse> GetAsync(RegisteredDatabase db, string path, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("REST must not be called in Sdk mode: GET " + path);
        }

        public Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("REST must not be called in Sdk mode: POST " + path);
        }

        public Task<bool> ExistsInListAsync(RegisteredDatabase db, string listPath, string codeField, string code, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("REST must not be called in Sdk mode: list " + listPath);
        }
    }

    private sealed class StubWrite : ISqlAccountWriteAdapter
    {
        public int StockGroupCreates { get; private set; }

        public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true, Message = "ok" });

        public Task<WriteResult> UpdateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
            => Task.FromResult(new WriteResult { Success = true });

        public Task<WriteResult> DeleteMasterAsync(RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });

        public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true });

        public Task<WriteResult> CreateStockGroupAsync(RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
        {
            StockGroupCreates++;
            return Task.FromResult(new WriteResult { Success = true, Message = "created" });
        }

        public Task<WriteResult> UpdateStockGroupAsync(RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true, Message = "updated" });

        public Task<WriteResult> DeleteStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(new WriteResult { Success = true, Message = "deleted" });
    }

    private sealed class StubRead : ISqlAccountReadAdapter
    {
        public StockGroupRecord? StockGroup { get; init; }

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
        public Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(code, StockGroup?.Code, StringComparison.OrdinalIgnoreCase) ? StockGroup : null);
        public Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default) => Task.FromResult<DocumentHeader?>(null);
        public Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<DocumentListItem>());
        public Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
        public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
        public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new SchemaDiscoveryReport());
    }
}
