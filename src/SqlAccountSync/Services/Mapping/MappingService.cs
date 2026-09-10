using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Mapping;

public interface IMappingService
{
    Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default);
    Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default);
    Task<EntityMapping?> FindBySourceAsync(int sourceDbId, int destDbId, string srcEntityCode, string sourceCode, CancellationToken cancellationToken = default);
    Task<EntityMapping?> FindPartyMapAsync(int sourceDbId, int destDbId, string sourcePartyEntity, string sourcePartyCode, string? destPartyEntity = null, CancellationToken cancellationToken = default);
    Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default);
    Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmPairAsync(EntityMapping forward, string user, bool writeReverse, int? originJobId, CancellationToken cancellationToken = default);
    Task TouchLastSyncAtAsync(int mappingId, CancellationToken cancellationToken = default);
    Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default);
    Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default);
    Task<int> CountMappedAsync(CancellationToken cancellationToken = default);
    Task<List<(MasterRecord Source, MasterRecord? Suggested)>> SuggestAsync(
        IReadOnlyList<MasterRecord> master, IReadOnlyList<MasterRecord> sub);
}

public sealed class MappingService : IMappingService
{
    private readonly CentralDb _db;

    public MappingService(CentralDb db) => _db = db;

    public Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<EntityMapping>());
        return _db.QueryAsync(@"
SELECT * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID
  AND (SRCENTITYCODE=@ENTITYCODE OR ENTITYCODE=@ENTITYCODE)
ORDER BY MASTERCODE", Map, new { MASTERDBID = masterDbId, SUBDBID = subDbId, ENTITYCODE = entityCode }, cancellationToken);
    }

    public Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default) =>
        FindBySourceAsync(masterDbId, subDbId, entityCode, masterCode, cancellationToken);

    public Task<EntityMapping?> FindBySourceAsync(int sourceDbId, int destDbId, string srcEntityCode, string sourceCode, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult<EntityMapping?>(null);
        return _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID
  AND (SRCENTITYCODE=@SRCENTITY OR ENTITYCODE=@SRCENTITY)
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE) AND STATUS='MAPPED'",
            Map,
            new { MASTERDBID = sourceDbId, SUBDBID = destDbId, SRCENTITY = srcEntityCode, MASTERCODE = sourceCode },
            cancellationToken);
    }

    public async Task<EntityMapping?> FindPartyMapAsync(
        int sourceDbId, int destDbId, string sourcePartyEntity, string sourcePartyCode,
        string? destPartyEntity = null, CancellationToken cancellationToken = default)
    {
        if (string.Equals(sourcePartyEntity, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(destPartyEntity, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase))
        {
            var supplier = await _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND STATUS='MAPPED'
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE)
  AND (SRCENTITYCODE='CUSTOMER' OR ENTITYCODE='CUSTOMER')
  AND DSTENTITYCODE='SUPPLIER'",
                Map,
                new { MASTERDBID = sourceDbId, SUBDBID = destDbId, MASTERCODE = sourcePartyCode },
                cancellationToken).ConfigureAwait(false);
            if (supplier is not null)
                return supplier;

            return await _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND STATUS='MAPPED'
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE)
  AND (SRCENTITYCODE='CUSTOMER' OR ENTITYCODE='CUSTOMER')
  AND DSTENTITYCODE IS NULL AND ENTITYCODE='SUPPLIER'",
                Map,
                new { MASTERDBID = sourceDbId, SUBDBID = destDbId, MASTERCODE = sourcePartyCode },
                cancellationToken).ConfigureAwait(false);
        }

        // Sales notes (CN/DN) must stay customer→customer — never fall back to supplier.
        if (string.Equals(destPartyEntity, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase))
        {
            var customer = await FindBySourceAsync(sourceDbId, destDbId, EntityCodes.Customer, sourcePartyCode, cancellationToken)
                .ConfigureAwait(false);
            if (customer is not null
                && (string.IsNullOrWhiteSpace(customer.DstEntityCode)
                    || string.Equals(customer.DstEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)))
                return customer;

            return await _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND STATUS='MAPPED'
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE)
  AND (SRCENTITYCODE='CUSTOMER' OR ENTITYCODE='CUSTOMER')
  AND (DSTENTITYCODE='CUSTOMER' OR (DSTENTITYCODE IS NULL AND ENTITYCODE='CUSTOMER'))",
                Map,
                new { MASTERDBID = sourceDbId, SUBDBID = destDbId, MASTERCODE = sourcePartyCode },
                cancellationToken).ConfigureAwait(false);
        }

        var direct = await FindBySourceAsync(sourceDbId, destDbId, sourcePartyEntity, sourcePartyCode, cancellationToken)
            .ConfigureAwait(false);
        if (direct is not null)
            return direct;

        // Legacy rows or reverse lookup: customer on SI may map to supplier on dest.
        if (string.Equals(sourcePartyEntity, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase))
        {
            return await _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND STATUS='MAPPED'
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE)
  AND (SRCENTITYCODE='CUSTOMER' OR ENTITYCODE='CUSTOMER')
  AND (DSTENTITYCODE='SUPPLIER' OR (DSTENTITYCODE IS NULL AND ENTITYCODE='CUSTOMER'))",
                Map,
                new { MASTERDBID = sourceDbId, SUBDBID = destDbId, MASTERCODE = sourcePartyCode },
                cancellationToken).ConfigureAwait(false);
        }

        return await FindBySourceAsync(sourceDbId, destDbId, EntityCodes.Supplier, sourcePartyCode, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default)
    {
        NormalizeEntityTypes(mapping);
        mapping.Status = MappingStatuses.Mapped;
        mapping.UpdatedBy = user;
        try
        {
            if (mapping.MappingId == 0)
            {
                var id = (int)await _db.NextIdAsync("GEN_SYNC_MAPPING", cancellationToken).ConfigureAwait(false);
                mapping.MappingId = id;
                await _db.ExecuteAsync(@"
INSERT INTO SYNC_MAPPING (MAPPINGID, MASTERDBID, SUBDBID, ENTITYCODE, SRCENTITYCODE, DSTENTITYCODE,
 MASTERCODE, MASTERKEY, SUBCODE, SUBKEY, MASTERDESC, SUBDESC, STATUS, ORIGINJOBID, LASTSYNCAT,
 CREATEDAT, CREATEDBY, UPDATEDAT, UPDATEDBY)
VALUES (@MAPPINGID, @MASTERDBID, @SUBDBID, @ENTITYCODE, @SRCENTITYCODE, @DSTENTITYCODE,
 @MASTERCODE, @MASTERKEY, @SUBCODE, @SUBKEY, @MASTERDESC, @SUBDESC, 'MAPPED', @ORIGINJOBID, @LASTSYNCAT,
 CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP, @USER)",
                    Args(mapping, user), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _db.ExecuteAsync(@"
UPDATE SYNC_MAPPING SET MASTERCODE=@MASTERCODE, MASTERKEY=@MASTERKEY, SUBCODE=@SUBCODE, SUBKEY=@SUBKEY,
 SRCENTITYCODE=@SRCENTITYCODE, DSTENTITYCODE=@DSTENTITYCODE, ENTITYCODE=@ENTITYCODE,
 MASTERDESC=@MASTERDESC, SUBDESC=@SUBDESC, STATUS='MAPPED', ORIGINJOBID=@ORIGINJOBID, LASTSYNCAT=@LASTSYNCAT,
 UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER
WHERE MAPPINGID=@MAPPINGID", Args(mapping, user), cancellationToken).ConfigureAwait(false);
            }
            return (true, "Mapping confirmed.", mapping);
        }
        catch (FbException ex) when (ex.Message.Contains("violation of PRIMARY or UNIQUE KEY", StringComparison.OrdinalIgnoreCase)
                                     || ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            await TryMarkConflict(mapping, user, cancellationToken).ConfigureAwait(false);
            return (false, "This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.", null);
        }
    }

    public async Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmPairAsync(
        EntityMapping forward, string user, bool writeReverse, int? originJobId, CancellationToken cancellationToken = default)
    {
        forward.OriginJobId = originJobId;
        forward.LastSyncAt = DateTime.UtcNow;
        var result = await ConfirmAsync(forward, user, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || !writeReverse)
            return result;

        var reverse = new EntityMapping
        {
            MasterDbId = forward.SubDbId,
            SubDbId = forward.MasterDbId,
            SrcEntityCode = forward.DstEntityCode,
            DstEntityCode = forward.SrcEntityCode,
            EntityCode = forward.DstEntityCode,
            MasterCode = forward.SubCode,
            SubCode = forward.MasterCode,
            MasterDesc = forward.SubDesc,
            SubDesc = forward.MasterDesc,
            OriginJobId = originJobId,
            LastSyncAt = DateTime.UtcNow
        };
        try
        {
            await ConfirmAsync(reverse, user, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Reverse row is best-effort; forward map is canonical.
        }
        return result;
    }

    public Task TouchLastSyncAtAsync(int mappingId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_MAPPING SET LASTSYNCAT=CURRENT_TIMESTAMP WHERE MAPPINGID=@MAPPINGID",
            new { MAPPINGID = mappingId }, cancellationToken);

    public Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_MAPPING SET STATUS='DISABLED', UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER WHERE MAPPINGID=@MAPPINGID",
            new { MAPPINGID = mappingId, USER = user }, cancellationToken);

    public Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<EntityMapping>());
        return _db.QueryAsync("SELECT * FROM SYNC_MAPPING WHERE STATUS='CONFLICT' ORDER BY UPDATEDAT DESC", Map, cancellationToken: cancellationToken);
    }

    public async Task<int> CountMappedAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return 0;
        var n = await _db.ScalarAsync(
            "SELECT COUNT(*) FROM SYNC_MAPPING WHERE STATUS='MAPPED'",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(n ?? 0);
    }

    public Task<List<(MasterRecord Source, MasterRecord? Suggested)>> SuggestAsync(
        IReadOnlyList<MasterRecord> master, IReadOnlyList<MasterRecord> sub)
    {
        var byCode = sub.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var byDesc = sub.GroupBy(s => Normalize(s.Description)).ToDictionary(g => g.Key, g => g.First());
        var list = new List<(MasterRecord, MasterRecord?)>();
        foreach (var m in master)
        {
            MasterRecord? sug = null;
            if (byCode.TryGetValue(m.Code, out var hit))
                sug = hit;
            else if (byDesc.TryGetValue(Normalize(m.Description), out var dhit) && !string.IsNullOrWhiteSpace(m.Description))
                sug = dhit;
            list.Add((m, sug));
        }
        return Task.FromResult(list);
    }

    private async Task TryMarkConflict(EntityMapping mapping, string user, CancellationToken cancellationToken)
    {
        NormalizeEntityTypes(mapping);
        try
        {
            var id = mapping.MappingId == 0
                ? (int)await _db.NextIdAsync("GEN_SYNC_MAPPING", cancellationToken).ConfigureAwait(false)
                : mapping.MappingId;
            mapping.MappingId = id;
            mapping.Status = MappingStatuses.Conflict;
            await _db.ExecuteAsync(@"
UPDATE OR INSERT INTO SYNC_MAPPING (MAPPINGID, MASTERDBID, SUBDBID, ENTITYCODE, SRCENTITYCODE, DSTENTITYCODE,
 MASTERCODE, MASTERKEY, SUBCODE, SUBKEY, MASTERDESC, SUBDESC, STATUS, ORIGINJOBID, LASTSYNCAT,
 CREATEDAT, CREATEDBY, UPDATEDAT, UPDATEDBY)
VALUES (@MAPPINGID, @MASTERDBID, @SUBDBID, @ENTITYCODE, @SRCENTITYCODE, @DSTENTITYCODE,
 @MASTERCODE, @MASTERKEY, @SUBCODE, @SUBKEY, @MASTERDESC, @SUBDESC, 'CONFLICT', @ORIGINJOBID, @LASTSYNCAT,
 CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP, @USER)
MATCHING (MAPPINGID)", Args(mapping, user), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // unique still blocked — leave existing mapped row untouched
        }
    }

    private static void NormalizeEntityTypes(EntityMapping m)
    {
        if (string.IsNullOrWhiteSpace(m.SrcEntityCode))
            m.SrcEntityCode = m.EntityCode;
        if (string.IsNullOrWhiteSpace(m.DstEntityCode))
            m.DstEntityCode = m.EntityCode;
        if (string.IsNullOrWhiteSpace(m.EntityCode))
            m.EntityCode = m.SrcEntityCode;
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToUpperInvariant();

    private static object Args(EntityMapping m, string user) => new
    {
        MAPPINGID = m.MappingId,
        MASTERDBID = m.MasterDbId,
        SUBDBID = m.SubDbId,
        ENTITYCODE = m.EntityCode,
        SRCENTITYCODE = m.SrcEntityCode,
        DSTENTITYCODE = m.DstEntityCode,
        MASTERCODE = m.MasterCode,
        MASTERKEY = m.MasterKey,
        SUBCODE = m.SubCode,
        SUBKEY = m.SubKey,
        MASTERDESC = m.MasterDesc,
        SUBDESC = m.SubDesc,
        ORIGINJOBID = m.OriginJobId,
        LASTSYNCAT = m.LastSyncAt,
        USER = user
    };

    private static EntityMapping Map(FbDataReader r) => new()
    {
        MappingId = CentralDb.Int(r, "MAPPINGID"),
        MasterDbId = CentralDb.Int(r, "MASTERDBID"),
        SubDbId = CentralDb.Int(r, "SUBDBID"),
        EntityCode = CentralDb.Str(r, "ENTITYCODE"),
        SrcEntityCode = ReadSrcEntity(r),
        DstEntityCode = ReadDstEntity(r),
        MasterCode = CentralDb.Str(r, "MASTERCODE"),
        MasterKey = CentralDb.Str(r, "MASTERKEY"),
        SubCode = CentralDb.Str(r, "SUBCODE"),
        SubKey = CentralDb.Str(r, "SUBKEY"),
        MasterDesc = CentralDb.Str(r, "MASTERDESC"),
        SubDesc = CentralDb.Str(r, "SUBDESC"),
        Status = CentralDb.Str(r, "STATUS"),
        OriginJobId = CentralDb.IntN(r, "ORIGINJOBID"),
        LastSyncAt = CentralDb.Time(r, "LASTSYNCAT"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        UpdatedAt = CentralDb.TimeReq(r, "UPDATEDAT"),
        UpdatedBy = CentralDb.Str(r, "UPDATEDBY")
    };

    private static string ReadSrcEntity(FbDataReader r)
    {
        var src = CentralDb.Str(r, "SRCENTITYCODE");
        return string.IsNullOrWhiteSpace(src) ? CentralDb.Str(r, "ENTITYCODE") : src;
    }

    private static string ReadDstEntity(FbDataReader r)
    {
        var dst = CentralDb.Str(r, "DSTENTITYCODE");
        return string.IsNullOrWhiteSpace(dst) ? CentralDb.Str(r, "ENTITYCODE") : dst;
    }
}
