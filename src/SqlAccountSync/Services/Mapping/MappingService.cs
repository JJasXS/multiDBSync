using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Mapping;

public interface IMappingService
{
    Task<List<EntityMapping>> ListAsync(int masterDbId, int subDbId, string entityCode, CancellationToken cancellationToken = default);
    Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default);
    Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default);
    Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default);
    Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default);
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
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND ENTITYCODE=@ENTITYCODE
ORDER BY MASTERCODE", Map, new { MASTERDBID = masterDbId, SUBDBID = subDbId, ENTITYCODE = entityCode }, cancellationToken);
    }

    public Task<EntityMapping?> FindAsync(int masterDbId, int subDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult<EntityMapping?>(null);
        return _db.QuerySingleAsync(@"
SELECT FIRST 1 * FROM SYNC_MAPPING
WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID AND ENTITYCODE=@ENTITYCODE
  AND UPPER(MASTERCODE)=UPPER(@MASTERCODE) AND STATUS='MAPPED'",
            Map, new { MASTERDBID = masterDbId, SUBDBID = subDbId, ENTITYCODE = entityCode, MASTERCODE = masterCode }, cancellationToken);
    }

    public async Task<(bool Ok, string Message, EntityMapping? Mapping)> ConfirmAsync(EntityMapping mapping, string user, CancellationToken cancellationToken = default)
    {
        mapping.Status = MappingStatuses.Mapped;
        mapping.UpdatedBy = user;
        try
        {
            if (mapping.MappingId == 0)
            {
                var id = (int)await _db.NextIdAsync("GEN_SYNC_MAPPING", cancellationToken).ConfigureAwait(false);
                mapping.MappingId = id;
                await _db.ExecuteAsync(@"
INSERT INTO SYNC_MAPPING (MAPPINGID, MASTERDBID, SUBDBID, ENTITYCODE, MASTERCODE, MASTERKEY, SUBCODE, SUBKEY, MASTERDESC, SUBDESC, STATUS, CREATEDAT, CREATEDBY, UPDATEDAT, UPDATEDBY)
VALUES (@MAPPINGID, @MASTERDBID, @SUBDBID, @ENTITYCODE, @MASTERCODE, @MASTERKEY, @SUBCODE, @SUBKEY, @MASTERDESC, @SUBDESC, 'MAPPED', CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP, @USER)",
                    Args(mapping, user), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _db.ExecuteAsync(@"
UPDATE SYNC_MAPPING SET MASTERCODE=@MASTERCODE, MASTERKEY=@MASTERKEY, SUBCODE=@SUBCODE, SUBKEY=@SUBKEY,
 MASTERDESC=@MASTERDESC, SUBDESC=@SUBDESC, STATUS='MAPPED', UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER
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

    public Task DisableAsync(int mappingId, string user, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_MAPPING SET STATUS='DISABLED', UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER WHERE MAPPINGID=@MAPPINGID",
            new { MAPPINGID = mappingId, USER = user }, cancellationToken);

    public Task<List<EntityMapping>> ConflictsAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<EntityMapping>());
        return _db.QueryAsync("SELECT * FROM SYNC_MAPPING WHERE STATUS='CONFLICT' ORDER BY UPDATEDAT DESC", Map, cancellationToken: cancellationToken);
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
        try
        {
            var id = mapping.MappingId == 0
                ? (int)await _db.NextIdAsync("GEN_SYNC_MAPPING", cancellationToken).ConfigureAwait(false)
                : mapping.MappingId;
            mapping.MappingId = id;
            mapping.Status = MappingStatuses.Conflict;
            await _db.ExecuteAsync(@"
UPDATE OR INSERT INTO SYNC_MAPPING (MAPPINGID, MASTERDBID, SUBDBID, ENTITYCODE, MASTERCODE, MASTERKEY, SUBCODE, SUBKEY, MASTERDESC, SUBDESC, STATUS, CREATEDAT, CREATEDBY, UPDATEDAT, UPDATEDBY)
VALUES (@MAPPINGID, @MASTERDBID, @SUBDBID, @ENTITYCODE, @MASTERCODE, @MASTERKEY, @SUBCODE, @SUBKEY, @MASTERDESC, @SUBDESC, 'CONFLICT', CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP, @USER)
MATCHING (MAPPINGID)", Args(mapping, user), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // unique still blocked — leave existing mapped row untouched
        }
    }

    private static string Normalize(string s) => (s ?? "").Trim().ToUpperInvariant();

    private static object Args(EntityMapping m, string user) => new
    {
        MAPPINGID = m.MappingId,
        MASTERDBID = m.MasterDbId,
        SUBDBID = m.SubDbId,
        ENTITYCODE = m.EntityCode,
        MASTERCODE = m.MasterCode,
        MASTERKEY = m.MasterKey,
        SUBCODE = m.SubCode,
        SUBKEY = m.SubKey,
        MASTERDESC = m.MasterDesc,
        SUBDESC = m.SubDesc,
        USER = user
    };

    private static EntityMapping Map(FbDataReader r) => new()
    {
        MappingId = CentralDb.Int(r, "MAPPINGID"),
        MasterDbId = CentralDb.Int(r, "MASTERDBID"),
        SubDbId = CentralDb.Int(r, "SUBDBID"),
        EntityCode = CentralDb.Str(r, "ENTITYCODE"),
        MasterCode = CentralDb.Str(r, "MASTERCODE"),
        MasterKey = CentralDb.Str(r, "MASTERKEY"),
        SubCode = CentralDb.Str(r, "SUBCODE"),
        SubKey = CentralDb.Str(r, "SUBKEY"),
        MasterDesc = CentralDb.Str(r, "MASTERDESC"),
        SubDesc = CentralDb.Str(r, "SUBDESC"),
        Status = CentralDb.Str(r, "STATUS"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        UpdatedAt = CentralDb.TimeReq(r, "UPDATEDAT"),
        UpdatedBy = CentralDb.Str(r, "UPDATEDBY")
    };
}
