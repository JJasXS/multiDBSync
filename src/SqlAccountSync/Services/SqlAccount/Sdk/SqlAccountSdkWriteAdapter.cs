using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// SDK Live write adapter implementing <see cref="ISqlAccountWriteAdapter"/>.
/// Company targeting uses <see cref="ISqlAccSessionManager"/> + SYNC_DATABASE <c>FbPath</c> (dynamic; not hardcoded).
/// No Firebird write fallback. REST adapter remains available via <c>SqlAccountingWrite:Provider=Rest</c>.
/// </summary>
public sealed class SqlAccountSdkWriteAdapter : ISqlAccountWriteAdapter
{
    private readonly ISqlAccSessionManager _sessions;
    private readonly ILogger<SqlAccountSdkWriteAdapter> _logger;

    public SqlAccountSdkWriteAdapter(ISqlAccSessionManager sessions, ILogger<SqlAccountSdkWriteAdapter> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<WriteResult> CreateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
            {
                var partner = BuildPartner(record, targetEntityCode, sourceDb, sourceEntityCode);
                var biz = PartyBiz(targetEntityCode);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertParty(native, biz, partner, record.Code, txnStamp);
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
            {
                var item = StockItemMapper.FromMasterRecord(record);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertStockItem(native, item, record.Code, txnStamp);
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            {
                var row = GlAccountMapper.FromMasterRecord(record);
                var destParent = ResolveDestParent(record);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertGlAccount(
                        native, row.Code, row.Description, destParent, 0,
                        row.Description2, row.AccType, row.SpecialAccType, row.Tax, row.CashFlowType, row.Sic,
                        false);
                }, cancellationToken).ConfigureAwait(false);
            }

            return RefuseMaster("CreateMaster", targetEntityCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK CreateMaster failed for {Entity} {Code}", targetEntityCode, record.Code);
            return new WriteResult { Success = false, Message = "SDK CreateMaster failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        var code = (record.Code ?? "").Trim();
        if (code.Length == 0)
            return new WriteResult { Success = false, Message = "Code is required for update." };

        try
        {
            if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
            {
                var partner = BuildPartner(record, targetEntityCode, sourceDb, sourceEntityCode);
                var biz = PartyBiz(targetEntityCode);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertParty(native, biz, partner, code, txnStamp);
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
            {
                var item = StockItemMapper.FromMasterRecord(record);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertStockItem(native, item, code, txnStamp);
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            {
                var row = GlAccountMapper.FromMasterRecord(record);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.UpsertGlAccount(
                        native, code, row.Description, destParentDocKey, destDocKey,
                        row.Description2, row.AccType, row.SpecialAccType, row.Tax, row.CashFlowType, row.Sic,
                        true);
                }, cancellationToken).ConfigureAwait(false);
            }

            return RefuseMaster("UpdateMaster", targetEntityCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK UpdateMaster failed for {Entity} {Code}", targetEntityCode, code);
            return new WriteResult { Success = false, Message = "SDK UpdateMaster failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code,
        CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return new WriteResult { Success = false, Message = "Code is required for delete." };

        try
        {
            if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
            {
                var biz = PartyBiz(targetEntityCode);
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.DeleteByCode(native, biz, key);
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
            {
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    // Wiki Del SKU uses Params Dockey = FindKeyByRef result.
                    return SqlAccBizObjectWriter.DeleteByCode(native, SqlAccBizObjectWriter.ItemBiz, key, "Dockey");
                }, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            {
                return await _sessions.RunAsync(db, app =>
                {
                    var native = SqlAccBizObjectWriter.NativeApp(app);
                    return SqlAccBizObjectWriter.DeleteGlAccount(native, key);
                }, cancellationToken).ConfigureAwait(false);
            }

            return RefuseMaster("DeleteMaster", targetEntityCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK DeleteMaster failed for {Entity} {Code}", targetEntityCode, key);
            return new WriteResult { Success = false, Message = "SDK DeleteMaster failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> CreateDocumentAsync(
        RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp,
        CancellationToken cancellationToken = default)
    {
        var biz = SqlAccBizObjectWriter.ResolveDocumentBizObject(apiPath);
        if (biz is null)
        {
            var detail = $"REST path '{apiPath}' has no verified SDK BizObject mapping.";
            _logger.LogWarning("SDK CreateDocument refused for path {Path}: {Detail}", apiPath, detail);
            return new WriteResult
            {
                Success = false,
                Message = SdkBizObjectCatalog.UnsupportedMessage("CreateDocument:" + apiPath, detail)
            };
        }

        try
        {
            return await _sessions.RunAsync(db, app =>
            {
                var native = SqlAccBizObjectWriter.NativeApp(app);
                return SqlAccBizObjectWriter.CreateDocument(native, biz, document, txnStamp);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK CreateDocument failed for {Path} {DocNo}", apiPath, document.DocNo);
            return new WriteResult { Success = false, Message = "SDK CreateDocument failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> CreateStockGroupAsync(
        RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
    {
        var code = (group.Code ?? "").Trim();
        if (code.Length == 0)
            return new WriteResult { Success = false, Message = "Stock group code is required." };

        try
        {
            return await _sessions.RunAsync(db, app =>
            {
                var native = SqlAccBizObjectWriter.NativeApp(app);
                return SqlAccBizObjectWriter.UpsertStockGroup(native, group);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK CreateStockGroup failed for {Code}", code);
            return new WriteResult { Success = false, Message = "SDK CreateStockGroup failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> UpdateStockGroupAsync(
        RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
    {
        var code = (group.Code ?? "").Trim();
        if (code.Length == 0)
            return new WriteResult { Success = false, Message = "Stock group code is required for update." };

        try
        {
            return await _sessions.RunAsync(db, app =>
            {
                var native = SqlAccBizObjectWriter.NativeApp(app);
                return SqlAccBizObjectWriter.UpdateStockGroup(native, group);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK UpdateStockGroup failed for {Code}", code);
            return new WriteResult { Success = false, Message = "SDK UpdateStockGroup failed: " + ex.Message };
        }
    }

    public async Task<WriteResult> DeleteStockGroupAsync(
        RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return new WriteResult { Success = false, Message = "Stock group code is required for delete." };

        try
        {
            return await _sessions.RunAsync(db, app =>
            {
                var native = SqlAccBizObjectWriter.NativeApp(app);
                return SqlAccBizObjectWriter.DeleteStockGroup(native, key);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK DeleteStockGroup failed for {Code}", key);
            return new WriteResult { Success = false, Message = "SDK DeleteStockGroup failed: " + ex.Message };
        }
    }

    private static int ResolveDestParent(MasterRecord record)
    {
        if (record.Fields.TryGetValue(GlAccountMapper.FieldDestParent, out var dp)
            && int.TryParse(dp, out var parsedParent))
            return parsedParent;
        return 0;
    }

    private static BusinessPartner BuildPartner(
        MasterRecord record, string targetEntityCode, RegisteredDatabase? sourceDb, string? sourceEntityCode)
    {
        if (sourceDb is not null && !string.IsNullOrWhiteSpace(sourceEntityCode))
            return BusinessPartnerMapper.FromMasterRecord(record, sourceDb, sourceEntityCode);

        return BusinessPartnerMapper.FromMasterRecord(
            record,
            sourceDb ?? new RegisteredDatabase(),
            string.IsNullOrWhiteSpace(sourceEntityCode) ? targetEntityCode : sourceEntityCode!);
    }

    private static string PartyBiz(string targetEntityCode) =>
        string.Equals(targetEntityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
            ? SqlAccBizObjectWriter.SupplierBiz
            : SqlAccBizObjectWriter.CustomerBiz;

    private WriteResult RefuseMaster(string op, string entity)
    {
        var capability = SdkBizObjectCatalog.Matrix.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.RestPathOrEntity)
            && c.RestPathOrEntity!.Contains(entity, StringComparison.OrdinalIgnoreCase));

        var detail = capability is null
            ? $"Entity '{entity}' has no verified SDK mapping."
            : $"{capability.SyncFeature}: status={capability.Status}. {capability.Notes}";

        _logger.LogWarning("SDK {Op} refused for {Entity}: {Detail}", op, entity, detail);
        return new WriteResult
        {
            Success = false,
            Message = SdkBizObjectCatalog.UnsupportedMessage(op + ":" + entity, detail)
        };
    }
}
