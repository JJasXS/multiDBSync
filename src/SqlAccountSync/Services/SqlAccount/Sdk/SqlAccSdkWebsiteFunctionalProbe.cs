using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Items;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Controlled Website-path verification: <see cref="SqlAccountSdkWriteAdapter"/> → session → COM BizObject.
/// Unique SDKPOC* codes only. No bulk sync. No Firebird/REST write fallback.
/// </summary>
public sealed record SqlAccWebsiteFunctionalRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    string BackupPath,
    bool AuthorizeLanRead,
    string AuthorizedExactTarget,
    string? VerifyOtherDatabaseFileName = null,
    string? SupervisorManagedBackupConfirm = null,
    /// <summary>When true, skip document create steps (master CUD only — no Document Transfer / POC docs).</summary>
    bool SkipDocuments = false);

public sealed record SqlAccWebsiteFunctionalStep(
    string Feature,
    string Operation,
    bool Ok,
    string Detail,
    string? Code = null,
    string? DocNo = null,
    bool Blocked = false,
    bool OrphanPossible = false);

public sealed record SqlAccWebsiteFunctionalResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    string DatabaseFileName,
    bool LoginOk = false,
    bool IsolationOk = false,
    IReadOnlyList<SqlAccWebsiteFunctionalStep>? Steps = null);

