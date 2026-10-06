using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Databases;

public interface IDatabaseRegistry
{
    Task<List<RegisteredDatabase>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<RegisteredDatabase?> GetAsync(int dbId, CancellationToken cancellationToken = default);
    Task<int> SaveAsync(RegisteredDatabase db, string user, CancellationToken cancellationToken = default);
    Task DeactivateAsync(int dbId, string user, CancellationToken cancellationToken = default);
    Task DeleteAsync(int dbId, CancellationToken cancellationToken = default);
    Task SetActiveAsync(int dbId, bool active, string user, CancellationToken cancellationToken = default);
    Task PointSqlApiCredRefsAtBundleAsync(string tenantCode, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(int dbId, string status, bool success, CancellationToken cancellationToken = default);
    Task MarkSyncOkAsync(int dbId, CancellationToken cancellationToken = default);
    Task<List<DatabaseLink>> ListLinksAsync(CancellationToken cancellationToken = default);
    Task<int> AddLinkAsync(int masterId, int subId, string user, CancellationToken cancellationToken = default);
    Task DeactivateLinkAsync(int linkId, CancellationToken cancellationToken = default);
    Task<DashboardStats> DashboardAsync(CancellationToken cancellationToken = default);
    Task<DashboardRegistrySnapshot> ReadDashboardRegistryAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardRegistrySnapshot
{
    public bool Configured { get; set; }
    public bool ReadsLiveRegistry { get; set; }
    public DashboardStats Stats { get; set; } = new();
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
}

public sealed class DashboardStats
{
    public int TotalDatabases { get; set; }
    public int Online { get; set; }
    public int Offline { get; set; }
    public int Error { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Failed { get; set; }
    public int CompletedToday { get; set; }
}

public sealed class DatabaseRegistry : IDatabaseRegistry
{
    private readonly CentralDb _db;
    private readonly IRegistryReadGateway _registryReads;
    private readonly SyncOptions _sync;

    public DatabaseRegistry(CentralDb db, IRegistryReadGateway registryReads, IOptions<SyncOptions> sync)
    {
        _db = db;
        _registryReads = registryReads;
        _sync = sync.Value;
    }

    public Task<List<RegisteredDatabase>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<RegisteredDatabase>());
        var sql = includeInactive
            ? "SELECT * FROM SYNC_DATABASE ORDER BY DISPLAYNAME"
            : "SELECT * FROM SYNC_DATABASE WHERE ISACTIVE = 1 ORDER BY DISPLAYNAME";
        return _db.QueryAsync(sql, MapDb, cancellationToken: cancellationToken);
    }

    public Task<RegisteredDatabase?> GetAsync(int dbId, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult<RegisteredDatabase?>(null);
        return _db.QuerySingleAsync("SELECT * FROM SYNC_DATABASE WHERE DBID = @DBID", MapDb, new { DBID = dbId }, cancellationToken);
    }

    public async Task<int> SaveAsync(RegisteredDatabase db, string user, CancellationToken cancellationToken = default)
    {
        int id;
        if (db.DbId == 0)
        {
            id = (int)await _db.NextIdAsync("GEN_SYNC_DATABASE", cancellationToken).ConfigureAwait(false);
            ApplyAutoSqlApiCredRef(db);
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_DATABASE (DBID, DBCODE, DISPLAYNAME, COMPANYNAME, DBROLE, FBHOST, FBPORT, FBPATH, FBCHARSET, FBDIALECT, FBUSER, FBCREDREF, APIBASEURL, APICREDREF, ENVIRONMENT, ISACTIVE, CONNSTATUS, CREATEDAT, CREATEDBY, UPDATEDAT, UPDATEDBY, NOTES)
VALUES (@DBID, @DBCODE, @DISPLAYNAME, @COMPANYNAME, @DBROLE, @FBHOST, @FBPORT, @FBPATH, @FBCHARSET, @FBDIALECT, @FBUSER, @FBCREDREF, @APIBASEURL, @APICREDREF, @ENVIRONMENT, 1, 'OFFLINE', CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP, @USER, @NOTES)",
                Args(db, id, user), cancellationToken).ConfigureAwait(false);
            return id;
        }

        id = db.DbId;
        ApplyAutoSqlApiCredRef(db);
        await _db.ExecuteAsync(@"
UPDATE SYNC_DATABASE SET
    DBCODE=@DBCODE, DISPLAYNAME=@DISPLAYNAME, COMPANYNAME=@COMPANYNAME, DBROLE=@DBROLE,
    FBHOST=@FBHOST, FBPORT=@FBPORT, FBPATH=@FBPATH, FBCHARSET=@FBCHARSET, FBDIALECT=@FBDIALECT,
    FBUSER=@FBUSER, FBCREDREF=@FBCREDREF, APIBASEURL=@APIBASEURL, APICREDREF=@APICREDREF,
    ENVIRONMENT=@ENVIRONMENT, UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER, NOTES=@NOTES
WHERE DBID=@DBID AND ISACTIVE=1", Args(db, id, user), cancellationToken).ConfigureAwait(false);
        return id;
    }

    private void ApplyAutoSqlApiCredRef(RegisteredDatabase db)
    {
        if (!_sync.AutoAssignSqlApiCredRefByDbId)
            return;
        if (string.IsNullOrWhiteSpace(_sync.TenantCode))
            return;
        if (!SqlApiCredRefConvention.ShouldAutoAssign(db.ApiCredRef, _sync.TenantCode))
            return;

        var bundle = SqlApiCredRefConvention.BuildBundleCredRef(_sync.TenantCode);
        if (bundle.Length > 0)
            db.ApiCredRef = bundle;
    }

    public Task PointSqlApiCredRefsAtBundleAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var cred = SqlApiCredRefConvention.BuildBundleCredRef(tenantCode);
        var prefix = SqlApiCredRefConvention.LegacyPrefix(tenantCode);
        if (cred.Length == 0 || prefix.Length == 0 || !_db.IsConfigured)
            return Task.CompletedTask;
        return _db.ExecuteAsync(@"
UPDATE SYNC_DATABASE SET APICREDREF = @CRED
WHERE UPPER(APICREDREF) LIKE UPPER(@SM) || '%'
   OR UPPER(APICREDREF) LIKE UPPER(@BARE) || '%'",
            new { CRED = cred, SM = "sm:" + prefix, BARE = prefix }, cancellationToken);
    }

