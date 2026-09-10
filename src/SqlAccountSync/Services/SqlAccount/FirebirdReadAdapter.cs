using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount;

public interface ISqlAccountReadAdapter
{
    Task<List<MasterRecord>> GetItemsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetCustomersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetWarehousesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetTaxCodesAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetAccountsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    /// <summary>Full Maintain Account chart (GL_ACC) with dockey/parent for tree display.</summary>
    Task<List<Services.Accounts.GlAccountRow>> GetGlChartAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    /// <summary>Active credit terms from TERMS (code used as AR/AP creditterm).</summary>
    Task<List<MasterRecord>> GetTermsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
    Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default);
    Task<MasterRecord?> GetPartyDetailAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default);
    Task<MasterRecord?> GetItemDetailAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default);
    Task<bool> PartyExistsByStampAsync(RegisteredDatabase db, string entityCode, string txnStamp, CancellationToken cancellationToken = default);
    Task<bool> MasterExistsByCodeAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default);
    Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default);
    Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default);
    Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default);
    Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default);
    Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default);
    Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
}

public interface ISqlAccountWriteAdapter
{
    Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default);
    Task<WriteResult> UpdateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default);
    Task<WriteResult> DeleteMasterAsync(RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default);
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
        QueryPartyList(db, EntityCodes.Customer, cancellationToken);

    public async Task<List<MasterRecord>> GetSuppliersAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["AP_SUPPLIER", "AP_VENDOR", "CREDITOR"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        return await QueryPartyList(db, EntityCodes.Supplier, cancellationToken, table).ConfigureAwait(false);
    }

    public Task<MasterRecord?> GetPartyDetailAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default) =>
        QueryPartyDetail(db, entityCode, code, cancellationToken);

    public Task<MasterRecord?> GetItemDetailAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default) =>
        QueryItemDetail(db, code, cancellationToken);

    public Task<bool> PartyExistsByStampAsync(RegisteredDatabase db, string entityCode, string txnStamp, CancellationToken cancellationToken = default) =>
        WithReadTxOrFallback(db, async (conn, tx) =>
        {
            var table = await ResolvePartyTable(db, entityCode, conn, tx, cancellationToken).ConfigureAwait(false);
            if (table is null)
                return false;
            var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
            var stamp = (txnStamp ?? "").Trim();
            if (stamp.Length == 0)
                return false;

            // Prefer UDF stamp column; fall back to remark containing SYNC|{stamp}.
            var stampCol = cols.FirstOrDefault(c => c.Equals("UDF_TXNID", StringComparison.OrdinalIgnoreCase));
            if (stampCol is not null)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE TRIM(CAST({stampCol} AS VARCHAR(80))) = @T";
                SqlGuard.EnsureSelectOnly(cmd.CommandText);
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["T"] = stamp });
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
                    return true;
            }

            if (cols.Any(c => c.Equals("REMARK", StringComparison.OrdinalIgnoreCase)))
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE UPPER(TRIM(CAST(REMARK AS VARCHAR(500)))) CONTAINING @T";
                SqlGuard.EnsureSelectOnly(cmd.CommandText);
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["T"] = ("SYNC|" + stamp).ToUpperInvariant() });
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }

            return false;
        }, fallback: false, cancellationToken);

    public Task<bool> MasterExistsByCodeAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Task.FromResult(false);

        if (string.Equals(entityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
        {
            return WithReadTxOrFallback(db, async (conn, tx) =>
            {
                if (!await TableExistsConn(conn, tx, "ST_ITEM", cancellationToken).ConfigureAwait(false))
                    return false;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM ST_ITEM WHERE UPPER(TRIM(CODE))=UPPER(@C)";
                SqlGuard.EnsureSelectOnly(cmd.CommandText);
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }, fallback: false, cancellationToken);
        }

        if (string.Equals(entityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            return WithReadTxOrFallback(db, async (conn, tx) =>
            {
                var table = await FirstExistingConn(conn, tx, ["GL_ACC", "GL_ACCOUNT", "ACCOUNT"], cancellationToken)
                    .ConfigureAwait(false);
                if (table is null)
                    return false;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
                SqlGuard.EnsureSelectOnly(cmd.CommandText);
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }, fallback: false, cancellationToken);
        }

        if (!PartnerSyncRules.IsPartyEntity(entityCode))
            return Task.FromResult(false);

        return WithReadTxOrFallback(db, async (conn, tx) =>
        {
            var table = await ResolvePartyTable(db, entityCode, conn, tx, cancellationToken).ConfigureAwait(false);
            if (table is null)
                return false;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
        }, fallback: false, cancellationToken);
    }

    public Task<StockGroupRecord?> GetStockGroupAsync(RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Task.FromResult<StockGroupRecord?>(null);

        return WithReadTxOrFallback(db, async (conn, tx) =>
        {
            if (!await TableExistsConn(conn, tx, "ST_GROUP", cancellationToken).ConfigureAwait(false))
                return null;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
SELECT FIRST 1 TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION, CODE)) AS DESCRIPTION,
       TRIM(COALESCE(SALES, '')) AS SALES, TRIM(COALESCE(SALESRETURNED, '')) AS SALESRETURNED,
       TRIM(COALESCE(CASHSALES, '')) AS CASHSALES, TRIM(COALESCE(PURCHASE, '')) AS PURCHASE,
       TRIM(COALESCE(CASHPURCHASE, '')) AS CASHPURCHASE, TRIM(COALESCE(PURCHASERETURNED, '')) AS PURCHASERETURNED,
       TRIM(COALESCE(BALANCESTOCK, '')) AS BALANCESTOCK, COALESCE(COSTINGMETHOD, 1) AS COSTINGMETHOD,
       COALESCE(ISACTIVE, 1) AS ISACTIVE
FROM ST_GROUP WHERE UPPER(TRIM(CODE))=UPPER(@C)";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            return new StockGroupRecord
            {
                Code = Convert.ToString(r["CODE"])?.Trim() ?? key,
                Description = Convert.ToString(r["DESCRIPTION"])?.Trim() ?? key,
                Sales = Convert.ToString(r["SALES"])?.Trim() ?? "",
                SalesReturned = Convert.ToString(r["SALESRETURNED"])?.Trim() ?? "",
                CashSales = Convert.ToString(r["CASHSALES"])?.Trim() ?? "",
                Purchase = Convert.ToString(r["PURCHASE"])?.Trim() ?? "",
                CashPurchase = Convert.ToString(r["CASHPURCHASE"])?.Trim() ?? "",
                PurchaseReturned = Convert.ToString(r["PURCHASERETURNED"])?.Trim() ?? "",
                BalanceStock = Convert.ToString(r["BALANCESTOCK"])?.Trim() ?? "",
                CostingMethod = Convert.ToInt32(r["COSTINGMETHOD"]),
                IsActive = Convert.ToInt32(r["ISACTIVE"]) != 0
            };
        }, fallback: null, cancellationToken);
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
        var chart = await GetGlChartAsync(db, cancellationToken).ConfigureAwait(false);
        return chart.Select(a => new MasterRecord
        {
            Code = a.Code,
            Description = a.Description,
            Key = a.Code.Length > 0 ? a.Code : a.DocKey.ToString()
        }).ToList();
    }

    public async Task<List<Services.Accounts.GlAccountRow>> GetGlChartAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["GL_ACC", "GL_ACCOUNT", "ACCOUNT"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];

        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
                bool Has(string name) => cols.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!Has("DOCKEY") || !Has("CODE"))
                    return [];

                var desc = Has("DESCRIPTION") ? "DESCRIPTION" : (Has("NAME") ? "NAME" : "CODE");
                var sql = $@"
