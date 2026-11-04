using SqlAccountSync.Models;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Tenants;
using SqlAccountSync.Services.Validation;

namespace SqlAccountSync.Tests;

public class DocumentTransferDryRunSafetyTests
{
    [Fact]
    public void Six_transfer_types_are_implemented()
    {
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.SalesToPurchase));
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.DeliveryToGr));
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.StockTransfer));
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.CreditToPurchaseReturn));
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.CreditToDebit));
        Assert.True(TxnTypeCodes.IsImplemented(TxnTypeCodes.DebitToCredit));
        Assert.False(TxnTypeCodes.IsImplemented(TxnTypeCodes.SalesReturnToPr));
    }

    [Fact]
    public void Development_force_keeps_document_transfer_visible_when_tenant_hides_it()
    {
        var menu = new TenantMenuFlags();
        menu.ApplyTenantPlan(showSynchronization: true, showDocumentTransfer: false, forceShowDocumentTransfer: true);
        Assert.True(menu.ShowDocumentTransfer);
        Assert.True(menu.ShowSynchronization);

        menu.ApplyTenantPlan(showSynchronization: true, showDocumentTransfer: false, forceShowDocumentTransfer: false);
        Assert.False(menu.ShowDocumentTransfer);
    }

    [Fact]
    public void ValidateDatabase_does_not_require_rest_api_credentials()
    {
        var v = new ValidationService(new StubMapping());
        var db = new RegisteredDatabase
        {
            IsActive = true,
            FbHost = "192.168.100.201",
            FbPath = @"\\share\ACC-MIRACLE.FDB",
            ApiBaseUrl = "",
            ApiCredRef = ""
        };
        Assert.True(v.ValidateDatabase(db, "Destination").Ok);
    }

    private sealed class StubMapping : IMappingService
    {
        public Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<EntityMapping>());
        public Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default)
            => Task.FromResult<EntityMapping?>(null);
        public Task<EntityMapping?> FindBySourceAsync(int sourceDbId, int destDbId, string srcEntityCode, string sourceCode, CancellationToken cancellationToken = default)
            => Task.FromResult<EntityMapping?>(null);
        public Task<EntityMapping?> FindPartyMapAsync(int sourceDbId, int destDbId, string sourcePartyEntity, string sourcePartyCode, string? destPartyEntity = null, CancellationToken cancellationToken = default)
            => Task.FromResult<EntityMapping?>(null);
        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default)
            => Task.FromResult<(bool, string, EntityMapping?)>((false, "unused", null));
        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmPairAsync(EntityMapping forward, string user, bool writeReverse, int? originJobId, CancellationToken cancellationToken = default)
            => Task.FromResult<(bool, string, EntityMapping?)>((false, "unused", null));
        public Task TouchLastSyncAtAsync(int mappingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<EntityMapping>());
        public Task<int> CountMappedAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<List<(MasterRecord Source, MasterRecord? Suggested)>> SuggestAsync(IReadOnlyList<MasterRecord> master, IReadOnlyList<MasterRecord> sub)
            => Task.FromResult(new List<(MasterRecord, MasterRecord?)>());
    }
}