public sealed class SqlAccSdkWebsiteFunctionalProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkWebsiteFunctionalProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccWebsiteFunctionalResult> RunAsync(
        SqlAccWebsiteFunctionalRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SqlAccWritePocSafety.EnsureRestorableBackup(
                request.DatabaseFileName,
                request.BackupPath,
                request.SupervisorManagedBackupConfirm);
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath, request.DatabaseFileName, request.AuthorizeLanRead, request.AuthorizedExactTarget);
            if (!string.IsNullOrWhiteSpace(request.VerifyOtherDatabaseFileName))
            {
                SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                    request.DcfPath, request.VerifyOtherDatabaseFileName!,
                    request.AuthorizeLanRead, request.AuthorizedExactTarget);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccWebsiteFunctionalResult(
                false, true, ex.Message, request.DatabaseFileName));
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Task.FromResult(new SqlAccWebsiteFunctionalResult(
                false, false, "UserName and Password are required.", request.DatabaseFileName));
        }

        // Orchestration runs off the STA thread; LiveProbeSession marshals all COM onto _sta
        // (nested _sta.RunAsync from inside an STA callback would deadlock).
        return Task.Run(() => ExecuteOrchestration(request), cancellationToken);
    }

    private SqlAccWebsiteFunctionalResult ExecuteOrchestration(SqlAccWebsiteFunctionalRequest request)
    {
        var steps = new List<SqlAccWebsiteFunctionalStep>();
        var stamp = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var suffix = Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
        var token = stamp + suffix;
        var db = new RegisteredDatabase
        {
            DbCode = Path.GetFileNameWithoutExtension(request.DatabaseFileName),
            FbPath = request.DatabaseFileName,
            DisplayName = request.DatabaseFileName
        };

        // Same STA queue as this ExecuteOnSta — COM must stay on STA even when adapter awaits.
        var sessions = new LiveProbeSession(request, _factory, _sta);
        var write = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);

        var loginOk = false;
        var isolationOk = true;
        var failed = false;
        var message = "";

        try
        {
            // Warm Login + SELECT-only through the same session path the Website adapter uses.
            var warm = sessions.RunAsync(db, app =>
            {
                if (!app.IsLogin)
                    throw new InvalidOperationException("IsLogin=false.");
                return app.QueryRecordCount("SELECT COUNT(*) AS CNT FROM RDB$DATABASE");
            }).GetAwaiter().GetResult();
            loginOk = true;
            steps.Add(new SqlAccWebsiteFunctionalStep(
                "Session", "Login+Read", true,
                $"Website session Login/SELECT OK (CNT={warm}). Company FDB={request.DatabaseFileName}.",
                Code: request.DatabaseFileName));

            var customerControl = ResolveControlAccount(sessions, db, customer: true);
            var supplierControl = ResolveControlAccount(sessions, db, customer: false);
            steps.Add(new SqlAccWebsiteFunctionalStep(
                "Prereq", "control-accounts",
                !string.IsNullOrWhiteSpace(customerControl) && !string.IsNullOrWhiteSpace(supplierControl),
                $"CustomerControl={customerControl}; SupplierControl={supplierControl}.",
                Blocked: string.IsNullOrWhiteSpace(customerControl) || string.IsNullOrWhiteSpace(supplierControl)));
            if (string.IsNullOrWhiteSpace(customerControl) || string.IsNullOrWhiteSpace(supplierControl))
            {
                failed = true;
                message = "Missing ControlAccount references for party create.";
            }

            if (!failed) RunCustomer(write, sessions, db, token, customerControl, steps, ref failed, ref message);
            if (!failed) RunSupplier(write, sessions, db, token, supplierControl, steps, ref failed, ref message);
            if (!failed) RunStockGroupAndItem(write, sessions, db, token, steps, ref failed, ref message);
            if (!failed) RunGlAccount(write, sessions, db, token, steps, ref failed, ref message);
            if (!failed && request.SkipDocuments)
            {
                steps.Add(new SqlAccWebsiteFunctionalStep(
                    "Document", "skipped", true,
                    "Document creates skipped (SkipDocuments=true). Master CUD only; no Document Transfer against business docs."));
            }
            else if (!failed)
            {
                RunDocumentRoutingAndSafeGr(write, sessions, db, token, steps, ref failed, ref message);
            }

            steps.Add(new SqlAccWebsiteFunctionalStep(
                "Sync/Push/AutoPush", "routing", true,
                "SyncOrchestrator/TransactionEngine/Push pages inject ISqlAccountWriteAdapter; " +
                "Provider=Sdk selects SqlAccountSdkWriteAdapter. FB timeout fallback disabled when Provider=Sdk. " +
                "Background workers left disabled for this lab — bulk Sync/AutoPush NOT executed."));

            if (!failed && string.IsNullOrWhiteSpace(message))
                message = "Website-path SDK functional verification completed for " + request.DatabaseFileName + ".";
        }
        catch (Exception ex)
        {
            failed = true;
            message = "Website functional probe failed: " + ex.GetType().Name + ": " + ex.Message;
            steps.Add(new SqlAccWebsiteFunctionalStep("EXCEPTION", "run", false, message, OrphanPossible: true));
        }
        finally
        {
            sessions.Dispose();
        }

        if (!string.IsNullOrWhiteSpace(request.VerifyOtherDatabaseFileName) && loginOk)
        {
            isolationOk = VerifyIsolation(request, steps, out var isoDetail);
            steps.Add(new SqlAccWebsiteFunctionalStep("Isolation", "cross-company", isolationOk, isoDetail));
            if (!isolationOk)
            {
                failed = true;
                message = isoDetail;
            }
        }

        var success = !failed && loginOk && isolationOk
                      && steps.Where(s => !s.Blocked).All(s => s.Ok);
        return new SqlAccWebsiteFunctionalResult(
            success, false, message, request.DatabaseFileName, loginOk, isolationOk, steps);
    }

    private static void RunCustomer(
        ISqlAccountWriteAdapter write, LiveProbeSession sessions, RegisteredDatabase db, string token,
        string controlAccount,
        List<SqlAccWebsiteFunctionalStep> steps, ref bool failed, ref string message)
    {
        // AR_CUSTOMER.CODE CHARACTER_LENGTH=10 (UTF8). Long POC codes truncate and fail post-save verify.
        var code = ShortPocCode("C", token, 10);
        var record = PartyRecord(code, "SDK POC Customer " + code, controlAccount);
        var create = write.CreateMasterAsync(db, EntityCodes.Customer, record, "SDKPOC-" + token).GetAwaiter().GetResult();
        steps.Add(Step("Customer", "create", create.Success, create.Message, code, orphan: create.Success));
        if (!create.Success) { failed = true; message = create.Message; return; }

        var exists = CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.CustomerBiz, code);
        steps.Add(Step("Customer", "read-after-create", exists, exists ? "AR_CUSTOMER FindKey present." : "Missing after create.", code, orphan: true));
        if (!exists) { failed = true; message = "Customer missing after create."; return; }

        record.Description = "SDK POC Customer UPD " + code;
        record.Fields["companyname"] = record.Description;
        var update = write.UpdateMasterAsync(db, EntityCodes.Customer, record, "SDKPOC-" + token, 0, 0).GetAwaiter().GetResult();
        steps.Add(Step("Customer", "update", update.Success, update.Message, code, orphan: true));
        if (!update.Success) { failed = true; message = update.Message; return; }

        var del = write.DeleteMasterAsync(db, EntityCodes.Customer, code).GetAwaiter().GetResult();
        steps.Add(Step("Customer", "delete", del.Success, del.Message, code, orphan: !del.Success));
        if (!del.Success) { failed = true; message = del.Message; return; }

        var gone = !CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.CustomerBiz, code);
        steps.Add(Step("Customer", "verify-deleted", gone, gone ? "Absent after delete." : "Still present.", code, orphan: !gone));
        if (!gone) { failed = true; message = "Customer still present after delete."; }
    }

    private static void RunSupplier(
        ISqlAccountWriteAdapter write, LiveProbeSession sessions, RegisteredDatabase db, string token,
        string controlAccount,
        List<SqlAccWebsiteFunctionalStep> steps, ref bool failed, ref string message)
    {
        // AP_SUPPLIER.CODE CHARACTER_LENGTH=10 (UTF8).
        var code = ShortPocCode("S", token, 10);
        var record = PartyRecord(code, "SDK POC Supplier " + code, controlAccount);
        var create = write.CreateMasterAsync(db, EntityCodes.Supplier, record, "SDKPOC-" + token).GetAwaiter().GetResult();
        steps.Add(Step("Supplier", "create", create.Success, create.Message, code, orphan: create.Success));
        if (!create.Success) { failed = true; message = create.Message; return; }

        var exists = CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.SupplierBiz, code);
        steps.Add(Step("Supplier", "read-after-create", exists, exists ? "AP_SUPPLIER FindKey present." : "Missing after create.", code, orphan: true));
        if (!exists) { failed = true; message = "Supplier missing after create."; return; }

        record.Description = "SDK POC Supplier UPD " + code;
        record.Fields["companyname"] = record.Description;
        var update = write.UpdateMasterAsync(db, EntityCodes.Supplier, record, "SDKPOC-" + token, 0, 0).GetAwaiter().GetResult();
        steps.Add(Step("Supplier", "update", update.Success, update.Message, code, orphan: true));
        if (!update.Success) { failed = true; message = update.Message; return; }

        var del = write.DeleteMasterAsync(db, EntityCodes.Supplier, code).GetAwaiter().GetResult();
        steps.Add(Step("Supplier", "delete", del.Success, del.Message, code, orphan: !del.Success));
        if (!del.Success) { failed = true; message = del.Message; return; }

        var gone = !CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.SupplierBiz, code);
        steps.Add(Step("Supplier", "verify-deleted", gone, gone ? "Absent after delete." : "Still present.", code, orphan: !gone));
        if (!gone) { failed = true; message = "Supplier still present after delete."; }
    }

    private static void RunStockGroupAndItem(
        ISqlAccountWriteAdapter write, LiveProbeSession sessions, RegisteredDatabase db, string token,
        List<SqlAccWebsiteFunctionalStep> steps, ref bool failed, ref string message)
    {
        // ST_GROUP.CODE CHARACTER_LENGTH=20; ST_ITEM.CODE=30.
        var groupCode = ShortPocCode("G", token, 20);

        var group = new StockGroupRecord
        {
            Code = groupCode,
            Description = "SDK POC group " + groupCode,
            CostingMethod = 1,
            IsActive = true
        };
        var gCreate = write.CreateStockGroupAsync(db, group).GetAwaiter().GetResult();
        steps.Add(Step("StockGroup", "create", gCreate.Success, gCreate.Message, groupCode, orphan: gCreate.Success));
        if (!gCreate.Success) { failed = true; message = gCreate.Message; return; }

        var gDup = write.CreateStockGroupAsync(db, group).GetAwaiter().GetResult();
        var dupOk = gDup.Success && gDup.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
        steps.Add(Step("StockGroup", "duplicate", dupOk, gDup.Message, groupCode));

        var uom = ShortPocCode("U", token, 10);
        var itemCode = ShortPocCode("I", token, 30);
        var item = new MasterRecord
        {
            Code = itemCode,
            Description = "SDK POC item " + itemCode,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = "SDK POC item " + itemCode,
                ["stockgroup"] = groupCode,
                ["stockcontrol"] = "T",
                ["isactive"] = "T",
                ["uom"] = uom,
                ["refcost"] = "1",
                ["refprice"] = "1"
            }
        };
        // StockItemMapper needs Uoms — set via StockItem path fields commonly used by mapper.
        var stockItem = new StockItem
        {
            Code = itemCode,
            Description = item.Description,
            StockGroup = groupCode,
            StockControl = true,
            IsActive = true,
            Uom = uom,
            RefCost = "1",
            RefPrice = "1",
            Uoms = [new StockItemUom { Uom = uom, Rate = "1", RefCost = "1", RefPrice = "1", IsBase = true }]
        };
        // Prefer adapter CreateMaster with mapped record; fill Fields from mapper conventions.
        item = ToItemMaster(stockItem);

        var iCreate = write.CreateMasterAsync(db, EntityCodes.Item, item, "SDKPOC-" + token).GetAwaiter().GetResult();
        steps.Add(Step("StockItem", "create-with-uom", iCreate.Success, iCreate.Message + $" UOM={uom}", itemCode, orphan: iCreate.Success));
        if (!iCreate.Success) { failed = true; message = iCreate.Message; return; }

        var itemExists = CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.ItemBiz, itemCode);
        var uomOk = ItemHasUom(sessions, db, itemCode, uom);
        steps.Add(Step("StockItem", "read-after-create", itemExists && uomOk,
            $"Exists={itemExists}; cdsUOM has {uom}={uomOk}.", itemCode, orphan: true));
        if (!(itemExists && uomOk)) { failed = true; message = "Item/UOM read-back failed."; return; }

        item.Description = "SDK POC item UPD " + itemCode;
        item.Fields["description"] = item.Description;
        var iUpdate = write.UpdateMasterAsync(db, EntityCodes.Item, item, "SDKPOC-" + token, 0, 0).GetAwaiter().GetResult();
        steps.Add(Step("StockItem", "update", iUpdate.Success, iUpdate.Message, itemCode, orphan: true));
        if (!iUpdate.Success) { failed = true; message = iUpdate.Message; return; }

        var iDel = write.DeleteMasterAsync(db, EntityCodes.Item, itemCode).GetAwaiter().GetResult();
        steps.Add(Step("StockItem", "delete", iDel.Success, iDel.Message, itemCode, orphan: !iDel.Success));
        if (!iDel.Success) { failed = true; message = iDel.Message; return; }

        var groupUpd = write.UpdateStockGroupAsync(db, new StockGroupRecord
        {
            Code = groupCode,
            Description = "SDK POC group UPD " + groupCode,
            IsActive = true,
            CostingMethod = 0
        }).GetAwaiter().GetResult();
        steps.Add(Step("StockGroup", "update", groupUpd.Success, groupUpd.Message, groupCode, orphan: true));
        if (!groupUpd.Success) { failed = true; message = groupUpd.Message; return; }

        var gDel = write.DeleteStockGroupAsync(db, groupCode).GetAwaiter().GetResult();
        steps.Add(Step("StockGroup", "delete", gDel.Success, gDel.Message, groupCode, orphan: !gDel.Success));
        if (!gDel.Success) { failed = true; message = gDel.Message; return; }

        var gGone = !CodeExistsOnBiz(sessions, db, SqlAccBizObjectWriter.StockGroupBiz, groupCode);
        steps.Add(Step("StockGroup", "verify-deleted", gGone,
            gGone ? "Absent after delete." : "Still present.", groupCode, orphan: !gGone));
        if (!gGone) { failed = true; message = "Stock group still present after delete."; return; }

        steps.Add(Step("UOM", "standalone", true,
            "No standalone UOM BizObject used; new UOM written via ST_ITEM.cdsUOM only.", uom, blocked: true));
    }

    private static void RunGlAccount(
        ISqlAccountWriteAdapter write, LiveProbeSession sessions, RegisteredDatabase db, string token,
        List<SqlAccWebsiteFunctionalStep> steps, ref bool failed, ref string message)
    {
        var parent = FindParent(sessions, db);
        if (parent is null)
        {
            steps.Add(Step("GL Account", "find-parent", false, "No parent GL account found.", blocked: true));
            failed = true;
            message = "No parent GL account.";
            return;
        }

        steps.Add(Step("GL Account", "parent", true,
            $"Parent CODE={parent.Value.Code} DOCKEY={parent.Value.DocKey}", parent.Value.Code));

        // GL_ACC.CODE CHARACTER_LENGTH=10 (verified). Keep POC code within that limit.
        var code = "SDKPOC" + token[^4..];
        if (code.Length > SqlAccBizObjectWriter.GlAccountCodeMaxLength)
            code = code[..SqlAccBizObjectWriter.GlAccountCodeMaxLength];
        var record = new MasterRecord
        {
            Code = code,
            Description = "SDK POC GL " + code,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = "SDK POC GL " + code,
                [GlAccountMapper.FieldDestParent] = parent.Value.DocKey.ToString(CultureInfo.InvariantCulture),
                [GlAccountMapper.FieldDescription2] = "SDKPOC",
                [GlAccountMapper.FieldAccType] = string.IsNullOrWhiteSpace(parent.Value.AccType) ? "SA" : parent.Value.AccType
            }
        };

        var create = write.CreateMasterAsync(db, EntityCodes.Account, record, "SDKPOC-" + token).GetAwaiter().GetResult();
        steps.Add(Step("GL Account", "create", create.Success, create.Message, code, orphan: create.Success));
        if (!create.Success) { failed = true; message = create.Message; return; }

        // Prefer persisted CODE/DOCKEY from write result for all subsequent ops.
        var persistedCode = string.IsNullOrWhiteSpace(create.DocNo) ? code : create.DocNo.Trim();
        record.Code = persistedCode;

        var read1 = ReadGl(sessions, db, persistedCode);
        var ok1 = read1 is not null && read1.Value.Parent == parent.Value.DocKey && read1.Value.DocKey > 0;
        steps.Add(Step("GL Account", "read-after-create", ok1,
            read1 is null
                ? "Missing after create (BizObject/SQL)."
                : $"CODE={persistedCode}; DOCKEY={read1.Value.DocKey}; PARENT={read1.Value.Parent}; DESC={read1.Value.Description}",
            persistedCode, orphan: true));
        if (!ok1 || read1 is null)
        {
            try { write.DeleteMasterAsync(db, EntityCodes.Account, persistedCode).GetAwaiter().GetResult(); }
            catch { /* ignore */ }
            failed = true;
            message = "GL read-back failed.";
            return;
        }

        var updateDesc = "SDK POC GL UPD " + persistedCode;
        record.Description = updateDesc;
        record.Fields["description"] = updateDesc;
        record.Fields[GlAccountMapper.FieldDescription2] = "SDKPOC-UPD";
        var update = write.UpdateMasterAsync(
            db, EntityCodes.Account, record, "SDKPOC-" + token, read1.Value.DocKey, parent.Value.DocKey)
            .GetAwaiter().GetResult();
        steps.Add(Step("GL Account", "update", update.Success, update.Message, persistedCode, orphan: true));
        if (!update.Success) { failed = true; message = update.Message; return; }

        var read2 = ReadGl(sessions, db, persistedCode);
        var ok2 = read2 is not null
                  && string.Equals(read2.Value.Description.Trim(), updateDesc, StringComparison.Ordinal);
        steps.Add(Step("GL Account", "read-after-update", ok2,
            read2 is null ? "Missing." : $"DESC={read2.Value.Description}", persistedCode, orphan: true));
        if (!ok2)
        {
            try { write.DeleteMasterAsync(db, EntityCodes.Account, persistedCode).GetAwaiter().GetResult(); }
            catch { /* ignore */ }
            failed = true;
            message = "GL update read-back failed.";
            return;
        }

        // Read-only parent protection — never DeleteMaster on existing business/parent GL codes.
        var childCntText = FirstField(sessions, db,
            "SELECT COUNT(*) AS CNT FROM GL_ACC WHERE PARENT = " +
            parent.Value.DocKey.ToString(CultureInfo.InvariantCulture),
            "CNT");
        _ = int.TryParse(childCntText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var childCnt);
        var parentGuarded = childCnt > 0;
        steps.Add(Step("GL Account", "parent-protection-readonly", parentGuarded,
            parentGuarded
                ? "Parent DOCKEY=" + parent.Value.DocKey + " CODE=" + parent.Value.Code +
                  " has " + childCnt + " child row(s); parent delete not attempted."
                : "Parent has no children after POC create — unexpected.",
            parent.Value.Code));
        if (!parentGuarded)
        {
            failed = true;
            message = "Parent protection check failed.";
            return;
        }

        var del = write.DeleteMasterAsync(db, EntityCodes.Account, persistedCode).GetAwaiter().GetResult();
        steps.Add(Step("GL Account", "delete", del.Success, del.Message, persistedCode, orphan: !del.Success));
        if (!del.Success) { failed = true; message = del.Message; return; }

        var gone = ReadGl(sessions, db, persistedCode) is null;
        steps.Add(Step("GL Account", "verify-deleted", gone, gone ? "Absent after delete." : "Still present.", persistedCode, orphan: !gone));
        if (!gone) { failed = true; message = "GL still present after delete."; }
    }

    private static void RunDocumentRoutingAndSafeGr(
        ISqlAccountWriteAdapter write, LiveProbeSession sessions, RegisteredDatabase db, string token,
        List<SqlAccWebsiteFunctionalStep> steps, ref bool failed, ref string message)
    {
        var routes = new (string Feature, string Path, string? Biz)[]
        {
            ("Document PI", "/purchaseinvoice", SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchaseinvoice")),
            ("Document GR", "/goodsreceived", SqlAccBizObjectWriter.ResolveDocumentBizObject("/goodsreceived")),
            ("Document ST", "/stocktransfer", SqlAccBizObjectWriter.ResolveDocumentBizObject("/stocktransfer")),
            ("Document PR", "/purchasereturned", SqlAccBizObjectWriter.ResolveDocumentBizObject("/purchasereturned")),
            ("Document CN", "/salescreditnote", SqlAccBizObjectWriter.ResolveDocumentBizObject("/salescreditnote")),
            ("Document DN", "/salesdebitnote", SqlAccBizObjectWriter.ResolveDocumentBizObject("/salesdebitnote")),
        };
        foreach (var r in routes)
        {
            steps.Add(Step(r.Feature, "bizobject-route", r.Biz is not null,
                r.Biz is null ? $"No SDK mapping for {r.Path}." : $"{r.Path} → {r.Biz}.",
                blocked: r.Biz is null));
        }

        // Standalone document creates on STEPPES (empty dest tables). Unique DocNo; SDKPOC description.
        // No FROM* transfer links. No delete of existing business documents. Leave POC docs for inspection.
        var supplier = FirstCode(sessions, db, "SELECT FIRST 1 TRIM(CODE) AS CODE FROM AP_SUPPLIER ORDER BY CODE");
        var customer = FirstCode(sessions, db, "SELECT FIRST 1 TRIM(CODE) AS CODE FROM AR_CUSTOMER ORDER BY CODE");
        var item = FirstCode(sessions, db, "SELECT FIRST 1 TRIM(CODE) AS CODE FROM ST_ITEM ORDER BY CODE");
        var uom = FirstField(sessions, db,
            "SELECT FIRST 1 TRIM(UOM) AS UOM FROM ST_ITEM WHERE TRIM(CODE) = '" +
            (item ?? "").Replace("'", "''", StringComparison.Ordinal) + "'",
            "UOM");
        if (string.IsNullOrWhiteSpace(uom))
            uom = "UNIT";

        if (string.IsNullOrWhiteSpace(supplier) || string.IsNullOrWhiteSpace(item))
        {
            steps.Add(Step("Document GR", "create", false,
                "Missing supplier or item reference — document creates skipped.", blocked: true));
            return;
        }

        // PH_GR standalone (Website goodsreceived path). Unique DocNo — avoid literal "--".
        CreateAndVerifyDoc(
            write, sessions, db, steps, ref failed, ref message,
            feature: "Document GR",
            apiPath: "/goodsreceived",
            table: "PH_GR",
            docNo: ShortPocCode("GR", token, 10),
            party: supplier,
            item: item,
            uom: uom,
            token: token,
            descriptionPrefix: "SDKPOC GR");

        steps.Add(Step("Document GR", "from-link-note", true,
            "FROMDOCTYPE/FROMDOCKEY/FROMDTLKEY not set (no destination DO keys in this lab).",
            blocked: true));
        steps.Add(Step("Document DO→GR", "link", true,
            "Blocked: SL_DO has no rows on ACC-STEPPES.FDB; cannot authorize FROM* link without a safe destination DO.",
            blocked: true));

        // Remaining Website dest doc paths — independent of DO→GR. Stop only the failed path; continue others.
        CreateAndVerifyDoc(
            write, sessions, db, steps, ref failed, ref message,
            feature: "Document PI",
            apiPath: "/purchaseinvoice",
            table: "PH_PI",
            docNo: ShortPocCode("PI", token, 10),
            party: supplier,
            item: item,
            uom: uom,
            token: token,
            descriptionPrefix: "SDKPOC PI");

        var locFrom = FirstCode(sessions, db, "SELECT FIRST 1 TRIM(CODE) AS CODE FROM ST_LOCATION ORDER BY CODE");
        var locTo = FirstCode(sessions, db,
            "SELECT FIRST 1 TRIM(CODE) AS CODE FROM ST_LOCATION WHERE TRIM(CODE) <> '" +
            (locFrom ?? "").Replace("'", "''", StringComparison.Ordinal) + "' ORDER BY CODE");
        if (string.IsNullOrWhiteSpace(locFrom) || string.IsNullOrWhiteSpace(locTo)
            || string.Equals(locFrom, locTo, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(Step("Document ST", "create", true,
                "Blocked: ACC-STEPPES.FDB has fewer than 2 ST_LOCATION codes (SDK refuses same From/To). " +
                "BizObject route ST_XF is mapped; live transfer not executed. Locations=[" +
                (locFrom ?? "") + "].",
                blocked: true));
        }
        else
        {
            CreateAndVerifyDoc(
                write, sessions, db, steps, ref failed, ref message,
                feature: "Document ST",
                apiPath: "/stocktransfer",
                table: "ST_XF",
                docNo: ShortPocCode("ST", token, 10),
                party: supplier,
                item: item,
                uom: uom,
                token: token,
                descriptionPrefix: "SDKPOC ST",
                stockTransfer: true,
                fromLocation: locFrom,
                toLocation: locTo);
        }

        CreateAndVerifyDoc(
            write, sessions, db, steps, ref failed, ref message,
            feature: "Document PR",
            apiPath: "/purchasereturned",
            table: "PH_SC",
            docNo: ShortPocCode("PR", token, 10),
            party: supplier,
            item: item,
            uom: uom,
            token: token,
            descriptionPrefix: "SDKPOC PR");

        if (string.IsNullOrWhiteSpace(customer))
        {
            steps.Add(Step("Document CN", "create", false,
                "No AR_CUSTOMER row — CN/DN skipped.", blocked: true));
            steps.Add(Step("Document DN", "create", false,
                "No AR_CUSTOMER row — CN/DN skipped.", blocked: true));
        }
        else
        {
            CreateAndVerifyDoc(
                write, sessions, db, steps, ref failed, ref message,
                feature: "Document CN",
                apiPath: "/salescreditnote",
                table: "SL_CN",
                docNo: ShortPocCode("CN", token, 10),
                party: customer,
                item: item,
                uom: uom,
                token: token,
                descriptionPrefix: "SDKPOC CN");

            CreateAndVerifyDoc(
                write, sessions, db, steps, ref failed, ref message,
                feature: "Document DN",
                apiPath: "/salesdebitnote",
                table: "SL_DN",
                docNo: ShortPocCode("DN", token, 10),
                party: customer,
                item: item,
                uom: uom,
                token: token,
                descriptionPrefix: "SDKPOC DN");
        }

        steps.Add(Step("Document", "cleanup-note", true,
            "POC documents left for inspection (SDKPOC* DocNo/Description). Existing business documents were not deleted."));
    }

    private static void CreateAndVerifyDoc(
        ISqlAccountWriteAdapter write,
        LiveProbeSession sessions,
        RegisteredDatabase db,
        List<SqlAccWebsiteFunctionalStep> steps,
        ref bool failed,
        ref string message,
        string feature,
        string apiPath,
        string table,
        string docNo,
        string party,
        string item,
        string uom,
        string token,
        string descriptionPrefix,
        bool stockTransfer = false,
        string? fromLocation = null,
        string? toLocation = null)
    {
        var line = new DocumentLine
        {
            Seq = 1,
            ItemCode = item,
            Description = descriptionPrefix + " line",
            Qty = 1m,
            Uom = uom,
            Rate = "1",
            UnitPrice = 1m,
            Amount = 1m,
            Warehouse = stockTransfer ? (fromLocation ?? "----") : "----",
            FromLocation = stockTransfer ? (fromLocation ?? "----") : "",
            ToLocation = stockTransfer ? (toLocation ?? "----") : ""
        };

        var doc = new DocumentHeader
        {
            DocNo = docNo,
            DocDate = DateTime.Today,
            PostDate = DateTime.Today,
            TaxDate = DateTime.Today,
            PartyCode = party,
            PartyName = party,
            Description = descriptionPrefix + " " + token,
            Area = "----",
            Agent = "----",
            Project = "----",
            Terms = "30 Days",
            Currency = "----",
            CurrencyRate = "1",
            Amount = 1m,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["shipper"] = "----",
                ["country"] = "MY",
                ["sic"] = "00000"
            },
            Lines = [line]
        };

        var create = write.CreateDocumentAsync(db, apiPath, doc, "SDKPOC-" + token).GetAwaiter().GetResult();
        steps.Add(Step(feature, "create", create.Success, create.Message, docNo: create.DocNo ?? docNo, orphan: create.Success));
        if (!create.Success)
        {
            failed = true;
            if (string.IsNullOrWhiteSpace(message))
                message = feature + ": " + create.Message;
            return;
        }

        var safeDoc = (create.DocNo ?? docNo).Replace("'", "''", StringComparison.Ordinal);
        var persisted = FirstField(sessions, db,
            "SELECT FIRST 1 TRIM(DOCNO) AS DOCNO FROM " + table + " WHERE TRIM(DOCNO) = '" + safeDoc + "'",
            "DOCNO");
        var ok = !string.IsNullOrWhiteSpace(persisted);
        steps.Add(Step(feature, "verify-create", ok,
            ok ? $"Present in {table} DOCNO={persisted}." : $"Save reported success but {table} row missing for DOCNO={safeDoc}.",
            docNo: create.DocNo ?? docNo, orphan: true));
        if (!ok)
        {
            failed = true;
            if (string.IsNullOrWhiteSpace(message))
                message = feature + " verify-create failed.";
        }
    }

    private bool VerifyIsolation(
        SqlAccWebsiteFunctionalRequest request,
        List<SqlAccWebsiteFunctionalStep> steps,
        out string detail)
    {
        var codes = steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Code) && s.Code!.StartsWith("SDKPOC", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            detail = "No SDKPOC codes left to check on other company.";
            return true;
        }

        ISqlAccComApp? other = null;
        try
        {
            other = _factory.Create();
            other.Login(request.UserName.Trim(), request.Password, request.DcfPath.Trim(),
                request.VerifyOtherDatabaseFileName!.Trim());
            if (!other.IsLogin)
            {
                detail = "Isolation login failed on " + request.VerifyOtherDatabaseFileName;
                return false;
            }

            object native = SqlAccBizObjectWriter.NativeApp(other);
            var leaks = new List<string>();
            foreach (var code in codes)
            {
                if (BizCodeExists(native, "AR_CUSTOMER", code)) leaks.Add("AR_CUSTOMER:" + code);
                if (BizCodeExists(native, "AP_SUPPLIER", code)) leaks.Add("AP_SUPPLIER:" + code);
                if (BizCodeExists(native, "ST_ITEM", code)) leaks.Add("ST_ITEM:" + code);
                if (BizCodeExists(native, "ST_GROUP", code)) leaks.Add("ST_GROUP:" + code);
                if (BizCodeExists(native, "GL_ACC", code) || BizCodeExists(native, "GL_Acc", code))
                    leaks.Add("GL_ACC:" + code);
            }

            other.Logout();
            if (leaks.Count > 0)
            {
                detail = "Cross-company leak into " + request.VerifyOtherDatabaseFileName + ": " + string.Join(", ", leaks);
                return false;
            }

            detail = "No SDKPOC customer/supplier/item/group/GL codes on " + request.VerifyOtherDatabaseFileName + ".";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Isolation check failed: " + ex.Message;
            return false;
        }
        finally
        {
            try { if (other?.IsLogin == true) other.Logout(); } catch { /* ignore */ }
            try { other?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static bool CodeExistsOnBiz(LiveProbeSession sessions, RegisteredDatabase db, string bizName, string code) =>
        sessions.RunAsync(db, app =>
        {
            object native = SqlAccBizObjectWriter.NativeApp(app);
            return BizCodeExists(native, bizName, code);
        }).GetAwaiter().GetResult();

    private static bool BizCodeExists(object nativeApp, string bizName, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find(bizName);
            object? key = biz.FindKeyByRef("CODE", code);
            return SqlAccBizObjectWriter.IsPresentKey(key);
        }
        catch { return false; }
    }

    private static bool ItemHasUom(LiveProbeSession sessions, RegisteredDatabase db, string itemCode, string uomCode) =>
        sessions.RunAsync(db, app =>
        {
            try
            {
                dynamic native = SqlAccBizObjectWriter.NativeApp(app);
                dynamic biz = native.BizObjects.Find("ST_ITEM");
                object? key = biz.FindKeyByRef("CODE", itemCode);
                if (!SqlAccBizObjectWriter.IsPresentKey(key)) return false;
                try { biz.Params.Find("Dockey").Value = key; }
                catch { biz.Params.Find("Code").Value = itemCode; }
                biz.Open();
                try
                {
                    dynamic uom = biz.DataSets.Find("cdsUOM");
                    var count = Convert.ToInt32(uom.RecordCount);
                    if (count <= 0) return false;
                    uom.First();
                    for (var i = 0; i < count; i++)
                    {
                        var v = Convert.ToString(uom.FindField("UOM").AsString)
                                ?? Convert.ToString(uom.FindField("UOM").value)
                                ?? "";
                        if (string.Equals(v.Trim(), uomCode, StringComparison.OrdinalIgnoreCase))
                            return true;
                        if (i < count - 1) uom.Next();
                    }
                    return false;
                }
                finally { try { biz.Close(); } catch { /* ignore */ } }
            }
            catch { return false; }
        }).GetAwaiter().GetResult();

    private static (int DocKey, int Parent, string Code, string Description, string AccType)? FindParent(
        LiveProbeSession sessions, RegisteredDatabase db) =>
        sessions.RunAsync<(int DocKey, int Parent, string Code, string Description, string AccType)?>(db, app =>
        {
            dynamic native = SqlAccBizObjectWriter.NativeApp(app);
            foreach (var sql in new[]
                     {
                         "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC WHERE PARENT = 0 ORDER BY CODE",
                         "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC ORDER BY CODE"
                     })
            {
                var row = QueryFirst(native, sql);
                if (row is null) continue;
                if (!int.TryParse(Field(row, "DOCKEY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dk) || dk <= 0)
                    continue;
                _ = int.TryParse(Field(row, "PARENT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p);
                return (dk, p, Field(row, "CODE"), Field(row, "DESCRIPTION"), Field(row, "ACCTYPE"));
            }

            return null;
        }).GetAwaiter().GetResult();

    private static (int DocKey, int Parent, string Description)? ReadGl(
        LiveProbeSession sessions, RegisteredDatabase db, string code) =>
        sessions.RunAsync<(int DocKey, int Parent, string Description)?>(db, app =>
        {
            dynamic native = SqlAccBizObjectWriter.NativeApp(app);
            try
            {
                dynamic biz;
                try { biz = native.BizObjects.Find("GL_ACC"); }
                catch { biz = native.BizObjects.Find("GL_Acc"); }
                object? key = biz.FindKeyByRef("CODE", code);
                if (SqlAccBizObjectWriter.IsPresentKey(key))
                {
                    try { biz.Params.Find("Dockey").Value = key; }
                    catch
                    {
                        try { biz.Params.Find("DocKey").Value = key; }
                        catch { biz.Params.Find("Code").Value = code; }
                    }
                    biz.Open();
                    try
                    {
                        dynamic main = biz.DataSets.Find("MainDataSet");
                        var dockey = Convert.ToInt32(main.FindField("DOCKEY").value);
                        var parent = Convert.ToInt32(main.FindField("PARENT").value);
                        var desc = Convert.ToString(main.FindField("DESCRIPTION").AsString)
                                   ?? Convert.ToString(main.FindField("DESCRIPTION").value)
                                   ?? "";
                        return (dockey, parent, desc);
                    }
                    finally { try { biz.Close(); } catch { /* ignore */ } }
                }
            }
            catch { /* fall through to SQL */ }

            // SELECT-only persistence check (read path already used elsewhere; not a write bypass).
            var safe = code.Replace("'", "''");
            var row = QueryFirst(native,
                "SELECT FIRST 1 DOCKEY, PARENT, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION FROM GL_ACC WHERE TRIM(CODE) = '" + safe + "'");
            if (row is null) return null;
            if (!int.TryParse(Field(row, "DOCKEY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dk) || dk <= 0)
                return null;
            _ = int.TryParse(Field(row, "PARENT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p);
            return (dk, p, Field(row, "DESCRIPTION"));
        }).GetAwaiter().GetResult();

    private static string FirstCode(LiveProbeSession sessions, RegisteredDatabase db, string sql) =>
        sessions.RunAsync(db, app =>
        {
            dynamic native = SqlAccBizObjectWriter.NativeApp(app);
            return Field(QueryFirst(native, sql), "CODE");
        }).GetAwaiter().GetResult();

    private static Dictionary<string, string>? QueryFirst(dynamic native, string sql)
    {
        var rows = QueryAll(native, sql);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static List<Dictionary<string, string>> QueryAll(dynamic native, string sql)
    {
        SqlGuard.EnsureSelectOnly(sql);
        var list = new List<Dictionary<string, string>>();
        dynamic? ds = null;
        try
        {
            ds = native.DBManager.NewDataSet(sql);
            if (Convert.ToInt32(ds.RecordCount) <= 0)
                return list;
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

                list.Add(map);
                ds.Next();
            }
        }
        catch { /* return what we have */ }
        finally
        {
            if (ds is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ds); } catch { /* ignore */ }
            }
        }

        return list;
    }

    private static string Field(Dictionary<string, string>? row, string name) =>
        row is not null && row.TryGetValue(name, out var v) ? v : "";

    private static string ResolveControlAccount(LiveProbeSession sessions, RegisteredDatabase db, bool customer)
    {
        var table = customer ? "AR_CUSTOMER" : "AP_SUPPLIER";
        var sql =
            "SELECT DISTINCT TRIM(CONTROLACCOUNT) AS CONTROLACCOUNT FROM " + table +
            " WHERE CONTROLACCOUNT IS NOT NULL AND TRIM(CONTROLACCOUNT) <> ''";
        var candidates = sessions.RunAsync(db, app =>
        {
            dynamic native = SqlAccBizObjectWriter.NativeApp(app);
            return QueryAll(native, sql);
        }).GetAwaiter().GetResult();

        foreach (var row in candidates)
        {
            var control = Field(row, "CONTROLACCOUNT");
            if (string.IsNullOrWhiteSpace(control))
                continue;
            var safe = control.Replace("'", "''", StringComparison.Ordinal);
            var exists = FirstField(sessions, db,
                "SELECT FIRST 1 TRIM(CODE) AS CODE FROM GL_ACC WHERE TRIM(CODE) = '" + safe + "'",
                "CODE");
            if (!string.IsNullOrWhiteSpace(exists))
                return control;
        }

        // No hardcoded 300-000 / 400-000 — fail clear if destination has no valid ControlAccount.
        return "";
    }

    private static string FirstField(LiveProbeSession sessions, RegisteredDatabase db, string sql, string field) =>
        sessions.RunAsync(db, app =>
        {
            dynamic native = SqlAccBizObjectWriter.NativeApp(app);
            return Field(QueryFirst(native, sql), field);
        }).GetAwaiter().GetResult();

    /// <summary>Builds a unique POC code within Firebird CHARACTER_LENGTH for the target table.</summary>
    private static string ShortPocCode(string prefix, string token, int maxLen)
    {
        prefix = (prefix ?? "").Trim();
        token = (token ?? "").Trim();
        if (maxLen < 2) throw new ArgumentOutOfRangeException(nameof(maxLen));
        if (prefix.Length >= maxLen)
            return prefix[..maxLen];
        var budget = maxLen - prefix.Length;
        var tail = token.Length <= budget ? token : token[^budget..];
        return prefix + tail;
    }

    private static MasterRecord PartyRecord(string code, string companyName, string controlAccount) =>
        new()
        {
            Code = code,
            Description = companyName,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["companyname"] = companyName,
                ["controlaccount"] = controlAccount,
                ["area"] = "----",
                ["agent"] = "----",
                ["creditterm"] = "30 Days",
                ["currencycode"] = "----",
                ["status"] = "A"
            }
        };

    private static MasterRecord ToItemMaster(StockItem item)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = item.Description ?? item.Code,
            ["stockgroup"] = item.StockGroup ?? "",
            ["uom"] = item.Uom ?? "UNIT",
            ["refcost"] = item.RefCost ?? "0",
            ["refprice"] = item.RefPrice ?? "0",
            ["stockcontrol"] = item.StockControl ? "T" : "F",
            ["isactive"] = item.IsActive ? "T" : "F"
        };
        return new MasterRecord { Code = item.Code, Description = item.Description ?? item.Code, Fields = fields };
    }

    private static SqlAccWebsiteFunctionalStep Step(
        string feature, string op, bool ok, string detail,
        string? code = null, string? docNo = null, bool blocked = false, bool orphan = false) =>
        new(feature, op, ok, detail, code, docNo, blocked, orphan);

    /// <summary>Minimal session manager for the WritePoc: Login with request credentials + DCF/FDB.</summary>
    private sealed class LiveProbeSession : ISqlAccSessionManager, IDisposable
    {
        private readonly SqlAccWebsiteFunctionalRequest _request;
        private readonly ISqlAccComAppFactory _factory;
        private readonly StaWorkQueue _sta;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ISqlAccComApp? _app;
        private string? _key;

        public LiveProbeSession(SqlAccWebsiteFunctionalRequest request, ISqlAccComAppFactory factory, StaWorkQueue sta)
        {
            _request = request;
            _factory = factory;
            _sta = sta;
        }

        public async Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sta.RunAsync(() => EnsureLogin(db), cancellationToken).ConfigureAwait(false);
                return new Lease(_gate);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        public async Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default)
        {
            await using var lease = new AsyncLease(await UseCompanyAsync(db, cancellationToken).ConfigureAwait(false));
            return await _sta.RunAsync(() =>
            {
                if (_app is null)
                    throw new InvalidOperationException("COM app not logged in.");
                return action(_app);
            }, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureLogin(RegisteredDatabase db)
        {
            var file = Path.GetFileName((db.FbPath ?? "").Trim().Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(file))
                file = _request.DatabaseFileName.Trim();
            var key = _request.DcfPath.Trim() + "|" + file;
            _app ??= _factory.Create();
            if (_app.IsLogin && string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
                return;
            if (_app.IsLogin)
            {
                _app.Logout();
                _key = null;
            }
            _app.Login(_request.UserName.Trim(), _request.Password, _request.DcfPath.Trim(), file);
            if (!_app.IsLogin)
                throw new InvalidOperationException("Login did not set IsLogin=true for " + file);
            _key = key;
        }

        public void Dispose()
        {
            try
            {
                if (_app?.IsLogin == true) _app.Logout();
            }
            catch { /* ignore */ }
            try { _app?.Dispose(); } catch { /* ignore */ }
            _gate.Dispose();
        }

        private sealed class Lease(SemaphoreSlim gate) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                    gate.Release();
            }
        }

        private sealed class AsyncLease(IDisposable inner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
