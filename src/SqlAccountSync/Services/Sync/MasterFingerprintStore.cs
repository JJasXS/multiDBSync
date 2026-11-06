using SqlAccountSync.Data;

namespace SqlAccountSync.Services.Sync;

public interface IMasterFingerprintStore
{
    Task<Dictionary<string, string>> ListAsync(
        int masterDbId, string entityCode, CancellationToken cancellationToken = default);

    Task ReplaceAllAsync(
        int masterDbId,
        string entityCode,
        IReadOnlyDictionary<string, string> hashes,
        CancellationToken cancellationToken = default);
}

public sealed class MasterFingerprintStore : IMasterFingerprintStore
{
    private readonly CentralDb _db;

    public MasterFingerprintStore(CentralDb db) => _db = db;

    public async Task<Dictionary<string, string>> ListAsync(
        int masterDbId, string entityCode, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rows = await _db.QueryAsync(
            """
            SELECT CODE, CONTENTHASH FROM SYNC_MASTER_FINGERPRINT
            WHERE MASTERDBID=@MASTERDBID AND ENTITYCODE=@ENTITYCODE
            """,
            r => (CentralDb.Str(r, "CODE"), CentralDb.Str(r, "CONTENTHASH")),
            new { MASTERDBID = masterDbId, ENTITYCODE = entityCode },
            cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, hash) in rows)
        {
            var c = (code ?? "").Trim();
            if (c.Length == 0)
                continue;
            map[c] = (hash ?? "").Trim();
        }

        return map;
    }

    public async Task ReplaceAllAsync(
        int masterDbId,
        string entityCode,
        IReadOnlyDictionary<string, string> hashes,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return;

        await _db.ExecuteAsync(
            """
            DELETE FROM SYNC_MASTER_FINGERPRINT
            WHERE MASTERDBID=@MASTERDBID AND ENTITYCODE=@ENTITYCODE
            """,
            new { MASTERDBID = masterDbId, ENTITYCODE = entityCode },
            cancellationToken).ConfigureAwait(false);

        foreach (var (code, hash) in hashes)
        {
            var c = (code ?? "").Trim();
            var h = (hash ?? "").Trim();
            if (c.Length == 0 || h.Length == 0)
                continue;

            await _db.ExecuteAsync(
                """
                INSERT INTO SYNC_MASTER_FINGERPRINT (MASTERDBID, ENTITYCODE, CODE, CONTENTHASH, UPDATEDAT)
                VALUES (@MASTERDBID, @ENTITYCODE, @CODE, @CONTENTHASH, CURRENT_TIMESTAMP)
                """,
                new
                {
                    MASTERDBID = masterDbId,
                    ENTITYCODE = entityCode,
                    CODE = Truncate(c, 40),
                    CONTENTHASH = Truncate(h, 64)
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