    public Task DeactivateAsync(int dbId, string user, CancellationToken cancellationToken = default) =>
        SetActiveAsync(dbId, false, user, cancellationToken);

    public async Task DeleteAsync(int dbId, CancellationToken cancellationToken = default)
    {
        foreach (var sql in DeleteSql)
            await _db.ExecuteAsync(sql, new { DBID = dbId }, cancellationToken).ConfigureAwait(false);
    }

    private static readonly string[] DeleteSql =
    [
        """
        DELETE FROM SYNC_RULE_SCOPE WHERE RULEID IN (
            SELECT R.RULEID FROM SYNC_RULE R
            JOIN SYNC_DBLINK L ON L.LINKID = R.LINKID
            WHERE L.MASTERDBID = @DBID OR L.SUBDBID = @DBID)
        """,
        """
        DELETE FROM SYNC_RULE WHERE LINKID IN (
            SELECT LINKID FROM SYNC_DBLINK WHERE MASTERDBID = @DBID OR SUBDBID = @DBID)
        """,
        "DELETE FROM SYNC_DBLINK WHERE MASTERDBID = @DBID OR SUBDBID = @DBID",
        "DELETE FROM SYNC_MAPPING WHERE MASTERDBID = @DBID OR SUBDBID = @DBID",
        "DELETE FROM SYNC_USER_DBACL WHERE DBID = @DBID",
        "DELETE FROM SYNC_DATABASE WHERE DBID = @DBID"
    ];

