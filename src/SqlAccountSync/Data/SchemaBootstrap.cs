using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Authorization;
using SqlAccountSync.Options;

namespace SqlAccountSync.Data;

public sealed class SchemaBootstrap
{
    public const int CurrentVersion = 3;

    private readonly CentralDb _db;
    private readonly BootstrapOptions _bootstrap;
    private readonly ILogger<SchemaBootstrap> _logger;

    public SchemaBootstrap(CentralDb db, IOptions<BootstrapOptions> bootstrap, ILogger<SchemaBootstrap> logger)
    {
        _db = db;
        _bootstrap = bootstrap.Value;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
        {
            _logger.LogWarning("Central Firebird path is not configured. Schema bootstrap skipped.");
            return;
        }

        await _db.EnsureDatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureGenerator(conn, "GEN_SYNC_DATABASE", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_DBLINK", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_RULE", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_SCOPE", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_MAPPING", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_TXN", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_JOB", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_EVENT", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_AUDIT", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_ERROR", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_USER", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_ACL", cancellationToken);
        await EnsureGenerator(conn, "GEN_SYNC_TXNSEQ", cancellationToken);

        await EnsureTable(conn, "SYNC_SCHEMA_VER", @"
CREATE TABLE SYNC_SCHEMA_VER (
    VERID INTEGER NOT NULL PRIMARY KEY,
    APPLIEDAT TIMESTAMP NOT NULL
)", cancellationToken);

        await EnsureTable(conn, "SYNC_USER", @"
CREATE TABLE SYNC_USER (
    USERID INTEGER NOT NULL PRIMARY KEY,
    EMAIL VARCHAR(160) NOT NULL,
    DISPLAYNAME VARCHAR(120) NOT NULL,
    USERROLE VARCHAR(20) NOT NULL,
    ISACTIVE SMALLINT DEFAULT 1 NOT NULL,
    CREATEDAT TIMESTAMP NOT NULL,
    CONSTRAINT UQ_SYNC_USER_EMAIL UNIQUE (EMAIL)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_DATABASE", @"
CREATE TABLE SYNC_DATABASE (
    DBID INTEGER NOT NULL PRIMARY KEY,
    DBCODE VARCHAR(40) NOT NULL,
    DISPLAYNAME VARCHAR(120) NOT NULL,
    COMPANYNAME VARCHAR(120),
    DBROLE VARCHAR(10) NOT NULL,
    FBHOST VARCHAR(120) NOT NULL,
    FBPORT INTEGER DEFAULT 3050 NOT NULL,
    FBPATH VARCHAR(500) NOT NULL,
    FBCHARSET VARCHAR(20) DEFAULT 'UTF8' NOT NULL,
    FBDIALECT INTEGER DEFAULT 3 NOT NULL,
    FBUSER VARCHAR(80) DEFAULT 'SYSDBA' NOT NULL,
    FBCREDREF VARCHAR(200) NOT NULL,
    APIBASEURL VARCHAR(300),
    APICREDREF VARCHAR(200),
    ENVIRONMENT VARCHAR(20) DEFAULT 'DEVELOPMENT' NOT NULL,
    ISACTIVE SMALLINT DEFAULT 1 NOT NULL,
    CONNSTATUS VARCHAR(40) DEFAULT 'OFFLINE' NOT NULL,
    LASTCONNOK TIMESTAMP,
    LASTSYNCOK TIMESTAMP,
    CREATEDAT TIMESTAMP NOT NULL,
    CREATEDBY VARCHAR(120),
    UPDATEDAT TIMESTAMP NOT NULL,
    UPDATEDBY VARCHAR(120),
    NOTES BLOB SUB_TYPE TEXT,
    CONSTRAINT UQ_SYNC_DB_CODE UNIQUE (DBCODE)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_DBLINK", @"
CREATE TABLE SYNC_DBLINK (
    LINKID INTEGER NOT NULL PRIMARY KEY,
    MASTERDBID INTEGER NOT NULL,
    SUBDBID INTEGER NOT NULL,
    ISACTIVE SMALLINT DEFAULT 1 NOT NULL,
    CREATEDAT TIMESTAMP NOT NULL,
    CREATEDBY VARCHAR(120),
    CONSTRAINT UQ_SYNC_DBLINK UNIQUE (MASTERDBID, SUBDBID),
    CONSTRAINT FK_SYNC_LINK_M FOREIGN KEY (MASTERDBID) REFERENCES SYNC_DATABASE (DBID),
    CONSTRAINT FK_SYNC_LINK_S FOREIGN KEY (SUBDBID) REFERENCES SYNC_DATABASE (DBID)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_ENTITYTYPE", @"
CREATE TABLE SYNC_ENTITYTYPE (
    ENTITYCODE VARCHAR(20) NOT NULL PRIMARY KEY,
    DISPLAYNAME VARCHAR(80) NOT NULL,
    DIRECTIONS VARCHAR(40) DEFAULT 'MASTER_TO_SUB' NOT NULL,
    ISACTIVE SMALLINT DEFAULT 1 NOT NULL
)", cancellationToken);

        await EnsureTable(conn, "SYNC_RULE", @"
CREATE TABLE SYNC_RULE (
    RULEID INTEGER NOT NULL PRIMARY KEY,
    LINKID INTEGER NOT NULL,
    ENTITYCODE VARCHAR(20) NOT NULL,
    ENABLED SMALLINT DEFAULT 0 NOT NULL,
    DIRECTION VARCHAR(20) DEFAULT 'MASTER_TO_SUB' NOT NULL,
    MODE VARCHAR(20) DEFAULT 'MANUAL' NOT NULL,
    CONFLICTSTRATEGY VARCHAR(20) DEFAULT 'REQUIRE_ADMIN' NOT NULL,
    SCHEDULECRON VARCHAR(80),
    LASTSYNCAT TIMESTAMP,
    CONSTRAINT UQ_SYNC_RULE UNIQUE (LINKID, ENTITYCODE),
    CONSTRAINT FK_SYNC_RULE_L FOREIGN KEY (LINKID) REFERENCES SYNC_DBLINK (LINKID),
    CONSTRAINT FK_SYNC_RULE_E FOREIGN KEY (ENTITYCODE) REFERENCES SYNC_ENTITYTYPE (ENTITYCODE)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_RULE_SCOPE", @"
CREATE TABLE SYNC_RULE_SCOPE (
    SCOPEID INTEGER NOT NULL PRIMARY KEY,
    RULEID INTEGER NOT NULL,
    RECORDCODE VARCHAR(80) NOT NULL,
    ENABLED SMALLINT DEFAULT 1 NOT NULL,
    CONSTRAINT UQ_SYNC_SCOPE UNIQUE (RULEID, RECORDCODE),
    CONSTRAINT FK_SYNC_SCOPE_R FOREIGN KEY (RULEID) REFERENCES SYNC_RULE (RULEID)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_MAPPING", @"
CREATE TABLE SYNC_MAPPING (
    MAPPINGID INTEGER NOT NULL PRIMARY KEY,
    MASTERDBID INTEGER NOT NULL,
    SUBDBID INTEGER NOT NULL,
    ENTITYCODE VARCHAR(20) NOT NULL,
    SRCENTITYCODE VARCHAR(20) NOT NULL,
    DSTENTITYCODE VARCHAR(20) NOT NULL,
    MASTERCODE VARCHAR(80) NOT NULL,
    MASTERKEY VARCHAR(80),
    SUBCODE VARCHAR(80) NOT NULL,
    SUBKEY VARCHAR(80),
    MASTERDESC VARCHAR(200),
    SUBDESC VARCHAR(200),
    STATUS VARCHAR(20) DEFAULT 'MAPPED' NOT NULL,
    ORIGINJOBID INTEGER,
    LASTSYNCAT TIMESTAMP,
    CREATEDAT TIMESTAMP NOT NULL,
    CREATEDBY VARCHAR(120),
    UPDATEDAT TIMESTAMP NOT NULL,
    UPDATEDBY VARCHAR(120),
    CONSTRAINT UQ_SYNC_MAP_SRC UNIQUE (MASTERDBID, SUBDBID, SRCENTITYCODE, DSTENTITYCODE, MASTERCODE),
    CONSTRAINT UQ_SYNC_MAP_DST UNIQUE (MASTERDBID, SUBDBID, DSTENTITYCODE, SUBCODE),
    CONSTRAINT FK_SYNC_MAP_M FOREIGN KEY (MASTERDBID) REFERENCES SYNC_DATABASE (DBID),
    CONSTRAINT FK_SYNC_MAP_S FOREIGN KEY (SUBDBID) REFERENCES SYNC_DATABASE (DBID)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_TXNTYPE", @"
CREATE TABLE SYNC_TXNTYPE (
    TXNTYPECODE VARCHAR(20) NOT NULL PRIMARY KEY,
    DISPLAYNAME VARCHAR(80) NOT NULL,
    SRCDOCTYPE VARCHAR(20) NOT NULL,
    DSTDOCTYPE VARCHAR(20) NOT NULL,
    SRCAPIPATH VARCHAR(80),
    DSTAPIPATH VARCHAR(80),
    ISACTIVE SMALLINT DEFAULT 1 NOT NULL
)", cancellationToken);

        await EnsureTable(conn, "SYNC_TXN", @"
CREATE TABLE SYNC_TXN (
    TXNPK INTEGER NOT NULL PRIMARY KEY,
    TXNID VARCHAR(40) NOT NULL,
    TXNTYPECODE VARCHAR(20) NOT NULL,
    SOURCEDBID INTEGER NOT NULL,
    DESTDBID INTEGER NOT NULL,
    SRCDOCTYPE VARCHAR(20) NOT NULL,
    SRCDOCKEY VARCHAR(40),
    SRCDOCNO VARCHAR(40) NOT NULL,
    DSTDOCTYPE VARCHAR(20) NOT NULL,
    DSTDOCKEY VARCHAR(40),
    DSTDOCNO VARCHAR(40),
    IDEMKEY VARCHAR(64) NOT NULL,
    STATUS VARCHAR(30) DEFAULT 'PENDING' NOT NULL,
    ERRORMESSAGE BLOB SUB_TYPE TEXT,
    RETRYCOUNT INTEGER DEFAULT 0 NOT NULL,
    CREATEDAT TIMESTAMP NOT NULL,
    STARTEDAT TIMESTAMP,
    COMPLETEDAT TIMESTAMP,
    CREATEDBY VARCHAR(120),
    UPDATEDAT TIMESTAMP NOT NULL,
    CONSTRAINT UQ_SYNC_TXN_ID UNIQUE (TXNID),
    CONSTRAINT UQ_SYNC_TXN_IDEM UNIQUE (IDEMKEY)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_JOB", @"
CREATE TABLE SYNC_JOB (
    JOBID INTEGER NOT NULL PRIMARY KEY,
    JOBKIND VARCHAR(30) NOT NULL,
    TXNPK INTEGER,
    SOURCEDBID INTEGER NOT NULL,
    DESTDBID INTEGER NOT NULL,
    ENTITYCODE VARCHAR(20),
    PAYLOADJSON BLOB SUB_TYPE TEXT,
    STATUS VARCHAR(30) DEFAULT 'PENDING' NOT NULL,
    PRIORITY INTEGER DEFAULT 0 NOT NULL,
    ATTEMPTS INTEGER DEFAULT 0 NOT NULL,
    MAXATTEMPTS INTEGER DEFAULT 5 NOT NULL,
    NEXTRUNAT TIMESTAMP NOT NULL,
    LOCKEDBY VARCHAR(80),
    LOCKEDAT TIMESTAMP,
    DRYRUN SMALLINT DEFAULT 0 NOT NULL,
    IDEMKEY VARCHAR(64) NOT NULL,
    ERRORMESSAGE BLOB SUB_TYPE TEXT,
    CREATEDAT TIMESTAMP NOT NULL,
    CREATEDBY VARCHAR(120),
    CONSTRAINT UQ_SYNC_JOB_IDEM UNIQUE (IDEMKEY)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_TXN_EVENT", @"
CREATE TABLE SYNC_TXN_EVENT (
    EVENTID INTEGER NOT NULL PRIMARY KEY,
    TXNPK INTEGER NOT NULL,
    ATUTC TIMESTAMP NOT NULL,
    STEPNAME VARCHAR(80) NOT NULL,
    RESULT VARCHAR(20) NOT NULL,
    DETAIL BLOB SUB_TYPE TEXT,
    CONSTRAINT FK_SYNC_EVT_T FOREIGN KEY (TXNPK) REFERENCES SYNC_TXN (TXNPK)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_AUDIT", @"
CREATE TABLE SYNC_AUDIT (
    AUDITID INTEGER NOT NULL PRIMARY KEY,
    ATUTC TIMESTAMP NOT NULL,
    USEREMAIL VARCHAR(160),
    ACTION VARCHAR(80) NOT NULL,
    DBID INTEGER,
    ENTITY VARCHAR(40),
    RECORDID VARCHAR(80),
    RESULT VARCHAR(20) NOT NULL,
    IPADDR VARCHAR(80),
    SESSIONID VARCHAR(80),
    REQUESTID VARCHAR(80),
    BEFOREJSON BLOB SUB_TYPE TEXT,
    AFTERJSON BLOB SUB_TYPE TEXT
)", cancellationToken);

        await EnsureTable(conn, "SYNC_ERROR", @"
CREATE TABLE SYNC_ERROR (
    ERRORID INTEGER NOT NULL PRIMARY KEY,
    ATUTC TIMESTAMP NOT NULL,
    TXNPK INTEGER,
    DBID INTEGER,
    ERRORTYPE VARCHAR(40) NOT NULL,
    USERMESSAGE VARCHAR(500) NOT NULL,
    STACKTRACE BLOB SUB_TYPE TEXT,
    RETRYCOUNT INTEGER DEFAULT 0 NOT NULL,
    RESOLVED SMALLINT DEFAULT 0 NOT NULL
)", cancellationToken);

        await EnsureTable(conn, "SYNC_USER_DBACL", @"
CREATE TABLE SYNC_USER_DBACL (
    ACLID INTEGER NOT NULL PRIMARY KEY,
    USERID INTEGER NOT NULL,
    DBID INTEGER NOT NULL,
    CANVIEW SMALLINT DEFAULT 1 NOT NULL,
    CANSYNC SMALLINT DEFAULT 0 NOT NULL,
    CONSTRAINT UQ_SYNC_ACL UNIQUE (USERID, DBID),
    CONSTRAINT FK_SYNC_ACL_U FOREIGN KEY (USERID) REFERENCES SYNC_USER (USERID),
    CONSTRAINT FK_SYNC_ACL_D FOREIGN KEY (DBID) REFERENCES SYNC_DATABASE (DBID)
)", cancellationToken);

        await EnsureTable(conn, "SYNC_SETTING", @"
CREATE TABLE SYNC_SETTING (
    SETKEY VARCHAR(80) NOT NULL PRIMARY KEY,
    SETVALUE VARCHAR(500),
    UPDATEDAT TIMESTAMP NOT NULL
)", cancellationToken);

        await SeedAsync(conn, cancellationToken);

        var ver = await ScalarInt(conn, "SELECT COALESCE(MAX(VERID), 0) FROM SYNC_SCHEMA_VER", cancellationToken);
        if (ver < 2)
            await MigrateToV2Async(conn, cancellationToken);
        if (ver < 3)
            await MigrateToV3Async(conn, cancellationToken);
        if (ver < CurrentVersion)
        {
            await Exec(conn, "INSERT INTO SYNC_SCHEMA_VER (VERID, APPLIEDAT) VALUES (@V, CURRENT_TIMESTAMP)",
                new Dictionary<string, object?> { ["V"] = CurrentVersion }, cancellationToken);
        }
    }

    private static async Task MigrateToV2Async(FbConnection conn, CancellationToken ct)
    {
        await EnsureColumn(conn, "SYNC_MAPPING", "SRCENTITYCODE", "VARCHAR(20)", ct);
        await EnsureColumn(conn, "SYNC_MAPPING", "DSTENTITYCODE", "VARCHAR(20)", ct);
        await EnsureColumn(conn, "SYNC_MAPPING", "ORIGINJOBID", "INTEGER", ct);
        await EnsureColumn(conn, "SYNC_MAPPING", "LASTSYNCAT", "TIMESTAMP", ct);
        await Exec(conn, "UPDATE SYNC_MAPPING SET SRCENTITYCODE = ENTITYCODE WHERE SRCENTITYCODE IS NULL", new Dictionary<string, object?>(), ct);
        await Exec(conn, "UPDATE SYNC_MAPPING SET DSTENTITYCODE = ENTITYCODE WHERE DSTENTITYCODE IS NULL", new Dictionary<string, object?>(), ct);
        await DropConstraintIfExists(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_MASTER", ct);
        await DropConstraintIfExists(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_SUB", ct);
        await EnsureConstraint(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_SRC",
            "UNIQUE (MASTERDBID, SUBDBID, SRCENTITYCODE, MASTERCODE)", ct);
        await EnsureConstraint(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_DST",
            "UNIQUE (MASTERDBID, SUBDBID, DSTENTITYCODE, SUBCODE)", ct);
    }

    /// <summary>
    /// Allow the same source customer to map to both a dest customer (CN↔DN)
    /// and a dest supplier (SI→PI / CN→PR) on the same DB link.
    /// </summary>
    private static async Task MigrateToV3Async(FbConnection conn, CancellationToken ct)
    {
        await DropConstraintIfExists(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_SRC", ct);
        await EnsureConstraint(conn, "SYNC_MAPPING", "UQ_SYNC_MAP_SRC",
            "UNIQUE (MASTERDBID, SUBDBID, SRCENTITYCODE, DSTENTITYCODE, MASTERCODE)", ct);
    }

    private static async Task EnsureColumn(FbConnection conn, string table, string column, string ddlType, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(*) FROM RDB$RELATION_FIELDS
WHERE TRIM(RDB$RELATION_NAME)=@T AND TRIM(RDB$FIELD_NAME)=@C";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["T"] = table, ["C"] = column });
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (n > 0)
            return;
        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD {column} {ddlType}";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DropConstraintIfExists(FbConnection conn, string table, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATION_CONSTRAINTS WHERE TRIM(RDB$CONSTRAINT_NAME)=@N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = name });
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (n == 0)
            return;
        await using var drop = conn.CreateCommand();
        drop.CommandText = $"ALTER TABLE {table} DROP CONSTRAINT {name}";
        await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureConstraint(FbConnection conn, string table, string name, string ddl, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATION_CONSTRAINTS WHERE TRIM(RDB$CONSTRAINT_NAME)=@N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = name });
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (n > 0)
            return;
        await using var add = conn.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD CONSTRAINT {name} {ddl}";
        await add.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedAsync(FbConnection conn, CancellationToken ct)
    {
        var entities = new (string Code, string Name)[]
        {
            ("ITEM", "Items"),
            ("CUSTOMER", "Customers"),
            ("SUPPLIER", "Suppliers"),
            ("ACCOUNT", "Accounts"),
            ("TAXCODE", "Tax Codes"),
            ("WAREHOUSE", "Warehouses"),
            ("UOM", "Units of Measure"),
            ("ITEMCATEGORY", "Item Categories"),
            ("PRICING", "Pricing"),
        };
        foreach (var (code, name) in entities)
        {
            await Exec(conn, @"
UPDATE OR INSERT INTO SYNC_ENTITYTYPE (ENTITYCODE, DISPLAYNAME, DIRECTIONS, ISACTIVE)
VALUES (@C, @N, 'MASTER_TO_SUB', 1)
MATCHING (ENTITYCODE)", new Dictionary<string, object?> { ["C"] = code, ["N"] = name }, ct);
        }

        var types = new (string Code, string Name, string Src, string Dst, string SrcPath, string DstPath)[]
        {
            ("SI_TO_PI", "Sales → Purchase", "IV", "PI", "/customerinvoice", "/purchaseinvoice"),
            ("DO_TO_GR", "Delivery → Goods received", "DO", "GR", "/deliveryorder", "/goodsreceived"),
            ("ST_TO_ST", "Stock transfer", "ST", "ST", "/stocktransfer", "/stocktransfer"),
            ("CN_TO_PR", "Credit note → Purchase return", "CN", "PR", "/salescreditnote", "/purchasereturned"),
            ("CN_TO_DN", "Credit note → Debit note", "CN", "DN", "/salescreditnote", "/salesdebitnote"),
            ("DN_TO_CN", "Debit note → Credit note", "DN", "CN", "/salesdebitnote", "/salescreditnote"),
        };
        foreach (var t in types)
        {
            await Exec(conn, @"
UPDATE OR INSERT INTO SYNC_TXNTYPE (TXNTYPECODE, DISPLAYNAME, SRCDOCTYPE, DSTDOCTYPE, SRCAPIPATH, DSTAPIPATH, ISACTIVE)
VALUES (@C, @N, @S, @D, @SP, @DP, 1)
MATCHING (TXNTYPECODE)",
                new Dictionary<string, object?> { ["C"] = t.Code, ["N"] = t.Name, ["S"] = t.Src, ["D"] = t.Dst, ["SP"] = t.SrcPath, ["DP"] = t.DstPath }, ct);
        }

        // Legacy stub: no Sales Return create API — hide from transfer UI.
        await Exec(conn, @"
UPDATE SYNC_TXNTYPE SET ISACTIVE = 0, DISPLAYNAME = 'Sales return → Purchase return (retired)'
WHERE TXNTYPECODE = 'SR_TO_PR'", new Dictionary<string, object?>(), ct);

        var email = (_bootstrap.AdminEmail ?? "admin@localhost").Trim().ToLowerInvariant();
        var exists = await ScalarInt(conn, "SELECT COUNT(*) FROM SYNC_USER WHERE UPPER(EMAIL) = UPPER(@E)",
            ct, new Dictionary<string, object?> { ["E"] = email });
        if (exists == 0)
        {
            var id = await NextGen(conn, "GEN_SYNC_USER", ct);
            await Exec(conn, @"
INSERT INTO SYNC_USER (USERID, EMAIL, DISPLAYNAME, USERROLE, ISACTIVE, CREATEDAT)
VALUES (@I, @E, @N, @R, 1, CURRENT_TIMESTAMP)",
                new Dictionary<string, object?>
                {
                    ["I"] = id,
                    ["E"] = email,
                    ["N"] = _bootstrap.AdminName,
                    ["R"] = AppRoles.Admin
                }, ct);
        }
    }

    private static async Task EnsureGenerator(FbConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$GENERATORS WHERE TRIM(RDB$GENERATOR_NAME) = @N";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = name });
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (n == 0)
        {
            await using var c2 = conn.CreateCommand();
            c2.CommandText = $"CREATE GENERATOR {name}";
            await c2.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureTable(FbConnection conn, string table, string ddl, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME) = @N AND RDB$VIEW_BLR IS NULL";
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (n == 0)
        {
            await using var c2 = conn.CreateCommand();
            c2.CommandText = ddl;
            await c2.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task Exec(FbConnection conn, string sql, IDictionary<string, object?> args, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ScalarInt(FbConnection conn, string sql, CancellationToken ct, IDictionary<string, object?>? args = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (args is not null)
            CentralDb.Bind(cmd, args);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    private static async Task<long> NextGen(FbConnection conn, string gen, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT GEN_ID({gen}, 1) FROM RDB$DATABASE";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
