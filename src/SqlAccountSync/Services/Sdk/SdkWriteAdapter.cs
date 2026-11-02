using System.Text.Json;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Documents;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Sdk;

/// <summary>SQL Account SDK Live write path (replaces HTTP SigV4 API).</summary>
public sealed class SdkWriteAdapter : ISqlAccountWriteAdapter
{
    private readonly ISdkSessionManager _session;
    private readonly ILogger<SdkWriteAdapter> _logger;

    public SdkWriteAdapter(ISdkSessionManager session, ILogger<SdkWriteAdapter> logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<WriteResult> CreateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null,
        CancellationToken cancellationToken = default)
    {
        var bizName = SdkObjectMap.MasterBizObject(targetEntityCode);
        if (bizName.Length == 0)
            return Fail($"No SDK BizObject configured for {targetEntityCode}.");

        var payload = BuildMasterPayload(targetEntityCode, record, txnStamp, sourceDb, sourceEntityCode);
        try
        {
            return await _session.UseCompanyAsync(db.DbCode, biz =>
                SaveMaster(biz, bizName, payload, record.Code, isNew: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(ComHelper.FormatException(ex));
        }
    }

    public async Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        var code = (record.Code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required for update.");

        var bizName = SdkObjectMap.MasterBizObject(targetEntityCode);
        if (bizName.Length == 0)
            return Fail($"Update is not supported for {targetEntityCode}.");

        Dictionary<string, object?> payload;
        if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
        {
            payload = BuildMasterPayload(targetEntityCode, record, txnStamp, sourceDb, sourceEntityCode);
        }
        else if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var row = GlAccountMapper.FromMasterRecord(record);
            payload = GlAccountMapper.ToUpdatePayload(row, destParentDocKey, destDocKey);
        }
        else
        {
            return Fail($"Update is not supported for {targetEntityCode}.");
        }

        try
        {
            return await _session.UseCompanyAsync(db.DbCode, biz =>
                SaveMaster(biz, bizName, payload, code, isNew: false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(ComHelper.FormatException(ex));
        }
    }

    public async Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code,
        CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Fail("Code is required for delete.");

        var bizName = SdkObjectMap.MasterBizObject(targetEntityCode);
        if (bizName.Length == 0)
            return Fail($"Delete is not supported for {targetEntityCode}.");

        try
        {
            return await _session.UseCompanyAsync(db.DbCode, biz =>
            {
                dynamic? obj = null;
                try
                {
                    obj = biz.BizObjects.Find(bizName);
                    if (obj is null)
                        return Fail($"BizObject {bizName} not found.");

                    object? foundKey = null;
                    try { foundKey = obj.FindKeyByRef(key); }
                    catch { foundKey = null; }
                    if (foundKey is null)
                        return new WriteResult { Success = true, Message = "Already absent.", DocNo = key };

                    try
                    {
                        obj.Params.Find("DocKey").Value = foundKey;
                    }
                    catch
                    {
                        try { obj.Params.Find("Code").Value = key; }
                        catch { /* open by default */ }
                    }

                    obj.Open();
                    try
                    {
                        obj.Delete();
                        TrySave(obj);
                    }
                    finally
                    {
                        TryClose(obj);
                    }

                    return new WriteResult { Success = true, Message = "Deleted.", DocNo = key };
                }
                finally
                {
                    ComHelper.SafeRelease(obj);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var msg = ComHelper.FormatException(ex);
            if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("no record", StringComparison.OrdinalIgnoreCase))
                return new WriteResult { Success = true, Message = "Already absent.", DocNo = key };
            return Fail(msg);
        }
    }

    public async Task<WriteResult> CreateDocumentAsync(
        RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(apiPath) ? "/purchaseinvoice" : apiPath;
        var payload = BuildDocumentPayload(path, document, txnStamp);
        var bizName = SdkObjectMap.DocumentBizObject(path);

        try
        {
            return await _session.UseCompanyAsync(db.DbCode, biz =>
            {
                dynamic? obj = null;
                try
                {
                    obj = biz.BizObjects.Find(bizName);
                    if (obj is null)
                        return Fail($"BizObject {bizName} not found for path {path}.");

                    obj.New();
                    var main = obj.DataSets.Find(SdkObjectMap.MainDataSet);
                    SdkPayloadApplier.ApplyMain(main, payload);

                    var detailName = SdkObjectMap.DetailDataSetName(bizName);
                    var detailList = SdkPayloadApplier.FindNestedList(
                        payload, "sdsdocdetail", "cdsDocumentDetail", "detail");
                    if (detailList is System.Collections.IEnumerable rows)
                    {
                        dynamic? detail = null;
                        try
                        {
                            detail = TryFindDataSet(obj, detailName);
                            if (detail is not null)
                            {
                                var list = rows.Cast<object?>().ToList();
                                SdkPayloadApplier.ApplyDetailRows(detail, list);
                            }
                        }
                        finally
                        {
                            ComHelper.SafeRelease(detail);
                        }
                    }

                    TrySave(obj);
                    var readDocNo = (string)TryReadField(main, "DOCNO");
                    var docNo = readDocNo.Length > 0 ? readDocNo : (document.DocNo ?? "");
                    var docKey = (string)TryReadField(main, "DOCKEY");
                    TryClose(obj);
                    return new WriteResult
                    {
                        Success = true,
                        Message = "Created.",
                        DocNo = docNo,
                        DocKey = docKey
                    };
                }
                finally
                {
                    ComHelper.SafeRelease(obj);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(ComHelper.FormatException(ex));
        }
    }

    /// <summary>Create stock group via SDK (used by item prerequisites).</summary>
    public Task<WriteResult> CreateStockGroupAsync(
        RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = group.Code,
            ["description"] = string.IsNullOrWhiteSpace(group.Description) ? group.Code : group.Description
        };
        return CreateSimpleMasterAsync(db, SdkObjectMap.StockGroupBizObject, payload, group.Code, cancellationToken);
    }

    /// <summary>Create UOM via SDK when available.</summary>
    public Task<WriteResult> CreateUomAsync(
        RegisteredDatabase db, string uomCode, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = uomCode,
            ["description"] = uomCode,
            ["isactive"] = true
        };
        return CreateSimpleMasterAsync(db, SdkObjectMap.UomBizObject, payload, uomCode, cancellationToken);
    }

    private async Task<WriteResult> CreateSimpleMasterAsync(
        RegisteredDatabase db, string bizName, Dictionary<string, object?> payload, string code,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _session.UseCompanyAsync(db.DbCode, biz =>
                SaveMaster(biz, bizName, payload, code, isNew: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var msg = ComHelper.FormatException(ex);
            if (msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("already", StringComparison.OrdinalIgnoreCase))
                return new WriteResult { Success = true, Message = "Already exists.", DocNo = code };
            return Fail(msg);
        }
    }

    private WriteResult SaveMaster(
        dynamic biz, string bizName, Dictionary<string, object?> payload, string code, bool isNew)
    {
        dynamic? obj = null;
        try
        {
            obj = biz.BizObjects.Find(bizName);
            if (obj is null)
                return Fail($"BizObject {bizName} not found.");

            if (isNew)
            {
                obj.New();
            }
            else
            {
                object? foundKey = null;
                try { foundKey = obj.FindKeyByRef(code); }
                catch { foundKey = null; }
                if (foundKey is null)
                    return Fail($"Record {code} not found in {bizName}.");

                try { obj.Params.Find("DocKey").Value = foundKey; }
                catch
                {
                    try { obj.Params.Find("Code").Value = code; }
                    catch { /* fall through to Open */ }
                }
                obj.Open();
                try { obj.Edit(); }
                catch { /* some objects edit on Open */ }
            }

            var main = obj.DataSets.Find(SdkObjectMap.MainDataSet);
            SdkPayloadApplier.ApplyMain(main, payload);

            var branches = SdkPayloadApplier.FindNestedList(payload, "sdsbranch");
            if (branches is System.Collections.IEnumerable branchRows)
            {
                dynamic? branchDs = null;
                try
                {
                    branchDs = TryFindDataSet(obj, "cdsBranch", "BranchDataSet", "sdsbranch");
                    if (branchDs is not null)
                        SdkPayloadApplier.ApplyDetailRows(branchDs, branchRows.Cast<object?>());
                }
                finally
                {
                    ComHelper.SafeRelease(branchDs);
                }
            }

            var uoms = SdkPayloadApplier.FindNestedList(payload, "sdsitemuom");
            if (uoms is System.Collections.IEnumerable uomRows)
            {
                dynamic? uomDs = null;
                try
                {
                    uomDs = TryFindDataSet(obj, "cdsItemUom", "ItemUomDataSet", "sdsitemuom");
                    if (uomDs is not null)
                        SdkPayloadApplier.ApplyDetailRows(uomDs, uomRows.Cast<object?>());
                }
                finally
                {
                    ComHelper.SafeRelease(uomDs);
                }
            }

            TrySave(obj);
            TryClose(obj);
            return new WriteResult
            {
                Success = true,
                Message = isNew ? "Created." : "Updated.",
                DocNo = code,
                DocKey = code
            };
        }
        finally
        {
            ComHelper.SafeRelease(obj);
        }
    }

    private static Dictionary<string, object?> BuildMasterPayload(
        string targetEntityCode, MasterRecord record, string txnStamp,
        RegisteredDatabase? sourceDb, string? sourceEntityCode)
    {
        if (PartnerSyncRules.IsPartyEntity(targetEntityCode) && sourceDb is not null
            && !string.IsNullOrWhiteSpace(sourceEntityCode))
        {
            var partner = BusinessPartnerMapper.FromMasterRecord(record, sourceDb, sourceEntityCode);
            return BusinessPartnerMapper.ToApiPayload(partner, targetEntityCode, record.Code, txnStamp);
        }

        if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
        {
            return BusinessPartnerMapper.ToApiPayload(
                new BusinessPartner { CompanyName = record.Description, SourceCode = record.Code, SourceType = targetEntityCode },
                targetEntityCode, record.Code, txnStamp);
        }

        if (string.Equals(targetEntityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
        {
            var item = StockItemMapper.FromMasterRecord(record);
            return StockItemMapper.ToApiPayload(item, record.Code, txnStamp);
        }

        if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var row = GlAccountMapper.FromMasterRecord(record);
            var destParent = 0;
            if (record.Fields.TryGetValue(GlAccountMapper.FieldDestParent, out var dp)
                && int.TryParse(dp, out var parsedParent))
                destParent = parsedParent;
            return GlAccountMapper.ToApiPayload(row, destParent);
        }

        return new Dictionary<string, object?>
        {
            ["code"] = record.Code,
            ["description"] = record.Description,
            ["udf_txnid"] = txnStamp
        };
    }

    private static Dictionary<string, object?> BuildDocumentPayload(
        string path, DocumentHeader document, string txnStamp)
    {
        if (path.Contains("stocktransfer", StringComparison.OrdinalIgnoreCase))
            return StockTransferMapper.ToApiPayload(document, txnStamp);
        if (path.Contains("salesdebitnote", StringComparison.OrdinalIgnoreCase))
            return SalesNoteMapper.ToDebitNotePayload(document, txnStamp);
        if (path.Contains("salescreditnote", StringComparison.OrdinalIgnoreCase))
            return SalesNoteMapper.ToCreditNotePayload(document, txnStamp);
        if (path.Contains("purchasereturned", StringComparison.OrdinalIgnoreCase))
            return PurchaseReturnMapper.ToApiPayload(document, txnStamp);
        if (path.Contains("goodsreceived", StringComparison.OrdinalIgnoreCase))
            return GoodsReceivedMapper.ToApiPayload(document, txnStamp);
        if (path.Contains("purchaseinvoice", StringComparison.OrdinalIgnoreCase)
            || path.Contains("purchase", StringComparison.OrdinalIgnoreCase))
            return PurchaseInvoiceMapper.ToApiPayload(document, txnStamp);

        return new Dictionary<string, object?>
        {
            ["docno"] = document.DocNo,
            ["docdate"] = document.DocDate?.ToString("yyyy-MM-dd"),
            ["code"] = document.PartyCode,
            ["companyname"] = document.PartyName,
            ["docamt"] = document.Amount,
            ["currencycode"] = document.Currency,
            ["note"] = string.IsNullOrWhiteSpace(txnStamp) ? null : $"SYNC|{txnStamp}",
            ["sdsdocdetail"] = document.Lines.Select(l => new Dictionary<string, object?>
            {
                ["itemcode"] = l.ItemCode,
                ["description"] = l.Description,
                ["qty"] = l.Qty,
                ["unitprice"] = l.UnitPrice,
                ["amount"] = l.Amount,
                ["tax"] = l.TaxCode,
                ["location"] = l.Warehouse,
                ["uom"] = l.Uom
            }).ToList()
        };
    }

    private static dynamic? TryFindDataSet(dynamic obj, params string[] names)
    {
        foreach (var name in names.Concat(ComHelper.UnverifiedDetailDataSetNames))
        {
            try
            {
                var ds = obj.DataSets.Find(name);
                if (ds is not null)
                    return ds;
            }
            catch
            {
                // try next
            }
        }
        return null;
    }

    private static void TrySave(dynamic obj)
    {
        try { obj.Save(); return; }
        catch
        {
            try { obj.Post(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("SDK Save/Post failed: " + ComHelper.FormatException(ex), ex);
            }
        }
    }

    private static void TryClose(dynamic obj)
    {
        try { obj.Close(); }
        catch { /* ignore */ }
    }

    private static string TryReadField(dynamic dataSet, string fieldName)
    {
        try
        {
            var field = dataSet.FindField(fieldName);
            if (field is null) return "";
            try { return Convert.ToString(field.AsString) ?? ""; }
            catch
            {
                try { return Convert.ToString(field.Value) ?? ""; }
                catch { return ""; }
            }
        }
        catch
        {
            return "";
        }
    }

    private WriteResult Fail(string message)
    {
        _logger.LogWarning("SDK write failed: {Message}", message);
        return new WriteResult { Success = false, Message = message };
    }
}