SELECT DOCKEY,
       {(Has("PARENT") ? "PARENT" : "0 AS PARENT")},
       TRIM(CODE) AS CODE,
       TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION,
       {(Has("DESCRIPTION2") ? "DESCRIPTION2" : "CAST(NULL AS VARCHAR(1)) AS DESCRIPTION2")},
       {(Has("ACCTYPE") ? "ACCTYPE" : "CAST(NULL AS VARCHAR(1)) AS ACCTYPE")},
       {(Has("SPECIALACCTYPE") ? "SPECIALACCTYPE" : "CAST(NULL AS VARCHAR(1)) AS SPECIALACCTYPE")},
       {(Has("TAX") ? "TAX" : "CAST(NULL AS VARCHAR(1)) AS TAX")},
       {(Has("CASHFLOWTYPE") ? "CASHFLOWTYPE" : "0 AS CASHFLOWTYPE")},
       {(Has("SIC") ? "SIC" : "CAST(NULL AS VARCHAR(1)) AS SIC")}
FROM {table}
ORDER BY CODE";
                SqlGuard.EnsureSelectOnly(sql);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var list = new List<Services.Accounts.GlAccountRow>();
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    list.Add(new Services.Accounts.GlAccountRow
                    {
                        DocKey = r["DOCKEY"] is DBNull ? 0 : Convert.ToInt32(r["DOCKEY"]),
                        Parent = r["PARENT"] is DBNull ? 0 : Convert.ToInt32(r["PARENT"]),
                        Code = Convert.ToString(r["CODE"])?.Trim() ?? "",
                        Description = Convert.ToString(r["DESCRIPTION"])?.Trim() ?? "",
                        Description2 = NullIfEmpty(Convert.ToString(r["DESCRIPTION2"])),
                        AccType = NullIfEmpty(Convert.ToString(r["ACCTYPE"])),
                        SpecialAccType = NullIfEmpty(Convert.ToString(r["SPECIALACCTYPE"])),
                        Tax = NullIfEmpty(Convert.ToString(r["TAX"])),
                        CashFlowType = r["CASHFLOWTYPE"] is DBNull ? 0 : Convert.ToInt32(r["CASHFLOWTYPE"]),
                        Sic = NullIfEmpty(Convert.ToString(r["SIC"]))
                    });
                }
                return Services.Accounts.GlAccountTree.OrderTree(list);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GL chart read failed for {Db}", db.DbCode);
            return [];
        }

        static string? NullIfEmpty(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public async Task<List<MasterRecord>> GetUomsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["ST_UOM", "UOM"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];
        var desc = await FirstColumn(db, table, ["DESCRIPTION", "NAME"], cancellationToken).ConfigureAwait(false) ?? "CODE";
        return await QueryMaster(db, $"SELECT FIRST 200 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION FROM {table} ORDER BY CODE", cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<MasterRecord>> GetTermsAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var table = await FirstExisting(db, ["TERMS", "TERM", "AR_TERM"], cancellationToken).ConfigureAwait(false);
        if (table is null)
            return [];

        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
                bool Has(string name) => cols.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!Has("CODE"))
                    return [];

                var desc = Has("DESCRIPTION") ? "DESCRIPTION" : "CODE";
                var activeFilter = Has("ISACTIVE")
                    ? "WHERE COALESCE(ISACTIVE, TRUE) = TRUE"
                    : "";
                var sql = $@"
SELECT FIRST 200 TRIM(CODE) AS CODE, TRIM(COALESCE({desc}, CODE, '')) AS DESCRIPTION
FROM {table}
{activeFilter}
ORDER BY CODE";
                SqlGuard.EnsureSelectOnly(sql);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var list = new List<MasterRecord>();
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var code = Convert.ToString(r["CODE"])?.Trim() ?? "";
                    if (code.Length == 0)
                        continue;
                    list.Add(new MasterRecord
                    {
                        Code = code,
                        Description = Convert.ToString(r["DESCRIPTION"])?.Trim() ?? code,
                        Key = code
                    });
                }
                return list;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terms read failed for {Db}", db.DbCode);
            return [];
        }
    }

    public async Task<MasterRecord?> FindAsync(RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken = default)
    {
        if (string.Equals(entityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
            return await GetItemDetailAsync(db, code, cancellationToken).ConfigureAwait(false)
                   ?? (await GetItemsAsync(db, cancellationToken).ConfigureAwait(false))
                       .FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

        if (PartnerSyncRules.IsPartyEntity(entityCode))
            return await GetPartyDetailAsync(db, entityCode, code, cancellationToken).ConfigureAwait(false)
                   ?? (await (entityCode.ToUpperInvariant() switch
                   {
                       EntityCodes.Customer => GetCustomersAsync(db, cancellationToken),
                       EntityCodes.Supplier => GetSuppliersAsync(db, cancellationToken),
                       _ => Task.FromResult(new List<MasterRecord>())
                   }).ConfigureAwait(false))
                       .FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

        var list = entityCode.ToUpperInvariant() switch
        {
            EntityCodes.Warehouse => await GetWarehousesAsync(db, cancellationToken),
            EntityCodes.TaxCode => await GetTaxCodesAsync(db, cancellationToken),
            EntityCodes.Account => await GetAccountsAsync(db, cancellationToken),
            EntityCodes.Uom => await GetUomsAsync(db, cancellationToken),
            _ => []
        };
        return list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<MasterRecord>> QueryPartyList(
        RegisteredDatabase db, string entityCode, CancellationToken cancellationToken, string? tableOverride = null)
    {
        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                var table = tableOverride ?? await ResolvePartyTable(db, entityCode, conn, tx, cancellationToken).ConfigureAwait(false);
                if (table is null)
                    return [];
                var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
                var nameCol = cols.FirstOrDefault(c => c is "COMPANYNAME" or "DESCRIPTION" or "NAME") ?? "CODE";
                var sql = $@"
SELECT FIRST 500 TRIM(CODE) AS CODE, TRIM(COALESCE({nameCol}, CODE, '')) AS DESCRIPTION
FROM {table} ORDER BY CODE";
                SqlGuard.EnsureSelectOnly(sql);
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
                        Key = Convert.ToString(r["CODE"])?.Trim() ?? "",
                        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["partyType"] = entityCode.ToUpperInvariant()
                        }
                    });
                }
                return list;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Party list query failed for {Db} {Entity}", db.DbCode, entityCode);
            return [];
        }
    }

    private async Task<MasterRecord?> QueryPartyDetail(
        RegisteredDatabase db, string entityCode, string code, CancellationToken cancellationToken)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return null;
        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                var table = await ResolvePartyTable(db, entityCode, conn, tx, cancellationToken).ConfigureAwait(false);
                if (table is null)
                    return null;
                var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
                var wanted = new (string ApiKey, string[] FbNames)[]
                {
                    ("companyname", ["COMPANYNAME", "DESCRIPTION", "NAME"]),
                    ("companyname2", ["COMPANYNAME2"]),
                    ("controlaccount", ["CONTROLACCOUNT", "ACCNO"]),
                    ("companycategory", ["COMPANYCATEGORY", "CATEGORY"]),
                    ("area", ["AREA"]),
                    ("agent", ["AGENT"]),
                    ("biznature", ["BIZNATURE"]),
                    ("creditterm", ["CREDITTERM"]),
                    ("creditlimit", ["CREDITLIMIT"]),
                    ("overduelimit", ["OVERDUELIMIT"]),
                    ("statementtype", ["STATEMENTTYPE"]),
                    ("currencycode", ["CURRENCYCODE", "CURRENCY"]),
                    ("agingon", ["AGINGON"]),
                    ("pricetag", ["PRICETAG"]),
                    ("tax", ["TAX"]),
                    ("taxexemptno", ["TAXEXEMPTNO"]),
                    ("brn", ["BRN", "COMPANYNO"]),
                    ("brn2", ["BRN2"]),
                    ("gstno", ["GSTNO"]),
                    ("salestaxno", ["SALESTAXNO"]),
                    ("servicetaxno", ["SERVICETAXNO"]),
                    ("tin", ["TIN"]),
                    ("idtype", ["IDTYPE"]),
                    ("idno", ["IDNO"]),
                    ("sic", ["SIC"]),
                    ("submissiontype", ["SUBMISSIONTYPE"]),
                    ("irbm_classification", ["IRBM_CLASSIFICATION"]),
                    ("businessunit", ["BUSINESSUNIT"]),
                    ("taxarea", ["TAXAREA"]),
                    ("remark", ["REMARK"]),
                    ("note", ["NOTE"]),
                    ("status", ["STATUS", "ISACTIVE"]),
                    ("udf_email01", ["UDF_EMAIL01"]),
                    ("udf_department01", ["UDF_DEPARTMENT01"]),
                    ("udf_location", ["UDF_LOCATION"]),
                    ("address1", ["ADDRESS1", "ADDRESS"]),
                    ("address2", ["ADDRESS2"]),
                    ("address3", ["ADDRESS3"]),
                    ("address4", ["ADDRESS4"]),
                    ("postcode", ["POSTCODE", "ZIP"]),
                    ("city", ["CITY"]),
                    ("state", ["STATE"]),
                    ("country", ["COUNTRY"]),
                    ("attention", ["ATTENTION"]),
                    ("phone1", ["PHONE1", "PHONE", "TEL"]),
                    ("phone2", ["PHONE2"]),
                    ("mobile", ["MOBILE"]),
                    ("fax1", ["FAX1", "FAX"]),
                    ("email", ["EMAIL"])
                };

                var selectParts = new List<string> { "TRIM(CODE) AS CODE" };
                var selectedKeys = new List<string>();
                var i = 0;
                foreach (var (apiKey, fbNames) in wanted)
                {
                    var col = fbNames.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
                    if (col is null) continue;
                    var alias = $"F{i}";
                    i++;
                    selectParts.Add($"TRIM(COALESCE(CAST({col} AS VARCHAR(500)), '')) AS {alias}");
                    selectedKeys.Add(apiKey);
                }

                var sql = $"SELECT FIRST 1 {string.Join(", ", selectParts)} FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
                SqlGuard.EnsureSelectOnly(sql);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return null;

                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["partyType"] = entityCode.ToUpperInvariant()
                };
                var codeVal = Convert.ToString(r["CODE"])?.Trim() ?? "";
                var name = "";
                for (var k = 0; k < selectedKeys.Count; k++)
                {
                    var apiKey = selectedKeys[k];
                    var val = Convert.ToString(r[$"F{k}"])?.Trim() ?? "";
                    if (val.Length == 0) continue;
                    fields[apiKey] = val;
                    if (apiKey == "companyname")
                        name = val;
                }

                await AppendBranchFieldsAsync(conn, tx, entityCode, codeVal, fields, cancellationToken).ConfigureAwait(false);

                return new MasterRecord
                {
                    Code = codeVal,
                    Description = name.Length > 0 ? name : codeVal,
                    Key = codeVal,
                    Fields = fields
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Party detail query failed for {Db} {Entity} {Code}", db.DbCode, entityCode, code);
            return null;
        }
    }

    private async Task<MasterRecord?> QueryItemDetail(RegisteredDatabase db, string code, CancellationToken cancellationToken)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return null;
        try
        {
            return await WithReadTx(db, async (conn, tx) =>
            {
                if (!await TableExistsConn(conn, tx, "ST_ITEM", cancellationToken).ConfigureAwait(false))
                    return null;

                var cols = await Columns(conn, tx, "ST_ITEM", cancellationToken).ConfigureAwait(false);
                var wanted = new (string ApiKey, string[] FbNames)[]
                {
                    ("description", ["DESCRIPTION"]),
                    ("description2", ["DESCRIPTION2"]),
                    ("description3", ["DESCRIPTION3"]),
                    ("stockgroup", ["STOCKGROUP"]),
                    ("stockcontrol", ["STOCKCONTROL"]),
                    ("costingmethod", ["COSTINGMETHOD"]),
                    ("serialnumber", ["SERIALNUMBER"]),
                    ("shelf", ["SHELF"]),
                    ("minqty", ["MINQTY"]),
                    ("maxqty", ["MAXQTY"]),
                    ("suom", ["SUOM"]),
                    ("reorderlevel", ["REORDERLEVEL"]),
                    ("reorderqty", ["REORDERQTY"]),
                    ("sltax", ["SLTAX"]),
                    ("phtax", ["PHTAX"]),
                    ("tariff", ["TARIFF"]),
                    ("irbm_classification", ["IRBM_CLASSIFICATION"]),
                    ("stockmatrix", ["STOCKMATRIX"]),
                    ("defuom_st", ["DEFUOM_ST"]),
                    ("defuom_sl", ["DEFUOM_SL"]),
                    ("defuom_ph", ["DEFUOM_PH"]),
                    ("scriptcode", ["SCRIPTCODE"]),
                    ("isactive", ["ISACTIVE"]),
                    ("uom", ["UOM"]),
                    ("refcost", ["REFCOST"]),
                    ("refprice", ["REFPRICE"]),
                    ("itemtype", ["ITEMTYPE"]),
                    ("leadtime", ["LEADTIME"]),
                    ("bom_leadtime", ["BOM_LEADTIME"]),
                    ("bom_asmcost", ["BOM_ASMCOST"]),
                    ("remark1", ["REMARK1"]),
                    ("remark2", ["REMARK2"]),
                    ("barcode", ["BARCODE"]),
                    ("category", ["CATEGORY"]),
                    ("creationdate", ["CREATIONDATE"]),
                    ("udf_stdprice", ["UDF_STDPRICE"]),
                    ("udf_dleadtime", ["UDF_DLEADTIME"]),
                    ("udf_moq", ["UDF_MOQ"]),
                    ("udf_bundle", ["UDF_BUNDLE"]),
                    ("udf_thickness", ["UDF_THICKNESS"]),
                    ("udf_width", ["UDF_WIDTH"]),
                    ("udf_length", ["UDF_LENGTH"]),
                    ("udf_ds", ["UDF_DS"]),
                    ("udf_dp", ["UDF_DP"]),
                    ("udf_dsys", ["UDF_DSYS"]),
                    ("udf_dfs", ["UDF_DFS"]),
                    ("udf_dfp", ["UDF_DFP"]),
                    ("udf_dfsys", ["UDF_DFSYS"]),
                    ("udf_wts", ["UDF_WTS"]),
                    ("udf_wtp", ["UDF_WTP"]),
                    ("udf_wtsys", ["UDF_WTSYS"]),
                    ("udf_2uom", ["UDF_2UOM"]),
                    ("udf_formula", ["UDF_FORMULA"]),
                    ("udf_pricekg", ["UDF_PRICEKG"]),
                    ("udf_costkg", ["UDF_COSTKG"]),
                    ("udf_uom", ["UDF_UOM"]),
                    ("udf_mtype", ["UDF_MTYPE"]),
                    ("udf_stockgroup", ["UDF_STOCKGROUP"]),
                    ("udf_direct", ["UDF_DIRECT"])
                };

                var selectParts = new List<string> { "TRIM(CODE) AS CODE" };
                var selectedKeys = new List<string>();
                var i = 0;
                foreach (var (apiKey, fbNames) in wanted)
                {
                    var col = fbNames.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
                    if (col is null) continue;
                    var alias = $"F{i}";
                    i++;
                    selectParts.Add($"TRIM(COALESCE(CAST({col} AS VARCHAR(500)), '')) AS {alias}");
                    selectedKeys.Add(apiKey);
                }

                var sql = $"SELECT FIRST 1 {string.Join(", ", selectParts)} FROM ST_ITEM WHERE UPPER(TRIM(CODE))=UPPER(@C)";
                SqlGuard.EnsureSelectOnly(sql);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = key });
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return null;

                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var codeVal = Convert.ToString(r["CODE"])?.Trim() ?? "";
                var name = "";
                for (var k = 0; k < selectedKeys.Count; k++)
                {
                    var apiKey = selectedKeys[k];
                    var val = Convert.ToString(r[$"F{k}"])?.Trim() ?? "";
                    if (val.Length == 0) continue;
                    fields[apiKey] = val;
                    if (apiKey == "description")
                        name = val;
                }

                await AppendItemChildFieldsAsync(conn, tx, codeVal, fields, cancellationToken).ConfigureAwait(false);

                return new MasterRecord
                {
                    Code = codeVal,
                    Description = name.Length > 0 ? name : codeVal,
                    Key = codeVal,
                    Fields = fields
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Item detail query failed for {Db} {Code}", db.DbCode, code);
            return null;
        }
    }

    private async Task AppendItemChildFieldsAsync(
        FbConnection conn, FbTransaction tx, string code,
        Dictionary<string, string> fields, CancellationToken ct)
    {
        foreach (var table in new[] { "ST_ITEM_UOM", "ST_ITEMUOM" })
        {
            if (!await TableExistsConn(conn, tx, table, ct).ConfigureAwait(false))
                continue;
            var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
            if (!cols.Contains("CODE", StringComparer.OrdinalIgnoreCase))
                continue;

            string? Col(params string[] names) => names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
            var uomCol = Col("UOM");
            if (uomCol is null) continue;

            var select = new List<string> { "TRIM(COALESCE(CAST(UOM AS VARCHAR(40)), '')) AS UOM" };
            if (Col("RATE") is { } rateCol) select.Add($"TRIM(COALESCE(CAST({rateCol} AS VARCHAR(40)), '')) AS RATE");
            if (Col("REFCOST") is { } rcCol) select.Add($"TRIM(COALESCE(CAST({rcCol} AS VARCHAR(40)), '')) AS REFCOST");
            if (Col("REFPRICE") is { } rpCol) select.Add($"TRIM(COALESCE(CAST({rpCol} AS VARCHAR(40)), '')) AS REFPRICE");
            if (Col("MINCOST") is { } mcCol) select.Add($"TRIM(COALESCE(CAST({mcCol} AS VARCHAR(40)), '')) AS MINCOST");
            if (Col("MAXCOST") is { } xcCol) select.Add($"TRIM(COALESCE(CAST({xcCol} AS VARCHAR(40)), '')) AS MAXCOST");
            if (Col("MINPRICE") is { } mpCol) select.Add($"TRIM(COALESCE(CAST({mpCol} AS VARCHAR(40)), '')) AS MINPRICE");
            if (Col("MAXPRICE") is { } xpCol) select.Add($"TRIM(COALESCE(CAST({xpCol} AS VARCHAR(40)), '')) AS MAXPRICE");
            if (Col("ISBASE") is { } ibCol) select.Add($"TRIM(COALESCE(CAST({ibCol} AS VARCHAR(10)), '')) AS ISBASE");

            var sql = $"SELECT {string.Join(", ", select)} FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
            SqlGuard.EnsureSelectOnly(sql);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var uoms = new List<StockItemUom>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var u = Convert.ToString(r["UOM"])?.Trim() ?? "";
                if (u.Length == 0) continue;
                uoms.Add(new StockItemUom
                {
                    Uom = u,
                    Rate = HasCol(r, "RATE") ? Convert.ToString(r["RATE"])?.Trim() ?? "1" : "1",
                    RefCost = HasCol(r, "REFCOST") ? Convert.ToString(r["REFCOST"])?.Trim() ?? "0" : "0",
                    RefPrice = HasCol(r, "REFPRICE") ? Convert.ToString(r["REFPRICE"])?.Trim() ?? "0" : "0",
                    MinCost = HasCol(r, "MINCOST") ? Convert.ToString(r["MINCOST"])?.Trim() ?? "0" : "0",
                    MaxCost = HasCol(r, "MAXCOST") ? Convert.ToString(r["MAXCOST"])?.Trim() ?? "0" : "0",
                    MinPrice = HasCol(r, "MINPRICE") ? Convert.ToString(r["MINPRICE"])?.Trim() ?? "0" : "0",
                    MaxPrice = HasCol(r, "MAXPRICE") ? Convert.ToString(r["MAXPRICE"])?.Trim() ?? "0" : "0",
                    IsBase = !HasCol(r, "ISBASE") || Convert.ToString(r["ISBASE"])?.Trim() is "1" or "Y" or "T" or "true"
                });
            }
            if (uoms.Count > 0)
            {
                fields["sdsuom_json"] = System.Text.Json.JsonSerializer.Serialize(uoms);
                break;
            }
        }

        foreach (var table in new[] { "ST_ITEM_CATEGORY", "ST_ITEMCATEGORY" })
        {
            if (!await TableExistsConn(conn, tx, table, ct).ConfigureAwait(false))
                continue;
            var cols = await Columns(conn, tx, table, ct).ConfigureAwait(false);
            if (!cols.Contains("CODE", StringComparer.OrdinalIgnoreCase))
                continue;
            var catCol = cols.FirstOrDefault(c => c.Equals("CATEGORY", StringComparison.OrdinalIgnoreCase));
            if (catCol is null) continue;

            var sql = $"SELECT TRIM(COALESCE(CAST({catCol} AS VARCHAR(80)), '')) AS CATEGORY FROM {table} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
            SqlGuard.EnsureSelectOnly(sql);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var cats = new List<StockItemCategory>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var c = Convert.ToString(r["CATEGORY"])?.Trim() ?? "";
                if (c.Length > 0)
                    cats.Add(new StockItemCategory { Category = c });
            }
            if (cats.Count > 0)
            {
                fields["sdscategory_json"] = System.Text.Json.JsonSerializer.Serialize(cats);
                break;
            }
        }
    }

    private async Task AppendBranchFieldsAsync(
        FbConnection conn, FbTransaction tx, string entityCode, string code,
        Dictionary<string, string> fields, CancellationToken ct)
    {
        var candidates = string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
            ? new[] { "AR_CUSTBRANCH", "AR_CUSTOMERBRANCH", "AR_BRANCH" }
            : new[] { "AP_SUPPBRANCH", "AP_SUPPLIERBRANCH", "AP_BRANCH", "AP_VENDORBRANCH" };

        string? branchTable = null;
        foreach (var t in candidates)
        {
            if (await TableExistsConn(conn, tx, t, ct).ConfigureAwait(false))
            {
                branchTable = t;
                break;
            }
        }
        if (branchTable is null)
            return;

        var cols = await Columns(conn, tx, branchTable, ct).ConfigureAwait(false);
        if (!cols.Contains("CODE", StringComparer.OrdinalIgnoreCase))
            return;

        string? Col(params string[] names) => names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
        var select = new List<string>();
        void Add(string alias, params string[] names)
        {
            var c = Col(names);
            if (c is not null)
                select.Add($"TRIM(COALESCE(CAST({c} AS VARCHAR(300)), '')) AS {alias}");
        }

        Add("BRANCHTYPE", "BRANCHTYPE", "BRANCH_TYPE");
        Add("BRANCHNAME", "BRANCHNAME", "BRANCH_NAME", "NAME");
        Add("ADDRESS1", "ADDRESS1", "ADDRESS");
        Add("ADDRESS2", "ADDRESS2");
        Add("ADDRESS3", "ADDRESS3");
        Add("ADDRESS4", "ADDRESS4");
        Add("POSTCODE", "POSTCODE", "ZIP");
        Add("CITY", "CITY");
        Add("STATE", "STATE");
        Add("COUNTRY", "COUNTRY");
        Add("ATTENTION", "ATTENTION");
        Add("PHONE1", "PHONE1", "PHONE", "TEL");
        Add("PHONE2", "PHONE2");
        Add("MOBILE", "MOBILE");
        Add("FAX1", "FAX1", "FAX");
        Add("FAX2", "FAX2");
        Add("EMAIL", "EMAIL");
        if (select.Count == 0)
            return;

        var sql = $"SELECT FIRST 5 {string.Join(", ", select)} FROM {branchTable} WHERE UPPER(TRIM(CODE))=UPPER(@C)";
        SqlGuard.EnsureSelectOnly(sql);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["C"] = code });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var branches = new List<Dictionary<string, string>>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Take(string name, string field)
            {
                if (!HasCol(r, name)) return;
                var v = Convert.ToString(r[name])?.Trim() ?? "";
                if (v.Length > 0) b[field] = v;
            }
            Take("BRANCHTYPE", "BranchType");
            Take("BRANCHNAME", "BranchName");
            Take("ADDRESS1", "Address1");
            Take("ADDRESS2", "Address2");
            Take("ADDRESS3", "Address3");
            Take("ADDRESS4", "Address4");
            Take("POSTCODE", "Postcode");
            Take("CITY", "City");
            Take("STATE", "State");
            Take("COUNTRY", "Country");
            Take("ATTENTION", "Attention");
            Take("PHONE1", "Phone1");
            Take("PHONE2", "Phone2");
            Take("MOBILE", "Mobile");
            Take("FAX1", "Fax1");
            Take("FAX2", "Fax2");
            Take("EMAIL", "Email");
            if (b.Count > 0)
                branches.Add(b);
        }

        if (branches.Count == 0)
            return;

        // Flatten first branch into fields for mapper fallback; also store JSON for multi-branch.
        var first = branches[0];
        foreach (var (k, v) in first)
            fields[k.ToLowerInvariant()] = v;

        fields["sdsbranch_json"] = System.Text.Json.JsonSerializer.Serialize(
            branches.Select(b => new PartnerBranch
            {
                BranchType = b.GetValueOrDefault("BranchType", "B"),
                BranchName = b.GetValueOrDefault("BranchName", "BILLING"),
                Address1 = b.GetValueOrDefault("Address1", ""),
                Address2 = b.GetValueOrDefault("Address2", ""),
                Address3 = b.GetValueOrDefault("Address3", ""),
                Address4 = b.GetValueOrDefault("Address4", ""),
                Postcode = b.GetValueOrDefault("Postcode", ""),
                City = b.GetValueOrDefault("City", ""),
                State = b.GetValueOrDefault("State", ""),
                Country = b.GetValueOrDefault("Country", "MY"),
                Attention = b.GetValueOrDefault("Attention", ""),
                Phone1 = b.GetValueOrDefault("Phone1", ""),
                Phone2 = b.GetValueOrDefault("Phone2", ""),
                Mobile = b.GetValueOrDefault("Mobile", ""),
                Fax1 = b.GetValueOrDefault("Fax1", ""),
                Fax2 = b.GetValueOrDefault("Fax2", ""),
                Email = b.GetValueOrDefault("Email", "")
            }));
    }

    private static bool HasCol(FbDataReader r, string name)
    {
        try { return r.GetOrdinal(name) >= 0; }
        catch { return false; }
    }

    private async Task<string?> ResolvePartyTable(
        RegisteredDatabase db, string entityCode, FbConnection conn, FbTransaction tx, CancellationToken ct)
    {
        if (string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase))
            return await TableExistsConn(conn, tx, "AR_CUSTOMER", ct).ConfigureAwait(false) ? "AR_CUSTOMER" : null;
        foreach (var t in new[] { "AP_SUPPLIER", "AP_VENDOR", "CREDITOR" })
        {
            if (await TableExistsConn(conn, tx, t, ct).ConfigureAwait(false))
                return t;
        }
        return null;
    }

    public async Task<DocumentHeader?> GetDocumentAsync(RegisteredDatabase db, string docType, string docKeyOrNo, CancellationToken cancellationToken = default)
    {
        var (header, detail, typeCode) = ResolveDocTables(docType);
        if (header is null)
            return null;

        var key = (docKeyOrNo ?? "").Trim();
        if (key.Length == 0)
            return null;

        return await WithReadTx(db, async (conn, tx) =>
        {
            (header, detail, typeCode) = await ResolveDocTablesAsync(conn, tx, docType, cancellationToken).ConfigureAwait(false);
            if (header is null)
                return null;
            if (!await TableExistsConn(conn, tx, header, cancellationToken).ConfigureAwait(false))
                return null;

            var cols = await Columns(conn, tx, header, cancellationToken).ConfigureAwait(false);
            string? Col(params string[] names) => names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));

            var select = new List<string>
            {
                "DOCKEY",
                "TRIM(DOCNO) AS DOCNO",
                "DOCDATE"
            };
            void Add(string alias, params string[] names)
            {
                var c = Col(names);
                if (c is not null)
                    select.Add($"TRIM(COALESCE(CAST({c} AS VARCHAR(500)), '')) AS {alias}");
            }
            void AddNum(string alias, params string[] names)
            {
                var c = Col(names);
                if (c is not null)
                    select.Add($"COALESCE({c}, 0) AS {alias}");
            }
            void AddDate(string alias, params string[] names)
            {
                var c = Col(names);
                if (c is not null)
                    select.Add($"{c} AS {alias}");
            }

            Add("PARTYCODE", "CODE");
            Add("PARTYNAME", "COMPANYNAME", "CODE");
            AddNum("AMOUNT", "DOCAMT", "LOCALDOCAMT");
            Add("CURRENCY", "CURRENCYCODE", "CURRENCY");
            Add("CURRENCYRATE", "CURRENCYRATE");
            Add("TERMS", "TERMS");
            Add("AREA", "AREA");
            Add("AGENT", "AGENT");
            Add("PROJECT", "PROJECT");
            Add("DESCRIPTION", "DESCRIPTION");
            Add("NOTE", "NOTE");
            AddDate("POSTDATE", "POSTDATE");
            AddDate("TAXDATE", "TAXDATE");
            AddDate("DUEDATE", "DUEDATE");
            Add("ADDRESS1", "ADDRESS1");
            Add("ADDRESS2", "ADDRESS2");
            Add("ADDRESS3", "ADDRESS3");
            Add("ADDRESS4", "ADDRESS4");
            Add("POSTCODE", "POSTCODE");
            Add("CITY", "CITY");
            Add("STATE", "STATE");
            Add("COUNTRY", "COUNTRY");
            Add("PHONE1", "PHONE1");
            Add("ATTENTION", "ATTENTION");
            Add("BRANCHNAME", "BRANCHNAME");
            Add("DADDRESS1", "DADDRESS1");
            Add("DADDRESS2", "DADDRESS2");
            Add("DADDRESS3", "DADDRESS3");
            Add("DADDRESS4", "DADDRESS4");
            Add("DPOSTCODE", "DPOSTCODE");
            Add("DCITY", "DCITY");
            Add("DSTATE", "DSTATE");
            Add("DCOUNTRY", "DCOUNTRY");
            Add("DPHONE1", "DPHONE1");
            Add("DOCREF1", "DOCREF1");
            Add("TAXEXEMPTNO", "TAXEXEMPTNO");
            Add("TIN", "TIN");

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
SELECT FIRST 1 {string.Join(", ", select)}
FROM {header}
WHERE CAST(DOCKEY AS VARCHAR(40)) = @K OR UPPER(TRIM(DOCNO)) = UPPER(@K)";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["K"] = key });
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            string S(string name) => HasCol(r, name) ? Convert.ToString(r[name])?.Trim() ?? "" : "";
            DateTime? D(string name) => HasCol(r, name) && r[name] is DateTime dt ? dt : null;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Put(string apiKey, string col)
            {
                var v = S(col);
                if (v.Length > 0) fields[apiKey] = v;
            }
            Put("address1", "ADDRESS1");
            Put("address2", "ADDRESS2");
            Put("address3", "ADDRESS3");
            Put("address4", "ADDRESS4");
            Put("postcode", "POSTCODE");
            Put("city", "CITY");
            Put("state", "STATE");
            Put("country", "COUNTRY");
            Put("phone1", "PHONE1");
            Put("attention", "ATTENTION");
            Put("branchname", "BRANCHNAME");
            Put("daddress1", "DADDRESS1");
            Put("daddress2", "DADDRESS2");
            Put("daddress3", "DADDRESS3");
            Put("daddress4", "DADDRESS4");
            Put("dpostcode", "DPOSTCODE");
            Put("dcity", "DCITY");
            Put("dstate", "DSTATE");
            Put("dcountry", "DCOUNTRY");
            Put("dphone1", "DPHONE1");
            Put("docref1", "DOCREF1");
            Put("taxexemptno", "TAXEXEMPTNO");
            Put("tin", "TIN");

            var doc = new DocumentHeader
            {
                DocType = typeCode,
                DocKey = Convert.ToString(r["DOCKEY"]) ?? "",
                DocNo = S("DOCNO"),
                DocDate = D("DOCDATE"),
                PostDate = D("POSTDATE"),
                TaxDate = D("TAXDATE"),
                DueDate = D("DUEDATE"),
                PartyCode = S("PARTYCODE"),
                PartyName = S("PARTYNAME"),
                Description = S("DESCRIPTION"),
                Terms = S("TERMS"),
                Area = S("AREA"),
                Agent = S("AGENT"),
                Project = S("PROJECT"),
                Amount = HasCol(r, "AMOUNT") ? Convert.ToDecimal(r["AMOUNT"]) : 0m,
                Currency = S("CURRENCY") is { Length: > 0 } ccy ? ccy : "----",
                CurrencyRate = S("CURRENCYRATE") is { Length: > 0 } cr ? cr : "1",
                Note = S("NOTE"),
                Fields = fields
            };

            if (detail is not null && await TableExistsConn(conn, tx, detail, cancellationToken).ConfigureAwait(false))
                await AppendDocumentLinesAsync(conn, tx, detail, doc, cancellationToken).ConfigureAwait(false);

            return doc;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<List<DocumentListItem>> ListDocumentsAsync(RegisteredDatabase db, string docType, int take = 200, CancellationToken cancellationToken = default)
    {
        var (header, detail, typeCode) = ResolveDocTables(docType);
        if (header is null)
            return Task.FromResult(new List<DocumentListItem>());

        take = Math.Clamp(take, 1, 500);
        return WithReadTx(db, async (conn, tx) =>
        {
            (header, detail, typeCode) = await ResolveDocTablesAsync(conn, tx, docType, cancellationToken).ConfigureAwait(false);
            if (header is null)
                return new List<DocumentListItem>();
            if (!await TableExistsConn(conn, tx, header, cancellationToken).ConfigureAwait(false))
                return new List<DocumentListItem>();

            var cols = await Columns(conn, tx, header, cancellationToken).ConfigureAwait(false);
            string? Col(params string[] names) => names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (Col("DOCNO") is null || Col("DOCKEY") is null)
                return new List<DocumentListItem>();

            var select = new List<string>
            {
                "H.DOCKEY",
                "TRIM(H.DOCNO) AS DOCNO",
                "H.DOCDATE"
            };
            void Add(string alias, params string[] names)
            {
                var c = Col(names);
                if (c is not null)
                    select.Add($"TRIM(COALESCE(CAST(H.{c} AS VARCHAR(500)), '')) AS {alias}");
            }
            void AddNum(string alias, params string[] names)
            {
                var c = Col(names);
                if (c is not null)
                    select.Add($"COALESCE(H.{c}, 0) AS {alias}");
            }

            Add("PARTYCODE", "CODE");
            Add("PARTYNAME", "COMPANYNAME", "CODE");
            AddNum("AMOUNT", "DOCAMT", "LOCALDOCAMT");

            var qtyExpr = "CAST(0 AS NUMERIC(18,4)) AS QTYTOTAL";
            if (!string.IsNullOrWhiteSpace(detail)
                && await TableExistsConn(conn, tx, detail, cancellationToken).ConfigureAwait(false))
            {
                var dCols = await Columns(conn, tx, detail, cancellationToken).ConfigureAwait(false);
                var qtyCol = dCols.Contains("QTY", StringComparer.OrdinalIgnoreCase) ? "QTY"
                    : dCols.Contains("SQTY", StringComparer.OrdinalIgnoreCase) ? "SQTY"
                    : null;
                if (qtyCol is not null)
                    qtyExpr = $"(SELECT COALESCE(SUM(COALESCE(D.{qtyCol}, 0)), 0) FROM {detail} D WHERE D.DOCKEY = H.DOCKEY) AS QTYTOTAL";
            }
            select.Add(qtyExpr);

            var orderBy = Col("DOCDATE") is not null ? "H.DOCDATE DESC, H.DOCNO DESC" : "H.DOCNO DESC";
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT FIRST {take} {string.Join(", ", select)} FROM {header} H ORDER BY {orderBy}";
            SqlGuard.EnsureSelectOnly(cmd.CommandText);

            var list = new List<DocumentListItem>();
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string S(string name) => HasCol(r, name) ? Convert.ToString(r[name])?.Trim() ?? "" : "";
                DateTime? D(string name) => HasCol(r, name) && r[name] is DateTime dt ? dt : null;
                list.Add(new DocumentListItem
                {
                    DocType = typeCode,
                    DocKey = Convert.ToString(r["DOCKEY"]) ?? "",
                    DocNo = S("DOCNO"),
                    DocDate = D("DOCDATE"),
                    PartyCode = S("PARTYCODE"),
                    PartyName = S("PARTYNAME"),
                    Amount = HasCol(r, "AMOUNT") ? Convert.ToDecimal(r["AMOUNT"]) : 0m,
                    Qty = HasCol(r, "QTYTOTAL") ? Convert.ToDecimal(r["QTYTOTAL"]) : 0m
                });
            }
            return list;
        }, cancellationToken);
    }

    private async Task AppendDocumentLinesAsync(
        FbConnection conn, FbTransaction tx, string detailTable, DocumentHeader doc, CancellationToken ct)
    {
        var cols = await Columns(conn, tx, detailTable, ct).ConfigureAwait(false);
        string? Col(params string[] names) => names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));

        var select = new List<string> { "DTLKEY", "COALESCE(SEQ,0) AS SEQ" };
        void Add(string alias, params string[] names)
        {
            var c = Col(names);
            if (c is not null)
                select.Add($"TRIM(COALESCE(CAST({c} AS VARCHAR(500)), '')) AS {alias}");
        }
        void AddNum(string alias, params string[] names)
        {
            var c = Col(names);
            if (c is not null)
                select.Add($"COALESCE({c}, 0) AS {alias}");
        }
        void AddDate(string alias, params string[] names)
        {
            var c = Col(names);
            if (c is not null)
                select.Add($"{c} AS {alias}");
        }

        Add("ITEMCODE", "ITEMCODE");
        Add("DESCRIPTION", "DESCRIPTION");
        Add("DESCRIPTION2", "DESCRIPTION2");
        AddNum("QTY", "QTY");
        Add("UOM", "UOM");
        Add("RATE", "RATE");
        AddNum("UNITPRICE", "UNITPRICE");
        AddNum("AMOUNT", "AMOUNT");
        Add("TAX", "TAX", "TAXCODE");
        Add("LOCATION", "LOCATION", "WAREHOUSE");
        Add("FROMLOCATION", "FROMLOCATION");
        Add("TOLOCATION", "TOLOCATION");
        Add("BATCH", "BATCH");
        Add("PROJECT", "PROJECT");
        Add("ACCOUNT", "ACCOUNT");
        Add("DISC", "DISC");
        Add("TAXINCLUSIVE", "TAXINCLUSIVE");
        Add("IRBM", "IRBM_CLASSIFICATION");
        AddDate("DELIVERYDATE", "DELIVERYDATE");
        Add("UDF_THICKNESS", "UDF_THICKNESS");
        Add("UDF_WIDTH", "UDF_WIDTH");
        Add("UDF_LENGTH", "UDF_LENGTH");
        Add("UDF_DP", "UDF_DP");
        Add("UDF_DFP", "UDF_DFP");
        Add("UDF_WTP", "UDF_WTP");
        Add("UDF_2UOM", "UDF_2UOM");
        Add("UDF_FORMULA", "UDF_FORMULA");
        Add("UDF_MTYPE", "UDF_MTYPE");
        Add("UDF_STDPRICE", "UDF_STDPRICE");
        Add("UDF_DLEADTIME", "UDF_DLEADTIME");
        Add("UDF_MOQ", "UDF_MOQ");
        Add("UDF_BUNDLE", "UDF_BUNDLE");

        await using var dcmd = conn.CreateCommand();
        dcmd.Transaction = tx;
        dcmd.CommandText = $@"