    public Task SetActiveAsync(int dbId, bool active, string user, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync(
            "UPDATE SYNC_DATABASE SET ISACTIVE=@ACTIVE, UPDATEDAT=CURRENT_TIMESTAMP, UPDATEDBY=@USER WHERE DBID=@DBID",
            new { DBID = dbId, ACTIVE = active ? 1 : 0, USER = user }, cancellationToken);

    public Task UpdateStatusAsync(int dbId, string status, bool success, CancellationToken cancellationToken = default)
    {
        var sql = success
            ? "UPDATE SYNC_DATABASE SET CONNSTATUS=@S, LASTCONNOK=CURRENT_TIMESTAMP, UPDATEDAT=CURRENT_TIMESTAMP WHERE DBID=@DBID"
            : "UPDATE SYNC_DATABASE SET CONNSTATUS=@S, UPDATEDAT=CURRENT_TIMESTAMP WHERE DBID=@DBID";
        return _db.ExecuteAsync(sql, new { S = status, DBID = dbId }, cancellationToken);
    }

    public Task MarkSyncOkAsync(int dbId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_DATABASE SET LASTSYNCOK=CURRENT_TIMESTAMP WHERE DBID=@DBID", new { DBID = dbId }, cancellationToken);

    public Task<List<DatabaseLink>> ListLinksAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<DatabaseLink>());
        return _db.QueryAsync(@"
SELECT L.*, M.DISPLAYNAME AS MASTERNAME, S.DISPLAYNAME AS SUBNAME
FROM SYNC_DBLINK L
JOIN SYNC_DATABASE M ON M.DBID = L.MASTERDBID
JOIN SYNC_DATABASE S ON S.DBID = L.SUBDBID
WHERE L.ISACTIVE = 1
ORDER BY M.DISPLAYNAME, S.DISPLAYNAME", r => new DatabaseLink
        {
            LinkId = CentralDb.Int(r, "LINKID"),
            MasterDbId = CentralDb.Int(r, "MASTERDBID"),
            SubDbId = CentralDb.Int(r, "SUBDBID"),
            IsActive = CentralDb.Flag(r, "ISACTIVE"),
            MasterName = CentralDb.Str(r, "MASTERNAME"),
            SubName = CentralDb.Str(r, "SUBNAME"),
            CreatedAt = CentralDb.TimeReq(r, "CREATEDAT")
        }, cancellationToken: cancellationToken);
    }

