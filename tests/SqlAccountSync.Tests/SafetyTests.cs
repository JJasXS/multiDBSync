using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Validation;

namespace SqlAccountSync.Tests;

public class IdempotencyTests
{
    [Fact]
    public void Same_inputs_produce_same_key()
    {
        var a = IdempotencyKey.ForTransaction("A", "B", "SI_TO_PI", "IV", "123", "SI-000123");
        var b = IdempotencyKey.ForTransaction("a", "b", "SI_TO_PI", "IV", "123", "SI-000123");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Different_documents_produce_different_keys()
    {
        var a = IdempotencyKey.ForTransaction("A", "B", "SI_TO_PI", "IV", "123", "SI-000123");
        var b = IdempotencyKey.ForTransaction("A", "B", "SI_TO_PI", "IV", "124", "SI-000124");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Master_data_hash_changes_with_content()
    {
        var a = IdempotencyKey.ForMasterData("A", "B", "ITEM", "ITEM", "ITEM001", IdempotencyKey.ContentHash("desc1"));
        var b = IdempotencyKey.ForMasterData("A", "B", "ITEM", "ITEM", "ITEM001", IdempotencyKey.ContentHash("desc2"));
        Assert.NotEqual(a, b);
    }
}

public class SqlGuardTests
{
    [Fact]
    public void Allows_select() => SqlGuard.EnsureSelectOnly("SELECT CODE FROM ST_ITEM");

    [Fact]
    public void Rejects_insert() =>
        Assert.Throws<InvalidOperationException>(() => SqlGuard.EnsureSelectOnly("INSERT INTO ST_ITEM (CODE) VALUES ('X')"));

    [Fact]
    public void Rejects_compound_sql() =>
        Assert.Throws<InvalidOperationException>(() => SqlGuard.EnsureSelectOnly("SELECT 1; DROP TABLE ST_ITEM"));

    [Theory]
    [InlineData("DELETE FROM ST_ITEM")]
    [InlineData("UPDATE ST_ITEM SET CODE='X'")]
    [InlineData("DROP TABLE ST_ITEM")]
    public void Rejects_mutating(string sql) =>
        Assert.Throws<InvalidOperationException>(() => SqlGuard.EnsureSelectOnly(sql));
}

public class CredentialRedactorTests
{
    [Fact]
    public void Redacts_password_in_connection_string()
    {
        var s = CredentialRedactor.Redact("User=SYSDBA;Password=secret123;Database=X");
        Assert.DoesNotContain("secret123", s);
        Assert.Contains("***", s);
    }
}

public class FirebirdErrorMapperTests
{
    [Fact]
    public void Maps_timeout() =>
        Assert.Equal(ConnectionResults.Timeout, FirebirdErrorMapper.Map(new TimeoutException("timeout")));

    [Fact]
    public void Maps_auth() =>
        Assert.Equal(ConnectionResults.AuthenticationFailed, FirebirdErrorMapper.Map(new Exception("Your user name and password are not defined")));
}

public class ValidationTests
{
    private sealed class FakeMapping : IMappingService
    {
        public List<EntityMapping> Maps { get; } = [];
        public Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default) => Task.FromResult(Maps);
        public Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default) =>
            FindBySourceAsync(masterDbId, subDbId, entityCode, masterCode, cancellationToken);
        public Task<EntityMapping?> FindBySourceAsync(int sourceDbId, int destDbId, string srcEntityCode, string sourceCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Maps.FirstOrDefault(m =>
                (m.SrcEntityCode == srcEntityCode || m.EntityCode == srcEntityCode)
                && m.MasterCode == sourceCode
                && m.Status == MappingStatuses.Mapped));
        public Task<EntityMapping?> FindPartyMapAsync(int sourceDbId, int destDbId, string sourcePartyEntity, string sourcePartyCode, string? destPartyEntity = null, CancellationToken cancellationToken = default) =>
            FindBySourceAsync(sourceDbId, destDbId, sourcePartyEntity, sourcePartyCode, cancellationToken);
        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default) => Task.FromResult<(bool, string, EntityMapping?)>((true, "", mapping));
        public Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmPairAsync(EntityMapping forward, string user, bool writeReverse, int? originJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(bool, string, EntityMapping?)>((true, "", forward));
        public Task TouchLastSyncAtAsync(int mappingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<EntityMapping>());
        public Task<int> CountMappedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Maps.Count(m => m.Status == MappingStatuses.Mapped));
        public Task<List<(MasterRecord Source, MasterRecord? Suggested)>> SuggestAsync(IReadOnlyList<MasterRecord> master, IReadOnlyList<MasterRecord> sub) => Task.FromResult(new List<(MasterRecord, MasterRecord?)>());
    }

    [Fact]
    public void Master_requires_code_and_description()
    {
        var v = new ValidationService(new FakeMapping());
        var fail = v.ValidateMaster(new MasterRecord(), "ITEM");
        Assert.False(fail.Ok);
        var ok = v.ValidateMaster(new MasterRecord { Code = "I", Description = "Item" }, "ITEM");
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Document_fails_without_item_mapping()
    {
        var maps = new FakeMapping();
        maps.Maps.Add(new EntityMapping { EntityCode = EntityCodes.Customer, MasterCode = "C001", Status = MappingStatuses.Mapped, SubCode = "X" });
        var v = new ValidationService(maps);
        var doc = new DocumentHeader
        {
            DocNo = "SI-1",
            DocDate = DateTime.Today,
            PartyCode = "C001",
            Currency = "MYR",
            Lines = [new DocumentLine { Seq = 1, ItemCode = "ITEM001", Qty = 10, UnitPrice = 1, Amount = 10 }]
        };
        var result = await v.ValidateMappedDocumentAsync(doc, 1, 2);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("item mapping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Document_passes_when_mappings_exist()
    {
        var maps = new FakeMapping();
        maps.Maps.Add(new EntityMapping { EntityCode = EntityCodes.Customer, MasterCode = "C001", Status = MappingStatuses.Mapped, SubCode = "B" });
        maps.Maps.Add(new EntityMapping { EntityCode = EntityCodes.Item, MasterCode = "ITEM001", Status = MappingStatuses.Mapped, SubCode = "A-100" });
        var v = new ValidationService(maps);
        var doc = new DocumentHeader
        {
            DocNo = "SI-1",
            DocDate = DateTime.Today,
            PartyCode = "C001",
            Currency = "MYR",
            Lines = [new DocumentLine { Seq = 1, ItemCode = "ITEM001", Qty = 10, UnitPrice = 100, Amount = 1000 }]
        };
        var result = await v.ValidateMappedDocumentAsync(doc, 1, 2);
        Assert.True(result.Ok);
    }

    [Fact]
    public void Inactive_database_fails()
    {
        var v = new ValidationService(new FakeMapping());
        var r = v.ValidateDatabase(new RegisteredDatabase { IsActive = false, FbHost = "h", FbPath = "p" }, "Source");
        Assert.False(r.Ok);
    }
}

public class DryRunAdapterTests
{
    [Fact]
    public async Task Dry_run_does_not_claim_real_write()
    {
        var adapter = new DryRunWriteAdapter();
        var result = await adapter.CreateDocumentAsync(new RegisteredDatabase { DisplayName = "B" }, "/purchaseinvoice",
            new DocumentHeader { Amount = 1000, Lines = [new DocumentLine { Qty = 10 }] }, "TXN-1");
        Assert.True(result.Success);
        Assert.Contains("no data was modified", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WOULD POST", adapter.Actions[0]);
    }
}

public class MappingSuggestTests
{
    [Fact]
    public async Task Suggests_by_code_but_does_not_auto_apply()
    {
        var svc = new MappingService(null!);
        var master = new List<MasterRecord> { new() { Code = "ITEM001", Description = "Samsung SSD 1TB" } };
        var sub = new List<MasterRecord> { new() { Code = "A-100", Description = "Samsung SSD 1TB" }, new() { Code = "ITEM001", Description = "Other" } };
        var sug = await svc.SuggestAsync(master, sub);
        Assert.Equal("ITEM001", sug[0].Suggested!.Code);
    }
}