SELECT {string.Join(", ", select)}
FROM {detailTable}
WHERE DOCKEY = @DOCKEY
ORDER BY COALESCE(SEQ,0)";
        SqlGuard.EnsureSelectOnly(dcmd.CommandText);
        CentralDb.Bind(dcmd, new Dictionary<string, object?> { ["DOCKEY"] = Convert.ToInt64(doc.DocKey) });
        await using var dr = await dcmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await dr.ReadAsync(ct).ConfigureAwait(false))
        {
            string S(string name) => HasCol(dr, name) ? Convert.ToString(dr[name])?.Trim() ?? "" : "";
            decimal N(string name) => HasCol(dr, name) ? Convert.ToDecimal(dr[name]) : 0m;
            var lineFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var udf in new[]
                     {
                         "UDF_THICKNESS", "UDF_WIDTH", "UDF_LENGTH", "UDF_DP", "UDF_DFP", "UDF_WTP",
                         "UDF_2UOM", "UDF_FORMULA", "UDF_MTYPE", "UDF_STDPRICE", "UDF_DLEADTIME",
                         "UDF_MOQ", "UDF_BUNDLE"
                     })
            {
                var v = S(udf);
                if (v.Length > 0)
                    lineFields[udf.ToLowerInvariant()] = v;
            }

            var taxInc = S("TAXINCLUSIVE");
            doc.Lines.Add(new DocumentLine
            {
                DtlKey = Convert.ToString(dr["DTLKEY"]) ?? "",
                Seq = Convert.ToInt32(dr["SEQ"]),
                ItemCode = S("ITEMCODE"),
                Description = S("DESCRIPTION"),
                Description2 = S("DESCRIPTION2"),
                Qty = N("QTY"),
                Uom = S("UOM"),
                Rate = S("RATE") is { Length: > 0 } rt ? rt : "1",
                UnitPrice = N("UNITPRICE"),
                Amount = N("AMOUNT"),
                TaxCode = S("TAX"),
                Warehouse = S("LOCATION"),
                FromLocation = S("FROMLOCATION"),
                ToLocation = S("TOLOCATION"),
                Batch = S("BATCH"),
                Project = S("PROJECT"),
                Account = S("ACCOUNT"),
                Disc = S("DISC"),
                TaxInclusive = taxInc is "1" or "Y" or "T" or "true",
                IrbmClassification = S("IRBM"),
                DeliveryDate = HasCol(dr, "DELIVERYDATE") && dr["DELIVERYDATE"] is DateTime dd ? dd : null,
                Fields = lineFields
            });
        }
    }

    public async Task<ReconcileResult> FindByTxnStampAsync(RegisteredDatabase db, string txnId, CancellationToken cancellationToken = default)
    {
        // Prefer a UDF column if present; discovery is used so we do not invent column names.
        var tables = new[] { "PH_IV", "PH_PI", "SL_IV", "PH_GR", "SL_DO" };
        return await WithReadTxOrFallback(db, async (conn, tx) =>
        {
            foreach (var table in tables)
            {
                if (!await TableExistsConn(conn, tx, table, cancellationToken).ConfigureAwait(false))
                    continue;
                var cols = await Columns(conn, tx, table, cancellationToken).ConfigureAwait(false);
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
        }, fallback: new ReconcileResult(), cancellationToken).ConfigureAwait(false);
    }

    public Task<ReconcileResult> FindTransferAsync(RegisteredDatabase db, string fromDocType, string fromDocKey, CancellationToken cancellationToken = default) =>
        WithReadTxOrFallback(db, async (conn, tx) =>
        {
            if (!await TableExistsConn(conn, tx, "ST_XTRANS", cancellationToken).ConfigureAwait(false))
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
        }, fallback: new ReconcileResult(), cancellationToken);

    public Task<SchemaDiscoveryReport> DiscoverSchemaAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
        WithReadTxOrFallback(db, async (conn, tx) =>
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
                var cols = await Columns(conn, tx, name, cancellationToken).ConfigureAwait(false);
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
        }, fallback: new SchemaDiscoveryReport { DbId = db.DbId, AtUtc = DateTime.UtcNow }, cancellationToken);

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
        // Wait (not NoWait): SQL Account often holds locks on open company DBs; NoWait
        // turned those into swallowed failures that looked like "document not found".
        var options = new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read
                                | FbTransactionBehavior.ReadCommitted
                                | FbTransactionBehavior.RecVersion
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

    /// <summary>Same as <see cref="WithReadTx{T}"/> but returns <paramref name="fallback"/> when the company FDB cannot be opened.</summary>
    private async Task<T> WithReadTxOrFallback<T>(
        RegisteredDatabase db,
        Func<FbConnection, FbTransaction, Task<T>> work,
        T fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WithReadTx(db, work, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Company database unavailable for {Db} ({Path})", db.DbCode, db.FbPath);
            return fallback;
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

    private static async Task<string?> FirstExistingConn(
        FbConnection conn, FbTransaction tx, IEnumerable<string> names, CancellationToken ct)
    {
        foreach (var n in names)
        {
            if (await TableExistsConn(conn, tx, n, ct).ConfigureAwait(false))
                return n;
        }
        return null;
    }

    private Task<bool> TableExists(RegisteredDatabase db, string table, CancellationToken ct) =>
        WithReadTxOrFallback(db, (conn, tx) => TableExistsConn(conn, tx, table, ct), fallback: false, ct);

    private static async Task<bool> TableExistsConn(FbConnection conn, FbTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME)=@N";
        SqlGuard.EnsureSelectOnly(cmd.CommandText);
        CentralDb.Bind(cmd, new Dictionary<string, object?> { ["N"] = table });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private async Task<string?> FirstColumn(RegisteredDatabase db, string table, IEnumerable<string> names, CancellationToken ct)
    {
        var cols = await WithReadTxOrFallback(db, (conn, tx) => Columns(conn, tx, table, ct), fallback: [], ct)
            .ConfigureAwait(false);
        return names.FirstOrDefault(n => cols.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<List<string>> Columns(FbConnection conn, FbTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
            "ST" or "STOCKTRANSFER" => ("ST_XF", "ST_XFDTL", "ST"),
            "SR" or "SALESRETURN" => ("SL_SR", "SL_SRDTL", "SR"),
            "PR" or "PURCHASERETURN" => ("PH_SC", "PH_SCDTL", "PR"),
            "CN" or "CREDITNOTE" => ("SL_CN", "SL_CNDTL", "CN"),
            _ => (null, null, docType)
        };

    private static async Task<(string? Header, string? Detail, string Type)> ResolveDocTablesAsync(
        FbConnection conn, FbTransaction tx, string docType, CancellationToken ct)
    {
        var resolved = ResolveDocTables(docType);
        if (IsStockTransferType(docType))
        {
            var header = await FirstExistingConn(conn, tx, ["ST_XF", "ST_TRFR"], ct).ConfigureAwait(false);
            if (header is null)
                return (null, null, "ST");
            var detail = header == "ST_XF" ? "ST_XFDTL" : "ST_TRFRDTL";
            if (!await TableExistsConn(conn, tx, detail, ct).ConfigureAwait(false))
                detail = header == "ST_XF" ? "ST_XF_DTL" : "ST_TRFR_DTL";
            if (!await TableExistsConn(conn, tx, detail, ct).ConfigureAwait(false))
                detail = null;
            return (header, detail, "ST");
        }

        if (IsPurchaseReturnType(docType))
        {
            var header = await FirstExistingConn(conn, tx, ["PH_SC", "PH_PR"], ct).ConfigureAwait(false);
            if (header is null)
                return (null, null, "PR");
            var detail = header == "PH_SC" ? "PH_SCDTL" : "PH_PRDTL";
            if (!await TableExistsConn(conn, tx, detail, ct).ConfigureAwait(false))
                detail = null;
            return (header, detail, "PR");
        }

        if (IsPurchaseInvoiceType(docType))
        {
            var header = await FirstExistingConn(conn, tx, ["PH_IV", "PH_PI"], ct).ConfigureAwait(false);
            if (header is null)
                return (null, null, "PI");
            var detail = header == "PH_IV" ? "PH_IVDTL" : "PH_PIDTL";
            if (!await TableExistsConn(conn, tx, detail, ct).ConfigureAwait(false))
                detail = null;
            return (header, detail, "PI");
        }

        return resolved;
    }

    private static bool IsStockTransferType(string docType)
    {
        var t = docType.Trim().ToUpperInvariant();
        return t is "ST" or "STOCKTRANSFER";
    }

    private static bool IsPurchaseReturnType(string docType)
    {
        var t = docType.Trim().ToUpperInvariant();
        return t is "PR" or "PURCHASERETURN";
    }

    private static bool IsPurchaseInvoiceType(string docType)
    {
        var t = docType.Trim().ToUpperInvariant();
        return t is "PI" or "PURCHASEINVOICE";
    }
}
