using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.SqlAccount;

public interface ISqlAccountReadAdapter
{
    Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default);
    Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default);
    Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default);
    Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default);
    Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
}

public interface ISqlAccountWriteAdapter
{
    Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string entityCode, MasterRecord record, string txnStamp, CancellationToken cancellationToken = default);
    Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default);
}

/// <summary>Read-only Firebird adapter. All statements run in a Firebird read-only transaction.</summary>
public sealed class FirebirdReadAdapter : ISqlAccountReadAdapter
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdReadAdapter> _logger;

    public FirebirdReadAdapter(CompanyConnectionFactory factory, ILogger<FirebirdReadAdapter> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        QueryMaster(db, "SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION FROM ST_ITEM ORDER BY CODE", cancellationToken);

    public Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        QueryMaster(db, "SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE(COMPANYNAME, CODE, '')) AS DESCRIPTION FROM AR_CUSTOMER ORDER BY CODE", cancellationToken);

    public async Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["AP_SUPPLIER", "AP_VENDOR", "CREDITOR"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        var desc = await FirstColumn(db, table, ["COMPANYNAME", "DESCRIPTION", "NAME"], cancellationToken).ConfigureAwait(false) ?? "CODE";
        return await QueryMaster(db, $"SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION FROM {table} ORDER BY CODE", cancellationToken).ConfigureAwait(false);
    }

    public Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        QueryMaster(db, "SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION FROM ST_LOCATION ORDER BY CODE", cancellationToken);

    public async Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["TAX", "SY_TAX", "GL_TAX"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        var desc = await FirstColumn(db, table, ["DESCRIPTION", "NAME"], cancellationToken).ConfigureAwait(false) ?? "CODE";
        return await QueryMaster(db, $"SELECT FIRST 200 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION FROM {table} ORDER BY CODE", cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["GL_ACC", "GL_ACCOUNT", "ACCOUNT"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        var desc = await FirstColumn(db, table, ["DESCRIPTION", "NAME"], cancellationToken).ConfigureAwait(false) ?? "CODE";
        return await QueryMaster(db, $"SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION FROM {table} ORDER BY CODE", cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["ST_UOM", "UOM"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        var desc = await FirstColumn(db, table, ["DESCRIPTION", "NAME"], cancellationToken).ConfigureAwait(false) ?? "CODE";
        return await QueryMaster(db, $"SELECT FIRST 200 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION FROM {table} ORDER BY CODE", cancellationToken).ConfigureAwait(false);
    }

    public async Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default)
    {
        var list = entityCode.ToUpperInvariant() switch
        {
            EntityCodes.Item => await GetItemsAsync(db, cancellationToken),
            EntityCodes.Customer => await GetCustomersAsync(db, cancellationToken),
            EntityCodes.Supplier => await GetSuppliersAsync(db, cancellationToken),
            EntityCodes.Warehouse => await GetWarehousesAsync(db, cancellationToken),
            EntityCodes.TaxCode => await GetTaxCodesAsync(db, cancellationToken),
            EntityCodes.Account => await GetAccountsAsync(db, cancellationToken),
            EntityCodes.Uom => await GetUomsAsync(db, cancellationToken),
            _ => []
        };
        return list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default)
    {
        var (header, detail, typeCode) = ResolveDocTables(docType);
        if (header is null)
            return null;
        if (!await TableExists(db, header, cancellationToken).ConfigureAwait(false))
            return null;

        return await WithReadTx(db, async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
SELECT FIRST 1 DOCKEY, TRIM(DOCNO) AS DOCNO, DOCDATE,
       TRIM(COALESCE(CODE,'')) AS PARTYCODE,
       TRIM(COALESCE(COMPANYNAME, CODE, '')) AS PARTYNAME,
       COALESCE(DOCAMT, 0) AS AMOUNT
FROM {header}
WHERE CAST(DOCKEY AS VARCHAR(40)) = @K OR UPPER(TRIM(DOCNO)) = UPPER(@K)";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["K"] = docKeyOrNo });
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            var doc = new DocumentHeader
            {
                DocType = typeCode,
                DocKey = Convert.ToString(r["DOCKEY"]) ?? "",
                DocNo = Convert.ToString(r["DOCNO"])?.Trim() ?? "",
                DocDate = r["DOCDATE"] is DateTime dt ? dt : null,
                PartyCode = Convert.ToString(r["PARTYCODE"])?.Trim() ?? "",
                PartyName = Convert.ToString(r["PARTYNAME"])?.Trim() ?? "",
                Amount = Convert.ToDecimal(r["AMOUNT"])
            };

            if (detail is not null && await TableExists(db, detail, cancellationToken).ConfigureAwait(false))
            {
                await using var dcmd = conn.CreateCommand();
                dcmd.Transaction = tx;
                dcmd.CommandText = $@"
SELECT DTLKEY, COALESCE(SEQ,0) AS SEQ,
       TRIM(COALESCE(ITEMCODE,'')) AS ITEMCODE,
       TRIM(COALESCE(CAST(DESCRIPTION AS VARCHAR(800)),'')) AS DESCRIPTION,
       COALESCE(QTY,0) AS QTY,
       COALESCE(UNITPRICE,0) AS UNITPRICE,
       COALESCE(AMOUNT,0) AS AMOUNT
FROM {detail}
WHERE DOCKEY = @DOCKEY
ORDER BY COALESCE(SEQ,0)";
                SqlGuard.EnsureSelectOnly(dcmd.CommandText);
                CentralDb.Bind(dcmd, new Dictionary<string, object?> { ["DOCKEY"] = Convert.ToInt64(doc.DocKey) });
                await using var dr = await dcmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await dr.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    doc.Lines.Add(new DocumentLine
                    {
                        DtlKey = Convert.ToString(dr["DTLKEY"]) ?? "",
                        Seq = Convert.ToInt32(dr["SEQ"]),
                        ItemCode = Convert.ToString(dr["ITEMCODE"])?.Trim() ?? "",
                        Description = Convert.ToString(dr["DESCRIPTION"])?.Trim() ?? "",
                        Qty = Convert.ToDecimal(dr["QTY"]),
                        UnitPrice = Convert.ToDecimal(dr["UNITPRICE"]),
                        Amount = Convert.ToDecimal(dr["AMOUNT"])
                    });
                }
            }
            return doc;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default)
    {
        // Prefer a UDF column if present; discovery is used so we do not invent column names.
        var tables = new[] { "PH_IV", "PH_PI", "SL_IV", "PH_GR", "SL_DO" };
        return await WithReadTx(db, async (conn, tx) =>
        {
            foreach (var table in tables)
            {
                if (!await TableExistsConn(conn, table, cancellationToken).ConfigureAwait(false))
                    continue;
                var cols = await Columns(conn, table, cancellationToken).ConfigureAwait(false);
                var stampCol = cols.FirstOrDefault(c => c is "UDF_TXNID" or "UDF_SYNCID" or "DOCNOEX");
                if (stampCol is null)
                    continue;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT FIRST 1 DOCKEY, TRIM(DOCNO) AS DOCNO FROM {table} WHERE TRIM(CAST({stampCol} AS VARCHAR(80))) = @T";
                SqlGuard.EnsureSelectOnly(cmd.CommandText);
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["T"] = txnId });
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new ReconcileResult
                    {
                        DestinationExists = true,
                        DocKey = Convert.ToString(r["DOCKEY"]) ?? "",
                        DocNo = Convert.ToString(r["DOCNO"])?.Trim() ?? "",
                        Source = table + "." + stampCol
                    };
                }
            }
            return new ReconcileResult();
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) =>
        WithReadTx(db, async (conn, tx) =>
        {
            if (!await TableExistsConn(conn, "ST_XTRANS", cancellationToken).ConfigureAwait(false))
                return new ReconcileResult();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
SELECT FIRST 1 TODOCTYPE, TODOCKEY
FROM ST_XTRANS
WHERE FROMDOCTYPE = @T AND CAST(FROMDOCKEY AS VARCHAR(40)) = @K";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["T"] = fromDocType, ["K"] = fromDocKey });
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new ReconcileResult();
            return new ReconcileResult
            {
                DestinationExists = true,
                DocKey = Convert.ToString(r["TODOCKEY"]) ?? "",
                Source = "ST_XTRANS"
            };
        }, cancellationToken);

    public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        WithReadTx(db, async (conn, tx) =>
        {
            var report = new SchemaDiscoveryReport { DbId = db.DbId, AtUtc = DateTime.UtcNow };
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
SELECT TRIM(R.RDB$RELATION_NAME) AS TNAME
FROM RDB$RELATIONS R
WHERE R.RDB$VIEW_BLR IS NULL
  AND COALESCE(R.RDB$SYSTEM_FLAG,0) = 0
ORDER BY 1";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var names = new List<string>();
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                names.Add(Convert.ToString(r["TNAME"])?.Trim() ?? "");

            foreach (var name in names.Where(n => n.Length > 0))
            {
                var cols = await Columns(conn, name, cancellationToken).ConfigureAwait(false);
                report.Tables.Add(new DiscoveredTable { Name = name, Columns = cols });
            }

            void Hint(string entity, params string[] candidates)
            {
                var hit = candidates.FirstOrDefault(c => names.Contains(c, StringComparer.OrdinalIgnoreCase));
                if (hit is not null)
                    report.EntityTableHints[entity] = hit;
            }

            Hint(EntityCodes.Item, "ST_ITEM");
            Hint(EntityCodes.Customer, "AR_CUSTOMER");
            Hint(EntityCodes.Warehouse, "ST_LOCATION");
            Hint(EntityCodes.Supplier, "AP_SUPPLIER", "AP_VENDOR", "CREDITOR");
            Hint(EntityCodes.TaxCode, "TAX", "SY_TAX", "GL_TAX");
            Hint(EntityCodes.Account, "GL_ACC", "GL_ACCOUNT", "ACCOUNT");
            Hint(EntityCodes.Uom, "ST_UOM", "UOM");
            Hint("SALES_INVOICE", "SL_IV");
            Hint("PURCHASE_INVOICE", "PH_IV", "PH_PI", "PH_BILL");
            Hint("GOODS_RECEIVED", "PH_GR");
            Hint("DELIVERY_ORDER", "SL_DO");
            Hint("DOC_TRANSFER", "ST_XTRANS");
            return report;
        }, cancellationToken);

    private async Task<List<MasterRecord>> QueryMaster(RegisteredDatabase db, string sql, CancellationToken cancellationToken)
    {
        SqlGuard.EnsureSelectOnly(sql);
        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var list = new List<MasterRecord>();
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    list.Add(new MasterRecord
                    {
                        Code = Convert.ToString(r["CODE"])?.Trim() ?? "",
                        Description = Convert.ToString(r["DESCRIPTION"])?.Trim() ?? "",
                        Key = Convert.ToString(r["CODE"])?.Trim() ?? ""
                    });
                }
                return list;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read adapter query failed for {Db}", db.DbCode);
            return [];
        }
    }

    private async Task<T> WithReadTx<T>(RegisteredDatabase db, Func<FbConnection, FbTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        var options = new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read
                                | FbTransactionBehavior.ReadCommitted
                                | FbTransactionBehavior.RecVersion
                                | FbTransactionBehavior.NoWait
        };
        await using var tx = await conn.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await work(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); } catch { /* ignore */ }
            throw;
        }
    }

    private async Task<string?> FirstExisting(RegisteredDatabase db, IEnumerable<string> names, CancellationToken ct)
    {
        foreach (var n in names)
        {
            if (await TableExists(db, n, ct).ConfigureAwait(false))
                return n;
        }
        return null;
    }

    private Task<bool> TableExists(RegisteredDatabase db, string table, CancellationToken ct) =>
        WithReadTx(db, (conn, _) => TableExistsConn(conn, table, ct), ct);

    private static async Task<bool> TableExistsConn(FbConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME)=@N";
        SqlGuard.EnsureSelectOnly(cmd.CommandText);
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private async Task<string?> FirstColumn(RegisteredDatabase db, string table, IEnumerable<string> names, CancellationToken ct)
    {
        var cols = await WithReadTx(db, (conn, _) => Columns(conn, table, ct), ct).ConfigureAwait(false);
        return names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<List<string>> Columns(FbConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TRIM(RDB$FIELD_NAME) AS C FROM RDB$RELATION_FIELDS WHERE TRIM(RDB$RELATION_NAME)=@N";
        SqlGuard.EnsureSelectOnly(cmd.CommandText);
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Convert.ToString(r["C"])?.Trim() ?? "");
        return list;
    }

    private static (string? Header, string? Detail, string Type) ResolveDocTables(string docType) =>
        docType.Trim().ToUpperInvariant() switch
        {
            "IV" or "SI" or "SALESINVOICE" => ("SL_IV", "SL_IVDTL", "IV"),
            "DO" or "DELIVERYORDER" => ("SL_DO", "SL_DODTL", "DO"),
            "SO" or "SALESORDER" => ("SL_SO", "SL_SODTL", "SO"),
            "PO" or "PURCHASEORDER" => ("PH_PO", "PH_PODTL", "PO"),
            "GR" or "GOODSRECEIVED" => ("PH_GR", "PH_GRDTL", "GR"),
            "DN" => ("SL_DN", "SL_DNDTL", "DN"),
            "PI" or "PURCHASEINVOICE" => ("PH_IV", "PH_IVDTL", "PI"),
            _ => (null, null, docType)
        };
}
