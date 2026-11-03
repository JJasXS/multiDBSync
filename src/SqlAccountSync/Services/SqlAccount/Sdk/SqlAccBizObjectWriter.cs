using System.Globalization;
using System.Text;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// SDK Live BizObject write helpers (AR_CUSTOMER, AP_SUPPLIER, ST_ITEM, GL_ACC, ST_GROUP, PH_GR, PH_PI, PH_SC, ST_XF, SL_CN, SL_DN).
/// Uses New/Edit/Save/Delete + FindKeyByRef lifecycle. Field names from COM metadata / wiki samples. No Firebird DML.
/// </summary>
public static class SqlAccBizObjectWriter
{
    public const string CustomerBiz = "AR_CUSTOMER";
    public const string SupplierBiz = "AP_SUPPLIER";
    public const string ItemBiz = "ST_ITEM";
    public const string AccountBiz = "GL_ACC";
    public const string StockGroupBiz = "ST_GROUP";
    public const string GoodsReceivedBiz = "PH_GR";
    public const string PurchaseInvoiceBiz = "PH_PI";
    public const string PurchaseReturnBiz = "PH_SC";
    public const string StockTransferBiz = "ST_XF";
    public const string SalesCreditNoteBiz = "SL_CN";
    public const string SalesDebitNoteBiz = "SL_DN";

    /// <summary>
    /// Firebird <c>GL_ACC.CODE</c> CHARACTER_LENGTH on project companies (MIRACLE/ONYAT verified).
    /// COM XMLFieldDefs WIDTH="20" is not the character limit — do not use it for validation.
    /// </summary>
    public const int GlAccountCodeMaxLength = 10;

    public static string? ResolveDocumentBizObject(string apiPath)
    {
        var path = (apiPath ?? "").Trim().Trim('/').ToLowerInvariant();
        if (path.Contains("goodsreceived", StringComparison.Ordinal))
            return GoodsReceivedBiz;
        if (path.Contains("stocktransfer", StringComparison.Ordinal))
            return StockTransferBiz;
        if (path.Contains("salesdebitnote", StringComparison.Ordinal))
            return SalesDebitNoteBiz;
        if (path.Contains("salescreditnote", StringComparison.Ordinal))
            return SalesCreditNoteBiz;
        if (path.Contains("purchasereturned", StringComparison.Ordinal) || path.Contains("purchasereturn", StringComparison.Ordinal))
            return PurchaseReturnBiz;
        if (path.Contains("purchaseinvoice", StringComparison.Ordinal) || path is "purchase" or "")
            return PurchaseInvoiceBiz;
        return null;
    }

    public static WriteResult UpsertParty(dynamic app, string bizObjectName, BusinessPartner partner, string code, string txnStamp)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required.");

        // Resolve ControlAccount BEFORE New/Open. Mid-edit NewDataSet / GL_ACC FindKey cancels the
        // pending AP/AR insert (live MIRACLE: Save "Created." but SDKPOC-S3517 never hit Firebird).
        var control = ResolveControlAccount(app, bizObjectName, partner.ControlAccount);
        if (control is null)
        {
            return Fail(
                "ControlAccount is required for " + bizObjectName +
                " and could not be resolved from the destination company. " +
                "Provide a mapped ControlAccount or ensure at least one existing " +
                (string.Equals(bizObjectName, CustomerBiz, StringComparison.OrdinalIgnoreCase) ? "customer" : "supplier") +
                " has a valid ControlAccount that exists in GL_ACC.");
        }

        dynamic biz = app.BizObjects.Find(bizObjectName);
        dynamic main = biz.DataSets.Find("MainDataSet");
        dynamic branch = biz.DataSets.Find("cdsBranch");
        var key = FindKey(biz, "CODE", code);
        var isNew = key is null;

        if (isNew)
            biz.New();
        else
        {
            OpenPartyByKey(biz, key, code);
            biz.Edit();
            ClearDetailRows(branch);
        }

        Set(main, "CODE", code);
        Set(main, "CompanyName", NullIfEmpty(partner.CompanyName) ?? code);
        SetIf(main, "CompanyName2", partner.CompanyName2);
        Set(main, "ControlAccount", control);
        Set(main, "Area", NullIfEmpty(partner.Area) ?? "----");
        Set(main, "Agent", NullIfEmpty(partner.Agent) ?? "----");
        Set(main, "CreditTerm", NullIfEmpty(partner.CreditTerm) ?? "30 Days");
        Set(main, "CurrencyCode", NullIfEmpty(partner.CurrencyCode) ?? "----");
        Set(main, "Status", NullIfEmpty(partner.Status) ?? "A");
        SetIf(main, "CompanyCategory", partner.CompanyCategory);
        SetIf(main, "BizNature", partner.BizNature);
        SetIf(main, "CreditLimit", partner.CreditLimit);
        SetIf(main, "OverdueLimit", partner.OverdueLimit);
        SetIf(main, "StatementType", partner.StatementType);
        SetIf(main, "Tax", partner.Tax);
        SetIf(main, "TaxExemptNo", partner.TaxExemptNo);
        SetIf(main, "BRN", partner.Brn);
        SetIf(main, "BRN2", partner.Brn2);
        SetIf(main, "GSTNo", partner.GstNo);
        SetIf(main, "SalesTaxNo", partner.SalesTaxNo);
        SetIf(main, "ServiceTaxNo", partner.ServiceTaxNo);
        SetIf(main, "TIN", partner.Tin);
        SetIf(main, "IDNO", partner.IdNo);
        SetIf(main, "SIC", partner.Sic);
        SetIf(main, "IRBM_CLASSIFICATION", partner.IrbmClassification);
        SetIf(main, "BusinessUnit", partner.BusinessUnit);
        SetIf(main, "TaxArea", partner.TaxArea);
        SetIf(main, "Note", partner.Note);

        var remark = NullIfEmpty(partner.Remark) ?? "";
        if (!string.IsNullOrWhiteSpace(txnStamp))
            remark = string.IsNullOrWhiteSpace(remark) ? "SYNC|" + txnStamp : remark + " | SYNC|" + txnStamp;
        SetIf(main, "Remark", remark);

