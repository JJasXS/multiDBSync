using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Validation;

namespace SqlAccountSync.Tests;

public class ItemDestCodeLinkTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MasterExistsByCodeAsync_blank_code_returns_false(string code)
    {
        var adapter = new FirebirdReadAdapter(
            new CompanyConnectionFactory(new NoopSecretResolver()),
            NullLogger<FirebirdReadAdapter>.Instance);

        var db = new RegisteredDatabase { FbHost = "localhost", FbPath = "C:/missing.fdb" };
        var exists = await adapter.MasterExistsByCodeAsync(db, EntityCodes.Item, code);
        Assert.False(exists);
    }

    [Fact]
    public async Task MasterExistsByCodeAsync_unsupported_entity_returns_false()
    {
        var adapter = new FirebirdReadAdapter(
            new CompanyConnectionFactory(new NoopSecretResolver()),
            NullLogger<FirebirdReadAdapter>.Instance);

        var db = new RegisteredDatabase { FbHost = "localhost", FbPath = "C:/missing.fdb" };
        var exists = await adapter.MasterExistsByCodeAsync(db, EntityCodes.TaxCode, "SR");
        Assert.False(exists);
    }

    [Fact]
    public async Task MasterExistsByCodeAsync_customer_on_missing_db_returns_false()
    {
        var adapter = new FirebirdReadAdapter(
            new CompanyConnectionFactory(new NoopSecretResolver()),
            NullLogger<FirebirdReadAdapter>.Instance);

        var db = new RegisteredDatabase { FbHost = "localhost", FbPath = "C:/missing.fdb" };
        var exists = await adapter.MasterExistsByCodeAsync(db, EntityCodes.Customer, "300-C0001");
        Assert.False(exists);
    }

    [Fact]
    public async Task ProcessMaster_links_when_destination_item_code_exists_without_api_create()
    {
        var harness = new ItemLinkTestHarness { DestinationItemExists = true };
        var job = harness.BuildJob("LINKME");

        await harness.Orchestrator.ProcessJobAsync(job);

        Assert.Equal(0, harness.WriteAdapter.CreateMasterCallCount);
        Assert.Equal(1, harness.Mapping.ConfirmPairCallCount);
        Assert.Equal(JobStatuses.Completed, harness.Queue.LastStatus);
        Assert.Contains("Mapping linked", harness.Queue.LastMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessMaster_still_calls_api_create_when_destination_item_missing()
    {
        var harness = new ItemLinkTestHarness { DestinationItemExists = false };
        var job = harness.BuildJob("NEWITEM");

        await harness.Orchestrator.ProcessJobAsync(job);

        Assert.Equal(1, harness.WriteAdapter.CreateMasterCallCount);
        Assert.Equal(1, harness.Mapping.ConfirmPairCallCount);
    }

    [Fact]
    public async Task DryRun_reports_destination_item_code_exists()
    {
        var harness = new ItemLinkTestHarness { DestinationItemExists = true };
        var result = await harness.Orchestrator.DryRunMasterAsync(6, 8, EntityCodes.Item, "LINKME");

        var check = Assert.Single(result.Checks, c => c.Name == "Destination item code");
        Assert.Contains("Would link mapping only", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ItemLinkTestHarness
    {
        public bool DestinationItemExists { get; set; }
        public SpyWriteAdapter WriteAdapter { get; } = new();
        public TestMappingService Mapping { get; } = new();
        public TestJobQueue Queue { get; } = new();
        public SyncOrchestrator Orchestrator { get; }

        public ItemLinkTestHarness()
        {
            var source = TestDatabase(6, "HQ");
            var dest = TestDatabase(8, "S2");
            var registry = new TestDatabaseRegistry(source, dest);
            var read = new TestReadAdapter(this);
            var validation = new ValidationService(Mapping);
            Orchestrator = new SyncOrchestrator(
                new CentralDb(Microsoft.Extensions.Options.Options.Create(new CentralDbOptions()), new NoopSecretResolver(), NullLogger<CentralDb>.Instance),
                registry,
                read,
                WriteAdapter,
                Mapping,
                validation,
                Queue,
                new NoopAuditService(),
                new NoopErrorLogService(),
                new NoopItemPrerequisiteService());
        }

        public SyncJob BuildJob(string code) => new()
        {
            JobId = 99,
            JobKind = "MASTER",
            SourceDbId = 6,
            DestDbId = 8,
            EntityCode = EntityCodes.Item,
            DryRun = false,
            CreatedBy = "test@example.com",
            PayloadJson = JsonSerializer.Serialize(new MasterSyncPayload
            {
                Record = new MasterRecord { Code = code, Description = $"Item {code}" },
                SourceEntity = EntityCodes.Item,
                DestEntity = EntityCodes.Item
            })
        };

        private static RegisteredDatabase TestDatabase(int id, string code) => new()
        {
            DbId = id,
            DbCode = code,
            DisplayName = code,
            FbHost = "localhost",
            FbPath = $"C:/SQLData/{code}.FDB",
            IsActive = true,
            Environment = AppEnvironments.Development
        };
    }

    private sealed class TestReadAdapter(ItemLinkTestHarness harness) : ISqlAccountReadAdapter
    {
        public Task<bool> MasterExistsByCodeAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(harness.DestinationItemExists);

        public Task<MasterRecord?> GetItemDetailAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult<MasterRecord?>(new MasterRecord { Code = code, Description = $"Item {code}" });

        public Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult<StockGroupRecord?>(new StockGroupRecord { Code = code, Description = code });

        public Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) =>
            GetItemDetailAsync(db, code, cancellationToken);

        public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SchemaDiscoveryReport());

        public Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<SqlAccountSync.Services.Accounts.GlAccountRow>> GetGlChartAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<SqlAccountSync.Services.Accounts.GlAccountRow>());
        public Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<List<MasterRecord>> GetTermsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) => Task.FromResult(new List<MasterRecord>());
        public Task<MasterRecord?> GetPartyDetailAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) => Task.FromResult<MasterRecord?>(null);
        public Task<bool> PartyExistsByStampAsync(RegisteredDatabase db, string entityCode, string txnStamp, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default) => Task.FromResult<DocumentHeader?>(null);
        public Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<DocumentListItem>());
        public Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
        public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) => Task.FromResult(new ReconcileResult());
    }

    private sealed class SpyWriteAdapter : ISqlAccountWriteAdapter
    {
        public int CreateMasterCallCount { get; private set; }

        public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default)
        {
            CreateMasterCallCount++;
            return Task.FromResult(new WriteResult { Success = true, DocNo = record.Code, Message = "Created." });
        }

        public Task<WriteResult> UpdateMasterAsync(
            RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
            int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WriteResult { Success = true, DocNo = record.Code, Message = "Updated." });

        public Task<WriteResult> DeleteMasterAsync(
            RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WriteResult { Success = true, Message = "Deleted." });

        public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WriteResult { Success = true });
    }

    private sealed class TestMappingService : IMappingService
    {
        public int ConfirmPairCallCount { get; private set; }

        public Task<EntityMapping?> FindBySourceAsync(int sourceDbId, int destDbId, string srcEntityCode, string sourceCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<EntityMapping?>(null);

        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmPairAsync(EntityMapping forward, string user, bool writeReverse, int? originJobId, CancellationToken cancellationToken = default)
        {
            ConfirmPairCallCount++;
            return Task.FromResult((true, "ok", forward));
        }

        public Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default) => Task.FromResult(new List<EntityMapping>());
        public Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default) => Task.FromResult<EntityMapping?>(null);
        public Task<EntityMapping?> FindPartyMapAsync(int sourceDbId, int destDbId, string sourcePartyEntity, string sourcePartyCode, string? destPartyEntity = null, CancellationToken cancellationToken = default) => Task.FromResult<EntityMapping?>(null);
        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default) => Task.FromResult((true, "ok", mapping));
        public Task TouchLastSyncAtAsync(int mappingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<EntityMapping>());
        public Task<int> CountMappedAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<List<(MasterRecord Source, MasterRecord? Suggested)>> SuggestAsync(IReadOnlyList<MasterRecord> master, IReadOnlyList<MasterRecord> sub) => Task.FromResult(new List<(MasterRecord, MasterRecord?)>());
    }

    private sealed class TestDatabaseRegistry(RegisteredDatabase source, RegisteredDatabase dest) : IDatabaseRegistry
    {
        public Task<RegisteredDatabase?> GetAsync(int dbId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RegisteredDatabase?>(dbId == source.DbId ? source : dbId == dest.DbId ? dest : null);

        public Task MarkSyncOkAsync(int dbId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<RegisteredDatabase>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) => Task.FromResult(new List<RegisteredDatabase> { source, dest });
        public Task<int> SaveAsync(RegisteredDatabase db, string user, CancellationToken cancellationToken = default) => Task.FromResult(db.DbId);
        public Task DeactivateAsync(int dbId, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateStatusAsync(int dbId, string status, bool success, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<DatabaseLink>> ListLinksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<DatabaseLink>());
        public Task<int> AddLinkAsync(int masterId, int subId, string user, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task DeactivateLinkAsync(int linkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DashboardStats> DashboardAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DashboardStats());
    }

    private sealed class TestJobQueue : IJobQueue
    {
        public string? LastStatus { get; private set; }
        public string? LastMessage { get; private set; }

        public Task CompleteAsync(int jobId, string status, string? error, CancellationToken cancellationToken = default)
        {
            LastStatus = status;
            LastMessage = error;
            return Task.CompletedTask;
        }

        public Task<int> EnqueueAsync(SyncJob job, CancellationToken cancellationToken = default) => Task.FromResult(job.JobId);
        public Task<SyncJob?> ClaimAsync(string workerId, CancellationToken cancellationToken = default) => Task.FromResult<SyncJob?>(null);
        public Task<List<SyncJob>> ListAsync(string? status = null, int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<SyncJob>());
        public Task<SyncJob?> GetAsync(int jobId, CancellationToken cancellationToken = default) => Task.FromResult<SyncJob?>(null);
        public Task CancelAsync(int jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> ReclaimStaleProcessingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task UnlockProcessingAsync(int jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<QueueHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueHealthSnapshot());
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<AuditEntry>> ListAsync(int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<AuditEntry>());
        public Task<List<AuditListItem>> ListDetailedAsync(int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<AuditListItem>());
    }

    private sealed class NoopErrorLogService : IErrorLogService
    {
        public Task<int> WriteAsync(ErrorEntry entry, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<List<ErrorEntry>> ListAsync(bool unresolvedOnly = false, int take = 200, CancellationToken cancellationToken = default) => Task.FromResult(new List<ErrorEntry>());
        public Task ResolveAsync(int errorId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopItemPrerequisiteService : IItemPrerequisiteService
    {
        public Task<PrerequisiteResult> EnsureAsync(
            RegisteredDatabase dest, RegisteredDatabase? source, MasterRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrerequisiteResult { Ok = true, Message = "Prerequisites satisfied." });

        public Task<List<PrerequisiteCheck>> CheckAsync(
            RegisteredDatabase dest, RegisteredDatabase? source, MasterRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<PrerequisiteCheck>());
    }

    private sealed class NoopSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default) => Task.FromResult<string?>("masterkey");
    }
}