    public async Task<int> AddLinkAsync(int masterId, int subId, string user, CancellationToken cancellationToken = default)
    {
        if (masterId == subId)
            throw new InvalidOperationException("A database cannot be linked to itself. Choose a different Master and Sub.");
        var existing = await ExistingLinkIdAsync(masterId, subId, cancellationToken).ConfigureAwait(false);
        if (existing > 0)
            return await ReactivateLinkAsync(existing, cancellationToken).ConfigureAwait(false);
        var id = (int)await _db.NextIdAsync("GEN_SYNC_DBLINK", cancellationToken).ConfigureAwait(false);
        try
        {
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_DBLINK (LINKID, MASTERDBID, SUBDBID, ISACTIVE, CREATEDAT, CREATEDBY)
VALUES (@LINKID, @MASTERDBID, @SUBDBID, 1, CURRENT_TIMESTAMP, @USER)",
                new { LINKID = id, MASTERDBID = masterId, SUBDBID = subId, USER = user }, cancellationToken).ConfigureAwait(false);
        }
        catch (FirebirdSql.Data.FirebirdClient.FbException ex) when (ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("That Master/Sub relationship already exists.");
        }
        return id;
    }

    public async Task DeactivateLinkAsync(int linkId, CancellationToken cancellationToken = default)
    {
        await _db.ExecuteAsync(
            "UPDATE SYNC_RULE SET ENABLED=0 WHERE LINKID=@LINKID",
            new { LINKID = linkId },
            cancellationToken).ConfigureAwait(false);
        await _db.ExecuteAsync(
            "UPDATE SYNC_DBLINK SET ISACTIVE=0 WHERE LINKID=@LINKID",
            new { LINKID = linkId },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExistingLinkIdAsync(int masterId, int subId, CancellationToken cancellationToken)
    {
        var raw = await _db.ScalarAsync(
            "SELECT LINKID FROM SYNC_DBLINK WHERE MASTERDBID=@MASTERDBID AND SUBDBID=@SUBDBID",
            new { MASTERDBID = masterId, SUBDBID = subId },
            cancellationToken).ConfigureAwait(false);
        return raw is null or DBNull ? 0 : Convert.ToInt32(raw);
    }

    private async Task<int> ReactivateLinkAsync(int linkId, CancellationToken cancellationToken)
    {
        await _db.ExecuteAsync(
            "UPDATE SYNC_DBLINK SET ISACTIVE=1 WHERE LINKID=@LINKID",
            new { LINKID = linkId },
            cancellationToken).ConfigureAwait(false);
        return linkId;
    }

    public async Task<DashboardStats> DashboardAsync(CancellationToken cancellationToken = default)
    {
        var stats = new DashboardStats();
        if (!_db.IsConfigured)
            return stats;
        stats.TotalDatabases = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1", cancellationToken: cancellationToken) ?? 0);
        stats.Online = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS='ONLINE'", cancellationToken: cancellationToken) ?? 0);
        stats.Offline = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS IN ('OFFLINE','NETWORK_ERROR','TIMEOUT')", cancellationToken: cancellationToken) ?? 0);
        stats.Error = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS NOT IN ('ONLINE','OFFLINE')", cancellationToken: cancellationToken) ?? 0);
        stats.Pending = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('PENDING','RETRYING')", cancellationToken: cancellationToken) ?? 0);
        stats.Processing = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS='PROCESSING'", cancellationToken: cancellationToken) ?? 0);
        stats.Failed = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('FAILED','CONFLICT','WAITING_FOR_MAPPING')", cancellationToken: cancellationToken) ?? 0);
        stats.CompletedToday = Convert.ToInt32(await _db.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS='COMPLETED' AND CREATEDAT >= CURRENT_DATE", cancellationToken: cancellationToken) ?? 0);
        return stats;
    }

    public async Task<DashboardRegistrySnapshot> ReadDashboardRegistryAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new DashboardRegistrySnapshot { ReadsLiveRegistry = _registryReads.ReadsLiveRegistry };
        if (!_registryReads.IsConfigured)
            return snapshot;

        snapshot.Configured = true;
        snapshot.Databases = await _registryReads.QueryAsync(
            "SELECT * FROM SYNC_DATABASE WHERE ISACTIVE = 1 ORDER BY DISPLAYNAME",
            MapDb,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        snapshot.Links = await _registryReads.QueryAsync(@"
SELECT L.*, M.DISPLAYNAME AS MASTERNAME, S.DISPLAYNAME AS SUBNAME
FROM SYNC_DBLINK L
JOIN SYNC_DATABASE M ON M.DBID = L.MASTERDBID
JOIN SYNC_DATABASE S ON S.DBID = L.SUBDBID
WHERE L.ISACTIVE = 1
ORDER BY M.DISPLAYNAME, S.DISPLAYNAME", MapLink, cancellationToken: cancellationToken).ConfigureAwait(false);
        snapshot.Stats = await ReadStatsAsync(_registryReads, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static DatabaseLink MapLink(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        LinkId = CentralDb.Int(r, "LINKID"),
        MasterDbId = CentralDb.Int(r, "MASTERDBID"),
        SubDbId = CentralDb.Int(r, "SUBDBID"),
        IsActive = CentralDb.Flag(r, "ISACTIVE"),
        MasterName = CentralDb.Str(r, "MASTERNAME"),
        SubName = CentralDb.Str(r, "SUBNAME"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT")
    };

    private static async Task<DashboardStats> ReadStatsAsync(IRegistryReadGateway source, CancellationToken cancellationToken)
    {
        var stats = new DashboardStats();
        stats.TotalDatabases = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1", cancellationToken: cancellationToken) ?? 0);
        stats.Online = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS='ONLINE'", cancellationToken: cancellationToken) ?? 0);
        stats.Offline = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS IN ('OFFLINE','NETWORK_ERROR','TIMEOUT')", cancellationToken: cancellationToken) ?? 0);
        stats.Error = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_DATABASE WHERE ISACTIVE=1 AND CONNSTATUS NOT IN ('ONLINE','OFFLINE')", cancellationToken: cancellationToken) ?? 0);
        stats.Pending = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('PENDING','RETRYING')", cancellationToken: cancellationToken) ?? 0);
        stats.Processing = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS='PROCESSING'", cancellationToken: cancellationToken) ?? 0);
        stats.Failed = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS IN ('FAILED','CONFLICT','WAITING_FOR_MAPPING')", cancellationToken: cancellationToken) ?? 0);
        stats.CompletedToday = Convert.ToInt32(await source.ScalarAsync("SELECT COUNT(*) FROM SYNC_JOB WHERE STATUS='COMPLETED' AND CREATEDAT >= CURRENT_DATE", cancellationToken: cancellationToken) ?? 0);
        return stats;
    }

    private static object Args(RegisteredDatabase db, int id, string user) => new
    {
        DBID = id,
        DBCODE = db.DbCode.Trim().ToUpperInvariant(),
        DISPLAYNAME = db.DisplayName.Trim(),
        COMPANYNAME = db.CompanyName.Trim(),
        DBROLE = db.DbRole.Trim().ToUpperInvariant(),
        FBHOST = db.FbHost.Trim(),
        FBPORT = db.FbPort,
        FBPATH = db.FbPath.Trim(),
        FBCHARSET = string.IsNullOrWhiteSpace(db.FbCharset) ? "UTF8" : db.FbCharset,
        FBDIALECT = db.FbDialect <= 0 ? 3 : db.FbDialect,
        FBUSER = db.FbUser.Trim(),
        FBCREDREF = db.FbCredRef.Trim(),
        APIBASEURL = db.ApiBaseUrl.Trim(),
        APICREDREF = db.ApiCredRef.Trim(),
        ENVIRONMENT = db.Environment.Trim().ToUpperInvariant(),
        USER = user,
        NOTES = db.Notes
    };

    private static RegisteredDatabase MapDb(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        DbId = CentralDb.Int(r, "DBID"),
        DbCode = CentralDb.Str(r, "DBCODE"),
        DisplayName = CentralDb.Str(r, "DISPLAYNAME"),
        CompanyName = CentralDb.Str(r, "COMPANYNAME"),
        DbRole = CentralDb.Str(r, "DBROLE"),
        FbHost = CentralDb.Str(r, "FBHOST"),
        FbPort = CentralDb.Int(r, "FBPORT"),
        FbPath = CentralDb.Str(r, "FBPATH"),
        FbCharset = CentralDb.Str(r, "FBCHARSET"),
        FbDialect = CentralDb.Int(r, "FBDIALECT"),
        FbUser = CentralDb.Str(r, "FBUSER"),
        FbCredRef = CentralDb.Str(r, "FBCREDREF"),
        ApiBaseUrl = CentralDb.Str(r, "APIBASEURL"),
        ApiCredRef = CentralDb.Str(r, "APICREDREF"),
        Environment = CentralDb.Str(r, "ENVIRONMENT"),
        IsActive = CentralDb.Flag(r, "ISACTIVE"),
        ConnStatus = CentralDb.Str(r, "CONNSTATUS"),
        LastConnOk = CentralDb.Time(r, "LASTCONNOK"),
        LastSyncOk = CentralDb.Time(r, "LASTSYNCOK"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        UpdatedAt = CentralDb.TimeReq(r, "UPDATEDAT"),
        UpdatedBy = CentralDb.Str(r, "UPDATEDBY"),
        Notes = CentralDb.Str(r, "NOTES"),
    };
}