        var branches = partner.Branches.Count > 0 ? partner.Branches : [new PartnerBranch()];
        var first = true;
        foreach (var b in branches)
        {
            if (isNew && first)
                branch.Edit();
            else
                branch.Append();
            first = false;

            Set(branch, "BranchType", NullIfEmpty(b.BranchType) ?? "B");
            Set(branch, "BranchName", NullIfEmpty(b.BranchName) ?? "BILLING");
            SetIf(branch, "Address1", b.Address1);
            SetIf(branch, "Address2", b.Address2);
            SetIf(branch, "Address3", b.Address3);
            SetIf(branch, "Address4", b.Address4);
            SetIf(branch, "PostCode", b.Postcode);
            SetIf(branch, "City", b.City);
            SetIf(branch, "State", b.State);
            Set(branch, "Country", NullIfEmpty(b.Country) ?? "MY");
            SetIf(branch, "Attention", b.Attention);
            SetIf(branch, "Phone1", b.Phone1);
            SetIf(branch, "Phone2", b.Phone2);
            SetIf(branch, "Mobile", b.Mobile);
            SetIf(branch, "Fax1", b.Fax1);
            SetIf(branch, "Fax2", b.Fax2);
            SetIf(branch, "Email", b.Email);
            branch.Post();
        }

        var saved = SaveClose(biz, isNew ? "Created." : "Updated.", code, main);
        if (!saved.Success)
            return saved;

        // Never trust Save() alone — confirm the master row is visible after Close.
        var persisted = LoadPartyPersistence(app, bizObjectName, code);
        var presenceError = ValidatePostSaveMasterPresence(code, persisted?.Code);
        if (presenceError is not null)
            return Fail(presenceError);

        var controlError = ValidatePostSaveControlAccount(control, persisted?.ControlAccount);
        if (controlError is not null)
            return Fail(controlError);

        saved.DocNo = persisted!.Code;
        if (!string.IsNullOrWhiteSpace(persisted.ControlAccount))
            saved.Message = (isNew ? "Created." : "Updated.") + " ControlAccount=" + persisted.ControlAccount;
        return saved;
    }

    public static WriteResult DeleteByCode(dynamic app, string bizObjectName, string code, string paramName = "CODE")
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required for delete.");

        dynamic biz = app.BizObjects.Find(bizObjectName);
        var key = FindKey(biz, "CODE", code);
        if (key is null)
            return new WriteResult { Success = true, Message = "Already absent.", DocNo = code };

        biz.Params.Find(paramName).Value = key;
        biz.Open();
        biz.Delete();
        try { biz.Close(); } catch { /* ignore */ }
        return new WriteResult { Success = true, Message = "Deleted.", DocNo = code };
    }

    public static WriteResult UpsertStockItem(dynamic app, StockItem item, string code, string txnStamp)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required.");

        dynamic biz = app.BizObjects.Find(ItemBiz);
        dynamic main = biz.DataSets.Find("MainDataSet");
        dynamic uom = biz.DataSets.Find("cdsUOM");
        var key = FindKey(biz, "CODE", code);
        var isNew = key is null;

        if (isNew)
            biz.New();
        else
        {
            // Wiki ST_ITEM edit uses Params Dockey = FindKeyByRef result.
            try { biz.Params.Find("Dockey").Value = key; }
            catch { biz.Params.Find("Code").Value = code; }
            biz.Open();
            biz.Edit();
            ClearDetailRows(uom);
        }

        Set(main, "CODE", code);
        Set(main, "DESCRIPTION", NullIfEmpty(item.Description) ?? code);
        SetIf(main, "DESCRIPTION2", item.Description2);
        Set(main, "STOCKGROUP", NullIfEmpty(item.StockGroup) ?? "DEFAULT");
        Set(main, "STOCKCONTROL", item.StockControl ? "T" : "F");
        Set(main, "ISACTIVE", item.IsActive ? "T" : "F");
        SetIf(main, "SHELF", item.Shelf);
        SetIf(main, "REMARK1", item.Remark1);
        SetIf(main, "REMARK2", string.IsNullOrWhiteSpace(txnStamp)
            ? item.Remark2
            : (string.IsNullOrWhiteSpace(item.Remark2) ? "SYNC|" + txnStamp : item.Remark2 + " | SYNC|" + txnStamp));
        SetIf(main, "SLTAX", item.SlTax);
        SetIf(main, "PHTAX", item.PhTax);
        SetIf(main, "TARIFF", item.Tariff);
        SetIf(main, "IRBM_CLASSIFICATION", item.IrbmClassification);

        var uoms = item.Uoms.Count > 0
            ? item.Uoms
            : [new StockItemUom { Uom = NullIfEmpty(item.Uom) ?? "PCS", Rate = "1", IsBase = true, RefCost = item.RefCost, RefPrice = item.RefPrice }];

        var first = true;
        foreach (var row in uoms)
        {
            if (isNew && first)
                uom.Edit();
            else
                uom.Append();
            first = false;

            Set(uom, "UOM", NullIfEmpty(row.Uom) ?? "PCS");
            SetFloat(uom, "Rate", ParseDouble(row.Rate, 1));
            SetFloat(uom, "RefCost", ParseDouble(row.RefCost, 0));
            SetFloat(uom, "RefPrice", ParseDouble(row.RefPrice, 0));
            if (!isNew)
                SetFloat(uom, "ISBASE", row.IsBase ? 1 : 0);
            uom.Post();
        }

        return SaveClose(biz, isNew ? "Created." : "Updated.", code, main);
    }

    /// <summary>
    /// Maintain Account via <c>GL_ACC</c> MainDataSet.
    /// <paramref name="destParentDocKey"/> must be the destination-company Parent DocKey (never source Parent).
    /// <paramref name="destDocKey"/> is required for update (destination DocKey); ignored on create.
    /// </summary>
    public static WriteResult UpsertGlAccount(
        dynamic app, string code, string description, int destParentDocKey, int destDocKey,
        string? description2, string? accType, string? specialAccType, string? tax, int cashFlowType, string? sic,
        bool isUpdate)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required.");

        dynamic biz = FindBiz(app, AccountBiz, "GL_Acc");
        dynamic main = biz.DataSets.Find("MainDataSet");
        var maxLen = ResolveGlAccountCodeMaxLength(main);

        string effectiveCode;
        if (isUpdate)
        {
            var sqlHit = LoadGlPersistence(
                app,
                code,
                destDocKey > 0 ? destDocKey.ToString(CultureInfo.InvariantCulture) : null);
            if (sqlHit is null)
                return Fail($"Account '{code}' was not found for update.");
            if (destDocKey > 0 && sqlHit.DocKey != destDocKey)
            {
                return Fail(
                    "Refusing GL update: SQL CODE='" + sqlHit.Code + "' DOCKEY=" + sqlHit.DocKey +
                    " does not match expected DOCKEY=" + destDocKey + ".");
            }

            if (!TryOpenGlAccount((object)biz, sqlHit.DocKey, sqlHit.Code, out string? openError))
                return Fail(openError ?? $"Account '{code}' could not be opened for update.");
            var openedKey = ReadStringField(main, "DOCKEY");
            var openedCode = ReadStringField(main, "CODE");
            int openedDk;
            _ = int.TryParse(openedKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out openedDk);
            var identityError = ValidateOpenedGlIdentity(sqlHit.DocKey, sqlHit.Code, openedDk, openedCode);
            if (identityError is not null)
            {
                try { biz.Close(); } catch { /* ignore */ }
                return Fail(identityError);
            }

            biz.Edit();
            // Identity is DOCKEY. Never rewrite CODE with a longer value (Firebird truncates → collisions).
            effectiveCode = openedCode;
            if (string.IsNullOrWhiteSpace(effectiveCode))
                effectiveCode = sqlHit.Code;
        }
        else
        {
            var lengthError = ValidateGlAccountCodeLength(code, maxLen);
            if (lengthError is not null)
                return Fail(lengthError);

            var existing = FindKey(biz, "CODE", code);
            if (existing is not null)
                return Fail($"Account '{code}' already exists; refusing create overwrite.");
            biz.New();
            effectiveCode = code;
        }

        // DOCKEY is system-generated on create; never copy source DocKey into destination.
        SetInt(main, "PARENT", destParentDocKey);
        Set(main, "CODE", effectiveCode);
        Set(main, "DESCRIPTION", NullIfEmpty(description) ?? effectiveCode);
        Set(main, "DESCRIPTION2", description2 ?? "");
        Set(main, "ACCTYPE", accType ?? "");
        Set(main, "SPECIALACCTYPE", specialAccType ?? "");
        Set(main, "TAX", tax ?? "");
        SetInt(main, "CASHFLOWTYPE", cashFlowType);
        Set(main, "SIC", sic ?? "");

        try
        {
            biz.Save();
        }
        catch (Exception ex)
        {
            try { biz.Close(); } catch { /* ignore */ }
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }

        // Capture in-memory identity before Close, then confirm via SELECT (FindKey/Open alone is unreliable).
        var persistedCode = ReadStringField(main, "CODE");
        if (string.IsNullOrWhiteSpace(persistedCode))
            persistedCode = effectiveCode;
        else
            persistedCode = persistedCode.Trim();

        var persistedKey = ReadStringField(main, "DOCKEY");
        try { biz.Close(); } catch { /* ignore */ }

        if (!isUpdate
            && !string.Equals(persistedCode, code, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "GL account CODE was truncated or altered on save (requested '" + code +
                "', persisted '" + persistedCode + "', DOCKEY=" + persistedKey + "). " +
                "Use a CODE of at most " + maxLen +
                " characters. Refusing to treat this as success to avoid collisions.");
        }

        var confirmed = LoadGlPersistence(app, persistedCode, persistedKey);
        var presenceError = ValidatePostSaveMasterPresence(persistedCode, confirmed?.Code);
        if (presenceError is not null)
            return Fail(presenceError + " DOCKEY=" + persistedKey + ".");

        return new WriteResult
        {
            Success = true,
            Message = isUpdate ? "Updated." : "Created.",
            DocNo = confirmed!.Code,
            DocKey = confirmed.DocKey > 0
                ? confirmed.DocKey.ToString(CultureInfo.InvariantCulture)
                : (string.IsNullOrWhiteSpace(persistedKey) ? persistedCode : persistedKey.Trim())
        };
    }

    /// <summary>
    /// Returns null when <paramref name="code"/> fits <paramref name="maxLength"/>; otherwise an error message.
    /// </summary>
    public static string? ValidateGlAccountCodeLength(string? code, int maxLength = GlAccountCodeMaxLength)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0)
            return "Code is required.";
        if (maxLength <= 0)
            maxLength = GlAccountCodeMaxLength;
        if (c.Length > maxLength)
        {
            return "GL account CODE '" + c + "' length " + c.Length +
                   " exceeds GL_ACC.CODE limit of " + maxLength +
                   " characters (Firebird CHARACTER_LENGTH). Shorten the code to avoid truncation and duplicate collisions.";
        }

        return null;
    }

    /// <summary>
    /// Chooses ControlAccount: explicit partner mapping wins; otherwise first valid control from dest company parties.
    /// Returns null when nothing valid can be resolved (caller must fail — never guess 300-000/400-000).
    /// </summary>
    public static string? ChooseControlAccount(string? providedControlAccount, string? resolvedFromDestination)
    {
        var provided = NullIfEmpty(providedControlAccount);
        if (provided is not null)
            return provided;
        return NullIfEmpty(resolvedFromDestination);
    }

    /// <summary>
    /// Returns an error when Save claimed success but the master CODE is not visible after Close.
    /// </summary>
    public static string? ValidatePostSaveMasterPresence(string? requestedCode, string? foundCode)
    {
        var requested = (requestedCode ?? "").Trim();
        var found = (foundCode ?? "").Trim();
        if (found.Length == 0)
        {
            return "Save reported success but CODE '" + requested +
                   "' was not found after Close. Treating as failed persistence (no Firebird row).";
        }

        if (requested.Length > 0
            && !string.Equals(requested, found, StringComparison.OrdinalIgnoreCase))
        {
            return "Save reported success but persisted CODE '" + found +
                   "' does not match requested '" + requested + "'.";
        }

        return null;
    }

    /// <summary>Returns an error when persisted ControlAccount does not match the resolved destination value.</summary>
    public static string? ValidatePostSaveControlAccount(string? expectedControl, string? foundControl)
    {
        var expected = (expectedControl ?? "").Trim();
        var found = (foundControl ?? "").Trim();
        if (expected.Length == 0)
            return "ControlAccount was not resolved before save.";
        if (found.Length == 0)
            return "Save reported success but ControlAccount was empty on the persisted party row.";
        if (!string.Equals(expected, found, StringComparison.OrdinalIgnoreCase))
        {
            return "Persisted ControlAccount '" + found + "' does not match resolved '" + expected + "'.";
        }

        return null;
    }

    /// <summary>
    /// Confirms an opened GL row matches the intended DOCKEY/CODE (guards Params Open opening the wrong row).
    /// </summary>
    public static string? ValidateOpenedGlIdentity(
        int expectedDocKey, string? expectedCode, int openedDocKey, string? openedCode)
    {
        var code = (expectedCode ?? "").Trim();
        var opened = (openedCode ?? "").Trim();
        if (expectedDocKey > 0 && openedDocKey > 0 && expectedDocKey != openedDocKey)
        {
            return "Opened GL DOCKEY " + openedDocKey + " does not match expected " + expectedDocKey +
                   " (CODE='" + opened + "').";
        }

        if (code.Length > 0 && opened.Length > 0
            && !string.Equals(code, opened, StringComparison.OrdinalIgnoreCase))
        {
            return "Opened GL CODE '" + opened + "' does not match expected '" + code + "'.";
        }

        if (code.Length > 0 && opened.Length == 0)
            return "Opened GL row has empty CODE; refusing mutation.";

        if (openedDocKey <= 0 && opened.Length == 0)
            return "GL Open succeeded but MainDataSet identity (DOCKEY/CODE) was empty.";

        return null;
    }

    /// <summary>
    /// Returns an error when a GL account still has child rows (never delete parents/referenced accounts).
    /// </summary>
    public static string? ValidateGlDeleteHasNoChildren(string? code, int docKey, int childCount)
    {
        if (childCount <= 0)
            return null;
        return "Refusing delete of GL account CODE='" + (code ?? "").Trim() +
               "' DOCKEY=" + docKey + " because it has " + childCount +
               " child account(s). Delete children first.";
    }

    /// <summary>
    /// Deletes a GL account only after SQL identity resolution, Open identity check, and child-count guard.
    /// <paramref name="expectedDocKey"/> when &gt; 0 must match the opened row (prevents wrong-row deletes).
    /// </summary>
    public static WriteResult DeleteGlAccount(dynamic app, string code, int expectedDocKey = 0)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Code is required for delete.");

        var sqlHit = LoadGlPersistence(
            app,
            code,
            expectedDocKey > 0 ? expectedDocKey.ToString(CultureInfo.InvariantCulture) : null);
        if (sqlHit is null)
            return new WriteResult { Success = true, Message = "Already absent.", DocNo = code };

        if (expectedDocKey > 0 && sqlHit.DocKey != expectedDocKey)
        {
            return Fail(
                "Refusing GL delete: SQL CODE='" + sqlHit.Code + "' DOCKEY=" + sqlHit.DocKey +
                " does not match expected DOCKEY=" + expectedDocKey + ".");
        }

        if (!string.Equals(sqlHit.Code, code, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "Refusing GL delete: SQL CODE='" + sqlHit.Code + "' does not match requested '" + code + "'.");
        }

        var childCount = CountGlChildren(app, sqlHit.DocKey);
        var childError = ValidateGlDeleteHasNoChildren(sqlHit.Code, sqlHit.DocKey, childCount);
        if (childError is not null)
            return Fail(childError);

        dynamic biz = FindBiz(app, AccountBiz, "GL_Acc");
        if (!TryOpenGlAccount((object)biz, sqlHit.DocKey, sqlHit.Code, out string? openError))
            return Fail(openError ?? "Account could not be opened for delete.");

        dynamic main = biz.DataSets.Find("MainDataSet");
        var openedCode = ReadStringField(main, "CODE");
        var openedKeyText = ReadStringField(main, "DOCKEY");
        int openedDk;
        _ = int.TryParse(openedKeyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out openedDk);
        var identityError = ValidateOpenedGlIdentity(sqlHit.DocKey, sqlHit.Code, openedDk, openedCode);
        if (identityError is not null)
        {
            try { biz.Close(); } catch { /* ignore */ }
            return Fail("Refusing GL delete: " + identityError);
        }

        try
        {
            biz.Delete();
        }
        catch (Exception ex)
        {
            try { biz.Close(); } catch { /* ignore */ }
            return Fail("Account delete refused by SDK/accounting rules: " + ex.Message);
        }

        try { biz.Close(); } catch { /* ignore */ }

        var stillThere = LoadGlPersistence(app, code, sqlHit.DocKey.ToString(CultureInfo.InvariantCulture));
        if (stillThere is not null)
        {
            return Fail(
                "Delete reported success but GL CODE='" + code + "' DOCKEY=" + sqlHit.DocKey +
                " is still present.");
        }

        return new WriteResult
        {
            Success = true,
            Message = "Deleted.",
            DocNo = code,
            DocKey = sqlHit.DocKey.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Maintain Stock Group via <c>ST_GROUP</c> (not <c>ST_CATEGORY</c>).</summary>
    public static WriteResult UpsertStockGroup(dynamic app, StockGroupRecord group)
    {
        var code = (group.Code ?? "").Trim();
        if (code.Length == 0)
            return Fail("Stock group code is required.");

        dynamic biz = app.BizObjects.Find(StockGroupBiz);
        dynamic main = biz.DataSets.Find("MainDataSet");
        var existing = FindKey(biz, "CODE", code);
        if (existing is not null)
        {
            return new WriteResult
            {
                Success = true,
                Message = "Stock group already exists.",
                DocNo = code,
                DocKey = code
            };
        }

        biz.New();
        Set(main, "CODE", code);
        Set(main, "DESCRIPTION", NullIfEmpty(group.Description) ?? code);
        Set(main, "SALES", group.Sales ?? "");
        Set(main, "SALESRETURNED", group.SalesReturned ?? "");
        Set(main, "CASHSALES", group.CashSales ?? "");
        Set(main, "PURCHASE", group.Purchase ?? "");
        Set(main, "CASHPURCHASE", group.CashPurchase ?? "");
        Set(main, "PURCHASERETURNED", group.PurchaseReturned ?? "");
        Set(main, "BALANCESTOCK", group.BalanceStock ?? "");
        SetInt(main, "COSTINGMETHOD", group.CostingMethod <= 0 ? 1 : group.CostingMethod);
        SetBool(main, "ISACTIVE", group.IsActive);

        return SaveClose(biz, "Created.", code, main);
    }

    public static WriteResult CreateDocument(dynamic app, string bizObjectName, DocumentHeader document, string txnStamp)
    {
        if (string.Equals(bizObjectName, GoodsReceivedBiz, StringComparison.OrdinalIgnoreCase))
            return CreateGoodsReceived(app, document, txnStamp);

        var docNo = (document.DocNo ?? "").Trim();
        if (docNo.Length == 0)
            docNo = "--SYNC--";

        dynamic biz = app.BizObjects.Find(bizObjectName);
        dynamic main = biz.DataSets.Find("MainDataSet");
        dynamic detail = biz.DataSets.Find("cdsDocDetail");

        var party = (document.PartyCode ?? "").Trim();
        object? existing = null;
        if (string.Equals(bizObjectName, PurchaseInvoiceBiz, StringComparison.OrdinalIgnoreCase)
            || string.Equals(bizObjectName, PurchaseReturnBiz, StringComparison.OrdinalIgnoreCase))
        {
            // Wiki PH_PI: FindKeyByRef("DocNo;Code", [docNo, code])
            existing = FindKeyComposite(biz, "DocNo;Code", new object[] { docNo, party });
        }
        else
        {
            existing = FindKey(biz, "DocNo", docNo);
        }

        if (existing is not null)
            return Fail($"Document '{docNo}' already exists in {bizObjectName}; refusing overwrite.");

        biz.New();
        var docDate = document.DocDate ?? DateTime.Today;
        var postDate = document.PostDate ?? docDate;
        Set(main, "DocNo", docNo);
        Set(main, "DocDate", docDate);
        Set(main, "PostDate", postDate);
        SetIf(main, "Code", party);
        SetIf(main, "CompanyName", document.PartyName);
        Set(main, "Description", NullIfEmpty(document.Description)
                                 ?? (string.IsNullOrWhiteSpace(txnStamp) ? bizObjectName : "SYNC|" + txnStamp));
        SetIf(main, "Area", document.Area);
        SetIf(main, "Agent", document.Agent);
        SetIf(main, "Project", document.Project);
        SetIf(main, "Terms", document.Terms);
        SetIf(main, "CurrencyCode", document.Currency);
        SetIf(main, "Note", string.IsNullOrWhiteSpace(txnStamp) ? document.Note : $"SYNC|{txnStamp}");

        var seq = 0;
        foreach (var line in document.Lines)
        {
            seq++;
            detail.Append();
            Set(detail, "Seq", line.Seq > 0 ? line.Seq : seq);
            SetIf(detail, "ItemCode", line.ItemCode);
            SetIf(detail, "Description", line.Description);
            SetIf(detail, "Description2", line.Description2);
            SetFloat(detail, "Qty", (double)line.Qty);
            Set(detail, "UOM", NullIfEmpty(line.Uom) ?? "PCS");
            SetFloat(detail, "UnitPrice", (double)line.UnitPrice);
            SetFloat(detail, "Amount", (double)(line.Amount != 0 ? line.Amount : line.Qty * line.UnitPrice));
            SetIf(detail, "Tax", line.TaxCode);
            Set(detail, "TaxInclusive", line.TaxInclusive ? 1 : 0);
            SetFloat(detail, "TaxAmt", 0);
            SetIf(detail, "Account", line.Account);
            SetIf(detail, "DISC", line.Disc);
            SetIf(detail, "Project", line.Project);
            if (string.Equals(bizObjectName, StockTransferBiz, StringComparison.OrdinalIgnoreCase))
            {
                Set(detail, "FromLocation", NullIfEmpty(line.FromLocation) ?? NullIfEmpty(line.Warehouse) ?? "----");
                Set(detail, "ToLocation", NullIfEmpty(line.ToLocation) ?? "----");
            }
            else
            {
                SetIf(detail, "Location", NullIfEmpty(line.Warehouse) ?? "----");
            }

            detail.Post();
        }

        return SaveClose(biz, "Created.", docNo, main);
    }

    /// <summary>
    /// Goods Received via <c>PH_GR</c> MainDataSet + cdsDocDetail (+ empty cdsSerialNumber).
    /// Does not set FROM* keys from source-company DocKeys; only explicit destination-side link fields on the line.
    /// </summary>
    public static WriteResult CreateGoodsReceived(dynamic app, DocumentHeader document, string txnStamp)
    {
        var docNo = (document.DocNo ?? "").Trim();
        if (docNo.Length == 0)
            docNo = "--";

        dynamic biz = app.BizObjects.Find(GoodsReceivedBiz);
        dynamic main = biz.DataSets.Find("MainDataSet");
        dynamic detail = biz.DataSets.Find("cdsDocDetail");

        if (docNo is not "--" and not "--SYNC--")
        {
            var existing = FindKey(biz, "DocNo", docNo);
            if (existing is not null)
                return Fail($"Document '{docNo}' already exists in {GoodsReceivedBiz}; refusing overwrite.");
        }

        biz.New();
        var docDate = document.DocDate ?? DateTime.Today;
        var postDate = document.PostDate ?? docDate;
        var taxDate = document.TaxDate ?? docDate;
        var party = (document.PartyCode ?? "").Trim();
        var docAmt = (double)(document.Amount != 0
            ? document.Amount
            : document.Lines.Sum(l => l.Amount != 0 ? l.Amount : l.Qty * l.UnitPrice));

        Set(main, "DocNo", docNo);
        Set(main, "DocDate", docDate);
        Set(main, "PostDate", postDate);
        Set(main, "TaxDate", taxDate);
        SetIf(main, "Code", party);
        SetIf(main, "CompanyName", document.PartyName);
        Set(main, "Country", FieldOr(document.Fields, "country", "MY"));
        Set(main, "Area", NullIfEmpty(document.Area) ?? "----");
        Set(main, "Agent", NullIfEmpty(document.Agent) ?? "----");
        Set(main, "Project", NullIfEmpty(document.Project) ?? "----");
        Set(main, "Terms", NullIfEmpty(document.Terms) ?? "30 Days");
        Set(main, "CurrencyCode", NullIfEmpty(document.Currency) ?? "----");
        SetFloat(main, "CurrencyRate", ParseDouble(document.CurrencyRate, 1));
        Set(main, "Shipper", FieldOr(document.Fields, "shipper", "----"));
        Set(main, "Description", NullIfEmpty(document.Description)
                                 ?? (string.IsNullOrWhiteSpace(txnStamp) ? "Goods received" : "SYNC|" + txnStamp));
        Set(main, "Cancelled", false);
        SetInt(main, "Status", 0);
        SetFloat(main, "DocAmt", docAmt);
        SetFloat(main, "LocalDocAmt", docAmt);
        SetFloat(main, "LandingCost1", 0);
        SetFloat(main, "LandingCost2", 0);
        SetFloat(main, "LocalTotalWithCost", docAmt);
        SetFloat(main, "D_Amount", 0);
        Set(main, "BranchName", FieldOr(document.Fields, "branchname", "BILLING"));
        Set(main, "DCountry", FieldOr(document.Fields, "dcountry", "MY"));
        Set(main, "SIC", FieldOr(document.Fields, "sic", "00000"));
        SetInt(main, "SubmissionType", 0);
        SetIf(main, "Note", string.IsNullOrWhiteSpace(txnStamp)
            ? document.Note
            : (string.IsNullOrWhiteSpace(document.Note) ? "SYNC|" + txnStamp : document.Note + " | SYNC|" + txnStamp));
        SetHeaderOptional(main, document.Fields,
            "Address1", "Address2", "Address3", "Address4", "PostCode", "City", "State",
            "Phone1", "Mobile", "Fax1", "Attention",
            "DAddress1", "DAddress2", "DAddress3", "DAddress4", "DPostCode", "DCity", "DState",
            "DAttention", "DPhone1", "DMobile", "DFax1",
            "TaxExemptNo", "SalesTaxNo", "ServiceTaxNo", "TIN", "IDNO", "BusinessUnit",
            "DocRef1", "DocRef2", "DocRef3", "DocRef4");

        var seq = 0;
        foreach (var line in document.Lines)
        {
            seq++;
            var qty = (double)line.Qty;
            var unit = (double)line.UnitPrice;
            var amt = (double)(line.Amount != 0 ? line.Amount : line.Qty * line.UnitPrice);
            detail.Append();
            Set(detail, "Seq", line.Seq > 0 ? line.Seq : seq);
            SetIf(detail, "ItemCode", line.ItemCode);
            Set(detail, "Location", NullIfEmpty(line.Warehouse) ?? "----");
            SetIf(detail, "Batch", line.Batch);
            Set(detail, "Project", NullIfEmpty(line.Project) ?? NullIfEmpty(document.Project) ?? "----");
            SetIf(detail, "Description", line.Description);
            SetIf(detail, "Description2", line.Description2);
            SetFloat(detail, "ReceiveQty", qty);
            SetFloat(detail, "ReturnQty", 0);
            SetFloat(detail, "Qty", qty);
            Set(detail, "UOM", NullIfEmpty(line.Uom) ?? "UNIT");
            SetFloat(detail, "Rate", ParseDouble(line.Rate, 1));
            SetFloat(detail, "SQty", qty);
            SetFloat(detail, "SUOMQty", qty);
            SetFloat(detail, "UnitPrice", unit);
            SetIf(detail, "DISC", line.Disc);
            SetIf(detail, "Tax", line.TaxCode);
            SetIf(detail, "IRBM_CLASSIFICATION", line.IrbmClassification);
            SetFloat(detail, "TaxAmt", 0);
            SetFloat(detail, "LocalTaxAmt", 0);
            Set(detail, "TaxInclusive", line.TaxInclusive ? 1 : 0);
            SetFloat(detail, "Amount", amt);
            SetFloat(detail, "LocalAmount", amt);
            SetFloat(detail, "LandingCost1", 0);
            SetFloat(detail, "LandingCost2", 0);
            SetFloat(detail, "AmountWithTax", amt);
            Set(detail, "Printable", true);
            Set(detail, "Transferable", true);

            // Only destination-resolved transfer links. Never reuse source-company DocKey/DtlKey.
            SetTransferLinkIfDest(detail, line);

            detail.Post();
        }

        // Touch cdsSerialNumber so the dataset is opened; leave empty unless serials are supplied.
        try
        {
            dynamic serials = biz.DataSets.Find("cdsSerialNumber");
            _ = Convert.ToInt32(serials.RecordCount);
        }
        catch { /* optional dataset */ }

        return SaveClose(biz, "Created.", docNo, main);
    }

    private static void SetTransferLinkIfDest(dynamic detail, DocumentLine line)
    {
        var fromType = FieldOf(line.Fields, "fromdoctype", "FROMDOCTYPE");
        var fromDoc = FieldOf(line.Fields, "fromdockey", "FROMDOCKEY");
        var fromDtl = FieldOf(line.Fields, "fromdtlkey", "FROMDTLKEY");
        // Explicit dest markers only — ignore bare source DocKey copied onto the header.
        var destResolved = FieldOf(line.Fields, "dest_fromdockey", "destfromdockey");
        if (!string.IsNullOrWhiteSpace(destResolved))
            fromDoc = destResolved;
        var destDtl = FieldOf(line.Fields, "dest_fromdtlkey", "destfromdtlkey");
        if (!string.IsNullOrWhiteSpace(destDtl))
            fromDtl = destDtl;

        if (string.IsNullOrWhiteSpace(fromType) || string.IsNullOrWhiteSpace(fromDoc))
            return;
        if (!int.TryParse(fromDoc.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromDocKey)
            || fromDocKey <= 0)
            return;

        Set(detail, "FromDocType", fromType.Trim());
        SetInt(detail, "FromDocKey", fromDocKey);
        if (int.TryParse((fromDtl ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromDtlKey)
            && fromDtlKey > 0)
            SetInt(detail, "FromDtlKey", fromDtlKey);
    }

    private static WriteResult SaveClose(dynamic biz, string message, string? docNo, dynamic? main = null)
    {
        try
        {
            biz.Save();
        }
        catch (Exception ex)
        {
            try { biz.Close(); } catch { /* ignore */ }
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }

        var result = new WriteResult { Success = true, Message = message, DocNo = docNo ?? "" };
        if (main is not null)
        {
            try
            {
                var actualNo = Convert.ToString(main.FindField("DocNo").AsString)
                               ?? Convert.ToString(main.FindField("DOCNO").AsString)
                               ?? Convert.ToString(main.FindField("DocNo").value);
                if (!string.IsNullOrWhiteSpace(actualNo))
                    result.DocNo = actualNo.Trim();
            }
            catch { /* keep docNo */ }

            try
            {
                var dk = Convert.ToString(main.FindField("DOCKEY").value)
                         ?? Convert.ToString(main.FindField("DocKey").value);
                if (!string.IsNullOrWhiteSpace(dk))
                    result.DocKey = dk.Trim();
            }
            catch
            {
                result.DocKey = result.DocNo;
            }
        }

        if (string.IsNullOrWhiteSpace(result.DocKey))
            result.DocKey = result.DocNo;

        try { biz.Close(); } catch { /* ignore */ }
        return result;
    }

    private static dynamic FindBiz(dynamic app, params string[] names)
    {
        Exception? last = null;
        foreach (var name in names)
        {
            try { return app.BizObjects.Find(name); }
            catch (Exception ex) { last = ex; }
        }

        throw new InvalidOperationException(
            "BizObject not found: " + string.Join(", ", names), last);
    }

    private static void OpenByDocKey(object biz, object key) =>
        OpenByDocKey(biz, key, codeHint: null);

    private static void OpenByDocKey(object biz, object key, string? codeHint)
    {
        if (!TryOpenGlAccount(biz, key, codeHint, out string? error))
            throw new InvalidOperationException(error ?? "BizObject Open failed.");
    }

    /// <summary>
    /// Opens GL_ACC for update/delete/read. Tries Dockey/DocKey first, then Code/CODE with the
    /// string CODE hint (never put a numeric DOCKEY into the Code param — that was a read-back miss).
    /// </summary>
    public static bool TryOpenGlAccount(object bizObj, object key, string? codeHint, out string? error)
    {
        error = null;
        Exception? last = null;
        dynamic biz = bizObj;
        var code = (codeHint ?? "").Trim();
        var attempts = new List<(string Param, object Value)>
        {
            ("Dockey", key),
            ("DocKey", key)
        };
        if (code.Length > 0)
        {
            attempts.Add(("Code", code));
            attempts.Add(("CODE", code));
        }

        foreach (var (param, value) in attempts)
        {
            try
            {
                try { biz.Close(); } catch { /* ensure closed before next Open */ }
                biz.Params.Find(param).Value = value;
                biz.Open();
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                try { biz.Close(); } catch { /* ignore */ }
            }
        }

        error = "GL Open failed for key='" + Convert.ToString(key, CultureInfo.InvariantCulture) +
                "', CODE='" + code + "'" +
                (last is null ? "." : ": " + last.Message);
        return false;
    }

    private static void OpenPartyByKey(object bizObj, object key, string code)
    {
        Exception? last = null;
        dynamic biz = bizObj;
        foreach (var (param, value) in new (string, object)[]
                 {
                     ("Code", key),
                     ("CODE", key),
                     ("Code", code),
                     ("CODE", code)
                 })
        {
            try
            {
                biz.Params.Find(param).Value = value;
                biz.Open();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { biz.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            "Party Open failed for CODE='" + code + "'" +
            (last is null ? "." : ": " + last.Message), last);
    }

    /// <summary>
    /// COM FindKeyByRef often returns 0 / empty / DBNull when missing — those must not count as found.
    /// </summary>
    public static bool IsPresentKey(object? key)
    {
        if (key is null || key is DBNull) return false;
        switch (key)
        {
            case string s:
                return !string.IsNullOrWhiteSpace(s) && s.Trim() != "0";
            case int i:
                return i != 0;
            case long l:
                return l != 0;
            case short sh:
                return sh != 0;
            case uint ui:
                return ui != 0;
            case ulong ul:
                return ul != 0;
            case double d:
                return Math.Abs(d) > double.Epsilon;
            case float f:
                return Math.Abs(f) > float.Epsilon;
            case decimal m:
                return m != 0m;
            case bool:
                return true;
        }

        var text = Convert.ToString(key, CultureInfo.InvariantCulture);
        return !string.IsNullOrWhiteSpace(text) && text.Trim() != "0";
    }

    private static object? FindKey(dynamic biz, string field, string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0)
            return null;

        // Try exact field name, common casing, and FixedChar padding variants (GL CODE COM WIDTH≠FB len).
        var fields = new[] { field, field.ToUpperInvariant(), "CODE", "Code" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var values = new List<string> { value };
        if (field.Equals("CODE", StringComparison.OrdinalIgnoreCase)
            || field.Equals("Code", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length < GlAccountCodeMaxLength)
                values.Add(value.PadRight(GlAccountCodeMaxLength));
            if (value.Length < 20)
                values.Add(value.PadRight(20));
        }

        foreach (var f in fields)
        {
            foreach (var v in values.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    object? key = biz.FindKeyByRef(f, v);
                    if (IsPresentKey(key))
                        return key;
                }
                catch { /* try next */ }
            }
        }

        return null;
    }

    private static object? FindKeyComposite(dynamic biz, string fields, object[] values)
    {
        try
        {
            object? key = biz.FindKeyByRef(fields, values);
            return IsPresentKey(key) ? key : null;
        }
        catch { return null; }
    }

    private static void ClearDetailRows(dynamic detail)
    {
        try
        {
            while (Convert.ToInt32(detail.RecordCount) > 0)
            {
                detail.First();
                detail.Delete();
            }
        }
        catch
        {
            // Some detail sets use Last/Delete loop (wiki PH_PI edit).
            try
            {
                while (Convert.ToInt32(detail.RecordCount) != 0)
                {
                    detail.Last();
                    detail.Delete();
                }
            }
            catch { /* leave existing rows */ }
        }
    }

    private static void Set(dynamic ds, string field, object value)
    {
        // Prefer AsString for text fields — COM Edit/Save often ignores .value for DESCRIPTION/CODE.
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        try { ds.FindField(field).AsString = text; }
        catch
        {
            try { ds.FindField(field).value = value; }
            catch
            {
                try { ds.FindField(field).AsString = text; }
                catch { /* field absent on this build */ }
            }
        }
    }

    private static void SetIf(dynamic ds, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        Set(ds, field, value.Trim());
    }

    private static void SetInt(dynamic ds, string field, int value)
    {
        try { ds.FindField(field).value = value; }
        catch
        {
            try { ds.FindField(field).AsInteger = value; }
            catch { Set(ds, field, value); }
        }
    }

    private static void SetBool(dynamic ds, string field, bool value)
    {
        try { ds.FindField(field).value = value; }
        catch
        {
            try { ds.FindField(field).AsBoolean = value; }
            catch { Set(ds, field, value ? "T" : "F"); }
        }
    }

    private static void SetFloat(dynamic ds, string field, double value)
    {
        try { ds.FindField(field).value = value; }
        catch
        {
            try { ds.FindField(field).AsFloat = value; }
            catch { /* ignore */ }
        }
    }

    private static void SetHeaderOptional(dynamic main, Dictionary<string, string> fields, params string[] camelNames)
    {
        foreach (var name in camelNames)
        {
            if (fields.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
                Set(main, name, v.Trim());
            else if (fields.TryGetValue(name.ToLowerInvariant(), out var v2) && !string.IsNullOrWhiteSpace(v2))
                Set(main, name, v2.Trim());
        }
    }

    private static string FieldOr(Dictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static string FieldOf(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static WriteResult Fail(string message) =>
        new() { Success = false, Message = message };

    /// <summary>
    /// Uses Firebird-proven <see cref="GlAccountCodeMaxLength"/> (10). COM Size is unreliable for unicode FixedChar.
    /// </summary>
    public static int ResolveGlAccountCodeMaxLength(dynamic? mainDataSet)
    {
        // Prefer the verified Firebird CHARACTER_LENGTH. COM DataSize/WIDTH can look like 20/22 bytes.
        _ = mainDataSet;
        return GlAccountCodeMaxLength;
    }

    private static string ReadStringField(dynamic main, string field)
    {
        try
        {
            var v = Convert.ToString(main.FindField(field).AsString)
                    ?? Convert.ToString(main.FindField(field).value)
                    ?? "";
            return v.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static string? ResolveControlAccount(dynamic app, string bizObjectName, string? provided)
    {
        var chosen = ChooseControlAccount(provided, resolvedFromDestination: null);
        if (chosen is not null)
        {
            // Explicit mapping that does not exist on dest GL is an error — do not fall back to 300/400.
            return GlAccountCodeExists(app, chosen) ? chosen : null;
        }

        foreach (var candidate in QueryControlAccountCandidates(app, bizObjectName))
        {
            var pick = ChooseControlAccount(null, resolvedFromDestination: candidate);
            if (pick is not null && GlAccountCodeExists(app, pick))
                return pick;
        }

        return null;
    }

    private static IEnumerable<string> QueryControlAccountCandidates(dynamic app, string bizObjectName)
    {
        var isCustomer = string.Equals(bizObjectName, CustomerBiz, StringComparison.OrdinalIgnoreCase);
        var table = isCustomer ? "AR_CUSTOMER" : "AP_SUPPLIER";
        var sql =
            "SELECT DISTINCT TRIM(CONTROLACCOUNT) AS CONTROLACCOUNT FROM " + table +
            " WHERE CONTROLACCOUNT IS NOT NULL AND TRIM(CONTROLACCOUNT) <> ''";
        foreach (var row in QueryRows(app, sql))
        {
            if (row.ContainsKey("CONTROLACCOUNT"))
            {
                var code = NullIfEmpty(row["CONTROLACCOUNT"]);
                if (code is not null)
                    yield return code;
            }
        }
    }

    private static bool GlAccountCodeExists(dynamic app, string controlAccountCode)
    {
        // Prefer SELECT-only — FindBiz(GL_ACC) during party New/Edit is unsafe for the pending insert.
        return LoadGlPersistence(app, controlAccountCode, docKeyHint: null) is not null;
    }

    private sealed class PartyPersistence
    {
        public string Code { get; init; } = "";
        public string ControlAccount { get; init; } = "";
    }

    private sealed class GlPersistence
    {
        public string Code { get; init; } = "";
        public int DocKey { get; init; }
    }

    private static PartyPersistence? LoadPartyPersistence(dynamic app, string bizObjectName, string code)
    {
        // SQL-only. Never invent presence via FindKey (live MIRACLE: FindKey "hit" with empty CA, no Firebird row).
        var safe = code.Replace("'", "''", StringComparison.Ordinal);
        var isCustomer = string.Equals(bizObjectName, CustomerBiz, StringComparison.OrdinalIgnoreCase);
        var table = isCustomer ? "AR_CUSTOMER" : "AP_SUPPLIER";
        var row = QueryFirstRow(app,
            "SELECT FIRST 1 TRIM(CODE) AS CODE, TRIM(CONTROLACCOUNT) AS CONTROLACCOUNT FROM " + table +
            " WHERE TRIM(CODE) = '" + safe + "'");
        if (row is null)
            return null;

        var foundCode = row.ContainsKey("CODE") ? (row["CODE"] ?? "").Trim() : "";
        if (foundCode.Length == 0)
            return null;

        return new PartyPersistence
        {
            Code = foundCode,
            ControlAccount = row.ContainsKey("CONTROLACCOUNT") ? (row["CONTROLACCOUNT"] ?? "").Trim() : ""
        };
    }

    private static int CountGlChildren(dynamic app, int parentDocKey)
    {
        if (parentDocKey <= 0)
            return 0;
        var row = QueryFirstRow(app,
            "SELECT COUNT(*) AS CNT FROM GL_ACC WHERE PARENT = " +
            parentDocKey.ToString(CultureInfo.InvariantCulture));
        if (row is null || !row.ContainsKey("CNT"))
            return 0;
        var cntRaw = row["CNT"] ?? "0";
        int n;
        return int.TryParse(cntRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0
            ? n
            : 0;
    }

    private static GlPersistence? LoadGlPersistence(dynamic app, string? code, string? docKeyHint)
    {
        var trimmed = (code ?? "").Trim();
        var safe = trimmed.Replace("'", "''", StringComparison.Ordinal);
        Dictionary<string, string>? row = null;
        if (!string.IsNullOrWhiteSpace(docKeyHint)
            && int.TryParse(docKeyHint.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dkHint)
            && dkHint > 0)
        {
            row = QueryFirstRow(app,
                "SELECT FIRST 1 DOCKEY, TRIM(CODE) AS CODE FROM GL_ACC WHERE DOCKEY = " +
                dkHint.ToString(CultureInfo.InvariantCulture));
        }

        if (row is null && trimmed.Length > 0)
        {
            row = QueryFirstRow(app,
                "SELECT FIRST 1 DOCKEY, TRIM(CODE) AS CODE FROM GL_ACC WHERE TRIM(CODE) = '" + safe + "'");
        }

        if (row is null)
            return null;
        if (!int.TryParse(row.ContainsKey("DOCKEY") ? row["DOCKEY"] : "", NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var dk) || dk <= 0)
            return null;
        var foundCode = row.ContainsKey("CODE") ? (row["CODE"] ?? "").Trim() : trimmed;
        if (foundCode.Length == 0)
            return null;
        return new GlPersistence { Code = foundCode, DocKey = dk };
    }

    private static Dictionary<string, string>? QueryFirstRow(dynamic app, string sql)
    {
        foreach (var row in QueryRows(app, sql))
            return row;
        return null;
    }

    private static IEnumerable<Dictionary<string, string>> QueryRows(dynamic app, string sql)
    {
        SqlGuard.EnsureSelectOnly(sql);
        dynamic? ds = null;
        try
        {
            ds = app.DBManager.NewDataSet(sql);
            if (Convert.ToInt32(ds.RecordCount) <= 0)
                yield break;

            ds.First();
            while (!Convert.ToBoolean(ds.Eof))
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                dynamic fields = ds.Fields;
                var fieldCount = Convert.ToInt32(fields.Count);
                for (var i = 0; i < fieldCount; i++)
                {
                    dynamic f = fields.Items(i);
                    var name = Convert.ToString(f.FieldName) ?? ("F" + i);
                    string val;
                    try { val = Convert.ToString(f.AsString) ?? Convert.ToString(f.value) ?? ""; }
                    catch { val = Convert.ToString(f.value) ?? ""; }
                    map[name] = val.Trim();
                }

                yield return map;
                ds.Next();
            }
        }
        finally
        {
            if (ds is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ds); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Access underlying COM for BizObject ops (STA thread only).</summary>
    public static dynamic NativeApp(ISqlAccComApp app) =>
        app is SqlAccComApp com
            ? com.Native
            : throw new InvalidOperationException(
                "SDK writes require live SqlAccComApp. Unit-test fakes must stub write results without COM.");
}

/// <summary>Exposes native COM instance to <see cref="SqlAccBizObjectWriter"/>.</summary>
public sealed partial class SqlAccComApp
{
    internal dynamic Native => _app ?? throw new ObjectDisposedException(nameof(SqlAccComApp));
}
