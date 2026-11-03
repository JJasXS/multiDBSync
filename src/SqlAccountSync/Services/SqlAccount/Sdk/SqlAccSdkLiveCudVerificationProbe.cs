using System.Globalization;
using System.Text;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

[Flags]
public enum SqlAccLiveCudSuite
{
    None = 0,
    GlAccount = 1,
    StockGroup = 2,
    GoodsReceived = 4,
    ItemUom = 8,
    /// <summary>AP_SUPPLIER ControlAccount resolve + create/read/delete (no hardcoded 400-000).</summary>
    Supplier = 16,
    All = GlAccount | StockGroup | GoodsReceived | ItemUom
}

public sealed record SqlAccLiveCudRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    string BackupPath,
    bool AuthorizeLanRead,
    string AuthorizedExactTarget,
    SqlAccLiveCudSuite Suite = SqlAccLiveCudSuite.All,
    string? VerifyOtherDatabaseFileName = null,
    string? SupervisorManagedBackupConfirm = null);

public sealed record SqlAccLiveCudStepResult(
    string Operation,
    bool Ok,
    string Detail,
    string? Code = null,
    string? DocKey = null,
    string? DocNo = null,
    bool OrphanPossible = false,
    bool Blocked = false);

public sealed record SqlAccLiveCudResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    string DatabaseFileName,
    bool LoginOk = false,
    bool LogoutOk = false,
    bool ComCleanupOk = false,
    bool IsolationOk = false,
    IReadOnlyList<SqlAccLiveCudStepResult>? Steps = null);

/// <summary>
/// Isolated live COM CUD verification for GL_ACC / ST_GROUP / PH_GR / ST_ITEM.cdsUOM.
/// Uses <see cref="SqlAccBizObjectWriter"/> only. No REST and no Firebird DML.
/// Requires backup evidence + LAN authorization before Login.
/// </summary>
public sealed class SqlAccSdkLiveCudVerificationProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkLiveCudVerificationProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccLiveCudResult> RunAsync(
        SqlAccLiveCudRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SqlAccWritePocSafety.EnsureRestorableBackup(
                request.DatabaseFileName,
                request.BackupPath,
                request.SupervisorManagedBackupConfirm);
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath,
                request.DatabaseFileName,
                request.AuthorizeLanRead,
                request.AuthorizedExactTarget);
            if (!string.IsNullOrWhiteSpace(request.VerifyOtherDatabaseFileName))
            {
                SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                    request.DcfPath,
                    request.VerifyOtherDatabaseFileName!,
                    request.AuthorizeLanRead,
                    request.AuthorizedExactTarget);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccLiveCudResult(
                Success: false,
                SafetyBlocked: true,
                Message: ex.Message,
                DatabaseFileName: request.DatabaseFileName));
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Task.FromResult(new SqlAccLiveCudResult(
                false, false, "UserName and Password are required.", request.DatabaseFileName));
        }

        return _sta.RunAsync(() => ExecuteOnSta(request), cancellationToken);
    }

    private SqlAccLiveCudResult ExecuteOnSta(SqlAccLiveCudRequest request)
    {
        var steps = new List<SqlAccLiveCudStepResult>();
        ISqlAccComApp? app = null;
        var loginOk = false;
        var logoutOk = false;
        var cleanupOk = false;
        var isolationOk = true;
        var failed = false;
        var message = "";
        // Time + 4-digit random keeps CODE lengths reasonable while avoiding collisions.
        var stamp = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var suffix = Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

        try
        {
            app = _factory.Create();
            app.Login(request.UserName.Trim(), request.Password, request.DcfPath.Trim(), request.DatabaseFileName.Trim());
            loginOk = app.IsLogin;
            if (!loginOk)
            {
                failed = true;
                message = "Login failed (IsLogin=false).";
            }
            else
            {
                // Pass object (not dynamic) into suite helpers so returns stay statically typed.
                // A dynamic argument would bind the whole call at runtime and break Nullable<T>.Value.
                object nativeObj = SqlAccBizObjectWriter.NativeApp(app);

                // Read-only preflight before any CUD when running the focused fix suites.
                if (request.Suite.HasFlag(SqlAccLiveCudSuite.GlAccount)
                    || request.Suite.HasFlag(SqlAccLiveCudSuite.Supplier))
                {
                    List<SqlAccLiveCudStepResult> pre = RunReadOnlyPreflight(nativeObj, request.Suite);
                    steps.AddRange(pre);
                    if (pre.Exists(static s => !s.Ok))
                    {
                        failed = true;
                        message = pre.Find(static s => !s.Ok)!.Detail;
                    }
                }

                // GL and Supplier are independent: a failure in one must not skip the other,
                // and must not trigger an automatic retry of the failed suite.
                if (request.Suite.HasFlag(SqlAccLiveCudSuite.GlAccount))
                {
                    List<SqlAccLiveCudStepResult> gl = RunGlAccount(nativeObj, stamp, suffix);
                    steps.AddRange(gl);
                    if (gl.Exists(static s => !s.Ok && !s.Blocked))
                    {
                        failed = true;
                        message = gl.Find(static s => !s.Ok && !s.Blocked)!.Detail;
                    }
                    else if (gl.Exists(static s => s.Blocked))
                    {
                        failed = true;
                        message = gl.Find(static s => s.Blocked)!.Detail;
                    }
                }

                if (request.Suite.HasFlag(SqlAccLiveCudSuite.Supplier))
                {
                    List<SqlAccLiveCudStepResult> ap = RunSupplier(nativeObj, stamp, suffix);
                    steps.AddRange(ap);
                    if (ap.Exists(static s => !s.Ok && !s.Blocked))
                    {
                        failed = true;
                        if (string.IsNullOrWhiteSpace(message))
                            message = ap.Find(static s => !s.Ok && !s.Blocked)!.Detail;
                        else
                            message += " | " + ap.Find(static s => !s.Ok && !s.Blocked)!.Detail;
                    }
                    else if (ap.Exists(static s => s.Blocked))
                    {
                        failed = true;
                        var blockedDetail = ap.Find(static s => s.Blocked)!.Detail;
                        if (string.IsNullOrWhiteSpace(message))
                            message = blockedDetail;
                        else
                            message += " | " + blockedDetail;
                    }
                }

                if (!failed && request.Suite.HasFlag(SqlAccLiveCudSuite.StockGroup))
                {
                    List<SqlAccLiveCudStepResult> grp = RunStockGroup(nativeObj, stamp, suffix);
                    steps.AddRange(grp);
                    if (grp.Exists(static s => !s.Ok))
                    {
                        failed = true;
                        message = grp.Find(static s => !s.Ok)!.Detail;
                    }
                }

                if (!failed && request.Suite.HasFlag(SqlAccLiveCudSuite.GoodsReceived))
                {
                    List<SqlAccLiveCudStepResult> gr = RunGoodsReceived(nativeObj, stamp, suffix);
                    steps.AddRange(gr);
                    if (gr.Exists(static s => s.Blocked))
                    {
                        // Blocked GR is a reported limitation, not a hard suite failure unless create was attempted badly.
                        message = string.IsNullOrWhiteSpace(message)
                            ? gr.Find(static s => s.Blocked)!.Detail
                            : message;
                    }
                    else if (gr.Exists(static s => !s.Ok))
                    {
                        failed = true;
                        message = gr.Find(static s => !s.Ok)!.Detail;
                    }
                }

                if (!failed && request.Suite.HasFlag(SqlAccLiveCudSuite.ItemUom))
                {
                    List<SqlAccLiveCudStepResult> uom = RunItemUom(nativeObj, stamp, suffix);
                    steps.AddRange(uom);
                    if (uom.Exists(static s => !s.Ok && !s.Blocked))
                    {
                        failed = true;
                        message = uom.Find(static s => !s.Ok && !s.Blocked)!.Detail;
                    }
                }

                if (!failed && string.IsNullOrWhiteSpace(message))
                    message = "Live CUD verification completed for " + request.DatabaseFileName + ".";
            }

            if (app.IsLogin)
            {
                app.Logout();
                logoutOk = !app.IsLogin;
            }
        }
        catch (Exception ex)
        {
            failed = true;
            message = "Live CUD verification failed: " + ex.GetType().Name + ": " + ex.Message;
            steps.Add(new SqlAccLiveCudStepResult("EXCEPTION", false, message, OrphanPossible: true));
            try
            {
                if (app?.IsLogin == true)
                {
                    app.Logout();
                    logoutOk = !app.IsLogin;
                }
            }
            catch { /* ignore */ }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    if (app.IsLogin)
                    {
                        app.Logout();
                        logoutOk = !app.IsLogin;
                    }
                }
                catch { /* ignore */ }

                try
                {
                    app.Dispose();
                    cleanupOk = true;
                }
                catch { cleanupOk = false; }
            }
        }

        // Cross-company isolation: POC codes must not exist on the other FDB.
        if (!string.IsNullOrWhiteSpace(request.VerifyOtherDatabaseFileName) && loginOk)
        {
            isolationOk = VerifyIsolationOnOther(request, steps, out var isoDetail);
            steps.Add(new SqlAccLiveCudStepResult(
                "ISOLATION",
                isolationOk,
                isoDetail));
            if (!isolationOk)
            {
                failed = true;
                message = isoDetail;
            }
        }

        var success = !failed && loginOk && logoutOk && cleanupOk && isolationOk
                      && steps.Where(s => !s.Blocked).All(s => s.Ok);
        return new SqlAccLiveCudResult(
            Success: success,
            SafetyBlocked: false,
            Message: message,
            DatabaseFileName: request.DatabaseFileName,
            LoginOk: loginOk,
            LogoutOk: logoutOk,
            ComCleanupOk: cleanupOk,
            IsolationOk: isolationOk,
            Steps: steps);
    }

    private static List<SqlAccLiveCudStepResult> RunReadOnlyPreflight(object nativeApp, SqlAccLiveCudSuite suite)
    {
        var steps = new List<SqlAccLiveCudStepResult>();
        var warm = QueryFirst(nativeApp, "SELECT COUNT(*) AS CNT FROM RDB$DATABASE");
        var cnt = Field(warm, "CNT");
        steps.Add(new SqlAccLiveCudStepResult(
            "PREFLIGHT.select",
            !string.IsNullOrWhiteSpace(cnt),
            "SELECT-only warm CNT=" + cnt));

        if (suite.HasFlag(SqlAccLiveCudSuite.GlAccount))
        {
            var meta = QueryFirst(nativeApp,
                "SELECT FIRST 1 TRIM(CODE) AS CODE, CHARACTER_LENGTH(CODE) AS CODELEN FROM GL_ACC ORDER BY CODE");
            var sample = Field(meta, "CODE");
            var len = Field(meta, "CODELEN");
            var lenOk = int.TryParse(len, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        && n <= SqlAccBizObjectWriter.GlAccountCodeMaxLength;
            steps.Add(new SqlAccLiveCudStepResult(
                "PREFLIGHT.gl-code-limit",
                !string.IsNullOrWhiteSpace(sample) && (lenOk || string.IsNullOrWhiteSpace(len)),
                "Sample GL CODE='" + sample + "' CHARACTER_LENGTH=" + len +
                "; writer max=" + SqlAccBizObjectWriter.GlAccountCodeMaxLength,
                sample));
        }

        if (suite.HasFlag(SqlAccLiveCudSuite.Supplier))
        {
            var control = ResolveSupplierControlAccount(nativeApp);
            steps.Add(new SqlAccLiveCudStepResult(
                "PREFLIGHT.supplier-control",
                !string.IsNullOrWhiteSpace(control),
                string.IsNullOrWhiteSpace(control)
                    ? "No valid AP ControlAccount resolved from destination (no hardcoded fallback)."
                    : "Resolved destination AP ControlAccount=" + control,
                control,
                Blocked: string.IsNullOrWhiteSpace(control)));
        }

        return steps;
    }

    private static List<SqlAccLiveCudStepResult> RunSupplier(object nativeApp, string stamp, string suffix)
    {
        var steps = new List<SqlAccLiveCudStepResult>();
        var code = "SDKPOC-S" + suffix;
        if (!SqlAccWritePocSafety.LooksLikePocAgentCode(code))
        {
            steps.Add(new SqlAccLiveCudStepResult("AP_SUPPLIER", false, "Refusing non-SDKPOC code.", code, Blocked: true));
            return steps;
        }

        var expectedControl = ResolveSupplierControlAccount(nativeApp);
        if (string.IsNullOrWhiteSpace(expectedControl))
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "AP_SUPPLIER.resolve-control",
                false,
                "Could not resolve a valid ControlAccount from MIRACLE AP_SUPPLIER/GL_ACC.",
                Blocked: true));
            return steps;
        }

        steps.Add(new SqlAccLiveCudStepResult(
            "AP_SUPPLIER.resolve-control",
            true,
            "Dynamic ControlAccount=" + expectedControl,
            expectedControl));

        // Leave ControlAccount empty so UpsertParty resolves from destination (no hardcoded 400-000).
        var partner = new BusinessPartner
        {
            CompanyName = "SDK POC Supplier " + code,
            ControlAccount = "",
            Area = "----",
            Agent = "----",
            CreditTerm = "30 Days",
            CurrencyCode = "----",
            Status = "A"
        };

        var create = SqlAccBizObjectWriter.UpsertParty(
            nativeApp, SqlAccBizObjectWriter.SupplierBiz, partner, code, "SDKPOC-" + stamp);
        steps.Add(new SqlAccLiveCudStepResult(
            "AP_SUPPLIER.create",
            create.Success,
            create.Message,
            code,
            create.DocKey,
            OrphanPossible: create.Success));
        if (!create.Success)
            return steps;

        var safe = code.Replace("'", "''", StringComparison.Ordinal);
        var row = QueryFirst(nativeApp,
            "SELECT FIRST 1 TRIM(CODE) AS CODE, TRIM(CONTROLACCOUNT) AS CONTROLACCOUNT, TRIM(COMPANYNAME) AS COMPANYNAME " +
            "FROM AP_SUPPLIER WHERE TRIM(CODE) = '" + safe + "'");
        if (row is null)
            row = ReadSupplierViaBiz(nativeApp, code);
        var keyPresent = SupplierExists(nativeApp, code);
        var persistedControl = Field(row, "CONTROLACCOUNT");
        var controlOk = !string.IsNullOrWhiteSpace(persistedControl)
                        && string.Equals(persistedControl, expectedControl, StringComparison.OrdinalIgnoreCase);
        steps.Add(new SqlAccLiveCudStepResult(
            "AP_SUPPLIER.read-control",
            keyPresent && controlOk,
            !keyPresent
                ? "Supplier missing after create (FindKey/SQL)."
                : "Persisted CODE=" + (Field(row, "CODE").Length > 0 ? Field(row, "CODE") : code) +
                  "; ControlAccount=" + persistedControl +
                  "; CompanyName=" + Field(row, "COMPANYNAME") +
                  (controlOk ? "" : " (expected " + expectedControl + ")"),
            code,
            OrphanPossible: true));
        if (!keyPresent || !controlOk)
            return steps;

        var del = SqlAccBizObjectWriter.DeleteByCode(
            nativeApp, SqlAccBizObjectWriter.SupplierBiz, code);
        steps.Add(new SqlAccLiveCudStepResult(
            "AP_SUPPLIER.delete-poc",
            del.Success,
            del.Message,
            code,
            OrphanPossible: !del.Success));
        if (!del.Success)
            return steps;

        var gone = !SupplierExists(nativeApp, code)
                   && QueryFirst(nativeApp,
                       "SELECT FIRST 1 TRIM(CODE) AS CODE FROM AP_SUPPLIER WHERE TRIM(CODE) = '" + safe + "'") is null;
        steps.Add(new SqlAccLiveCudStepResult(
            "AP_SUPPLIER.verify-deleted",
            gone,
            gone ? "POC supplier absent after delete." : "POC supplier still present after delete.",
            code,
            OrphanPossible: !gone));
        return steps;
    }

    private static bool SupplierExists(object nativeApp, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find(SqlAccBizObjectWriter.SupplierBiz);
            object? key = biz.FindKeyByRef("CODE", code);
            return SqlAccBizObjectWriter.IsPresentKey(key);
        }
        catch { return false; }
    }

    private static Dictionary<string, string>? ReadSupplierViaBiz(object nativeApp, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find(SqlAccBizObjectWriter.SupplierBiz);
            object? key = biz.FindKeyByRef("CODE", code);
            if (!SqlAccBizObjectWriter.IsPresentKey(key))
                return null;
            try { biz.Params.Find("Code").Value = key; }
            catch { biz.Params.Find("CODE").Value = key; }
            biz.Open();
            try
            {
                dynamic main = biz.DataSets.Find("MainDataSet");
                string Read(string field)
                {
                    try
                    {
                        return (Convert.ToString(main.FindField(field).AsString)
                                ?? Convert.ToString(main.FindField(field).value)
                                ?? "").Trim();
                    }
                    catch { return ""; }
                }

                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CODE"] = Read("CODE"),
                    ["CONTROLACCOUNT"] = Read("ControlAccount").Length > 0 ? Read("ControlAccount") : Read("CONTROLACCOUNT"),
                    ["COMPANYNAME"] = Read("CompanyName").Length > 0 ? Read("CompanyName") : Read("COMPANYNAME")
                };
            }
            finally
            {
                try { biz.Close(); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSupplierControlAccount(object nativeApp)
    {
        var rows = QueryAll(nativeApp,
            "SELECT DISTINCT TRIM(CONTROLACCOUNT) AS CONTROLACCOUNT FROM AP_SUPPLIER " +
            "WHERE CONTROLACCOUNT IS NOT NULL AND TRIM(CONTROLACCOUNT) <> ''");
        foreach (var row in rows)
        {
            var control = Field(row, "CONTROLACCOUNT");
            if (string.IsNullOrWhiteSpace(control))
                continue;
            var safe = control.Replace("'", "''", StringComparison.Ordinal);
            var exists = QueryFirst(nativeApp,
                "SELECT FIRST 1 TRIM(CODE) AS CODE FROM GL_ACC WHERE TRIM(CODE) = '" + safe + "'");
            if (!string.IsNullOrWhiteSpace(Field(exists, "CODE")))
                return control;
        }

        return "";
    }

    private static List<SqlAccLiveCudStepResult> RunGlAccount(object nativeApp, string stamp, string suffix)
    {
        // Keep nativeApp as object for all helper/writer calls — dynamic args make Nullable<ValueTuple>.Value throw.
        var steps = new List<SqlAccLiveCudStepResult>();
        // GL_ACC.CODE CHARACTER_LENGTH=10 — keep within limit (SDKPOC + 4 unique digits).
        var code = "SDKPOC" + (stamp + suffix)[^4..];
        if (code.Length > SqlAccBizObjectWriter.GlAccountCodeMaxLength)
            code = code[..SqlAccBizObjectWriter.GlAccountCodeMaxLength];
        if (!SqlAccWritePocSafety.LooksLikePocAgentCode(code))
        {
            steps.Add(new SqlAccLiveCudStepResult("GL_ACC", false, "Refusing non-SDKPOC code.", code, Blocked: true));
            return steps;
        }

        var parent = FindParentAccount(nativeApp);
        if (parent is null)
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "GL_ACC.find-parent",
                false,
                "No suitable parent GL account found (PARENT=0 or first account). Stopping GL suite.",
                Blocked: true));
            return steps;
        }

        var parentRow = parent.GetValueOrDefault();
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.parent",
            true,
            $"Using parent CODE={parentRow.Code} DOCKEY={parentRow.DocKey}",
            parentRow.Code,
            parentRow.DocKey.ToString(CultureInfo.InvariantCulture)));

        var create = SqlAccBizObjectWriter.UpsertGlAccount(
            nativeApp, code, "SDK POC GL " + code, parentRow.DocKey, 0,
            "SDKPOC", "SA", "", "", 0, "", false);
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.create",
            create.Success,
            create.Message,
            code,
            create.DocKey,
            OrphanPossible: create.Success));
        if (!create.Success)
            return steps;

        var persistedCode = string.IsNullOrWhiteSpace(create.DocNo) ? code : create.DocNo.Trim();
        var persistedDocKey = 0;
        _ = int.TryParse(create.DocKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistedDocKey);
        // Prefer DOCKEY from Save result — FindKeyByRef(CODE) can miss CHAR-padded rows right after create.
        var read1 = persistedDocKey > 0
            ? ReadGlAccountByDocKey(nativeApp, persistedDocKey)
            : ReadGlAccount(nativeApp, persistedCode);
        if (read1 is null)
            read1 = ReadGlAccount(nativeApp, persistedCode);
        if (read1 is null)
            read1 = ReadGlAccountFromSql(nativeApp, persistedCode, persistedDocKey);
        if (read1 is null)
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "GL_ACC.read-after-create",
                false,
                "Created but could not open account for readback (CODE=" + persistedCode +
                ", DOCKEY=" + create.DocKey + ").",
                persistedCode,
                create.DocKey,
                OrphanPossible: true));
            return steps;
        }

        var created = read1.GetValueOrDefault();
        var dockeyOk = created.DocKey > 0;
        var parentOk = created.Parent == parentRow.DocKey;
        var descOk = string.Equals(created.Description.Trim(), ("SDK POC GL " + code).Trim(), StringComparison.Ordinal);
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.read-after-create",
            dockeyOk && parentOk && descOk,
            $"CODE={persistedCode}; DOCKEY={created.DocKey}; PARENT={created.Parent}; DESC={created.Description}; ACCTYPE={created.AccType}",
            persistedCode,
            created.DocKey.ToString(CultureInfo.InvariantCulture),
            OrphanPossible: true));
        if (!(dockeyOk && parentOk && descOk))
            return steps;

        var updateDesc = "SDK POC GL UPD " + persistedCode;
        var update = SqlAccBizObjectWriter.UpsertGlAccount(
            nativeApp, persistedCode, updateDesc, parentRow.DocKey, created.DocKey,
            "SDKPOC-UPD", created.AccType, "", "", 0, "", true);
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.update",
            update.Success,
            update.Message,
            persistedCode,
            created.DocKey.ToString(CultureInfo.InvariantCulture),
            OrphanPossible: true));
        if (!update.Success)
            return steps;

        var read2 = ReadGlAccountByDocKey(nativeApp, created.DocKey)
                    ?? ReadGlAccount(nativeApp, persistedCode)
                    ?? ReadGlAccountFromSql(nativeApp, persistedCode, created.DocKey);
        var updOk = false;
        string updDetail;
        if (read2 is null)
        {
            updDetail = "Missing after update.";
        }
        else
        {
            var row = read2.GetValueOrDefault();
            updOk = string.Equals(row.Description.Trim(), updateDesc, StringComparison.Ordinal)
                    && row.Parent == parentRow.DocKey;
            updDetail = $"DESC={row.Description}; PARENT={row.Parent}; DOCKEY={row.DocKey}";
        }

        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.read-after-update",
            updOk,
            updDetail,
            persistedCode,
            read2?.DocKey.ToString(CultureInfo.InvariantCulture),
            OrphanPossible: true));
        if (!updOk)
            return steps;

        // Read-only parent protection: never call Delete on existing business/parent accounts.
        var childRows = QueryFirst(nativeApp,
            "SELECT COUNT(*) AS CNT FROM GL_ACC WHERE PARENT = " +
            parentRow.DocKey.ToString(CultureInfo.InvariantCulture));
        _ = int.TryParse(Field(childRows, "CNT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var childCnt);
        var parentGuarded = childCnt > 0;
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.parent-protection-readonly",
            parentGuarded,
            parentGuarded
                ? "Parent DOCKEY=" + parentRow.DocKey + " CODE=" + parentRow.Code +
                  " has " + childCnt + " child row(s); delete of parent is not attempted."
                : "Parent has no children in SELECT — unexpected after POC create.",
            parentRow.Code,
            parentRow.DocKey.ToString(CultureInfo.InvariantCulture)));
        if (!parentGuarded)
            return steps;

        var del = SqlAccBizObjectWriter.DeleteGlAccount(nativeApp, persistedCode, created.DocKey);
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.delete-poc",
            del.Success,
            del.Message,
            persistedCode,
            created.DocKey.ToString(CultureInfo.InvariantCulture),
            OrphanPossible: !del.Success));
        if (!del.Success)
            return steps;

        var gone = ReadGlAccount(nativeApp, persistedCode) is null
                   && ReadGlAccountFromSql(nativeApp, persistedCode, created.DocKey) is null;
        steps.Add(new SqlAccLiveCudStepResult(
            "GL_ACC.verify-deleted",
            gone,
            gone ? "POC account absent after delete." : "POC account still present after delete.",
            persistedCode,
            OrphanPossible: !gone));
        return steps;
    }

    private static List<SqlAccLiveCudStepResult> RunStockGroup(object nativeApp, string stamp, string suffix)
    {
        dynamic native = nativeApp;
        var steps = new List<SqlAccLiveCudStepResult>();
        var code = "SDKPOC-GRP-" + stamp + suffix;
        var group = new StockGroupRecord
        {
            Code = code,
            Description = "SDK POC group " + code,
            CostingMethod = 1,
            IsActive = true
        };

        var create = SqlAccBizObjectWriter.UpsertStockGroup(native, group);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_GROUP.create",
            create.Success,
            create.Message,
            code,
            OrphanPossible: create.Success && !create.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)));
        if (!create.Success)
            return steps;

        var exists = StockGroupExists(nativeApp, code);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_GROUP.read-after-create",
            exists,
            exists ? "Stock group exists via FindKeyByRef." : "Stock group missing after create.",
            code,
            OrphanPossible: true));
        if (!exists)
            return steps;

        var dup = SqlAccBizObjectWriter.UpsertStockGroup(native, group);
        var dupOk = dup.Success
                    && dup.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_GROUP.duplicate",
            dupOk,
            dup.Message,
            code));

        // Leave SDKPOC stock group in place (unused) — safer than delete without verified unused check.
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_GROUP.cleanup-note",
            true,
            "POC stock group left in place for inspection (unused; unique SDKPOC code). Manual delete optional.",
            code));
        return steps;
    }

    private static List<SqlAccLiveCudStepResult> RunGoodsReceived(object nativeApp, string stamp, string suffix)
    {
        dynamic native = nativeApp;
        var steps = new List<SqlAccLiveCudStepResult>();

        var supplier = QueryFirst(nativeApp,
            "SELECT FIRST 1 TRIM(CODE) AS CODE, TRIM(COALESCE(COMPANYNAME, CODE)) AS COMPANYNAME FROM AP_SUPPLIER ORDER BY CODE");
        if (supplier is null || string.IsNullOrWhiteSpace(Field(supplier, "CODE")))
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "PH_GR.precheck-supplier",
                false,
                "No AP_SUPPLIER row found. Stopping PH_GR create (missing mandatory supplier).",
                Blocked: true));
            return steps;
        }

        var item = QueryFirst(nativeApp,
            "SELECT FIRST 1 TRIM(CODE) AS CODE FROM ST_ITEM ORDER BY CODE");
        if (item is null || string.IsNullOrWhiteSpace(Field(item, "CODE")))
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "PH_GR.precheck-item",
                false,
                "No ST_ITEM row found. Stopping PH_GR create (missing mandatory item).",
                Blocked: true));
            return steps;
        }

        steps.Add(new SqlAccLiveCudStepResult(
            "PH_GR.precheck",
            true,
            $"Supplier={Field(supplier, "CODE")}; Item={Field(item, "CODE")}. FROM* links not set (no destination-resolved DO keys)."));

        var doc = new DocumentHeader
        {
            DocNo = "--",
            DocDate = DateTime.Today,
            PostDate = DateTime.Today,
            TaxDate = DateTime.Today,
            PartyCode = Field(supplier, "CODE"),
            PartyName = Field(supplier, "COMPANYNAME"),
            Description = "SDKPOC GR " + stamp + suffix,
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
            Lines =
            [
                new DocumentLine
                {
                    Seq = 1,
                    ItemCode = Field(item, "CODE"),
                    Description = "SDKPOC GR line",
                    Qty = 1m,
                    Uom = "UNIT",
                    Rate = "1",
                    UnitPrice = 1m,
                    Amount = 1m,
                    Warehouse = "----"
                }
            ]
        };

        var create = SqlAccBizObjectWriter.CreateGoodsReceived(native, doc, "SDKPOC-" + stamp);
        steps.Add(new SqlAccLiveCudStepResult(
            "PH_GR.create",
            create.Success,
            create.Message,
            DocKey: create.DocKey,
            DocNo: create.DocNo,
            OrphanPossible: create.Success));
        if (!create.Success)
            return steps;

        var read = ReadGoodsReceived(nativeApp, create.DocNo, create.DocKey, Field(supplier, "CODE"));
        steps.Add(new SqlAccLiveCudStepResult(
            "PH_GR.read-after-create",
            read.Ok,
            read.Detail,
            DocKey: create.DocKey,
            DocNo: create.DocNo,
            OrphanPossible: true));

        steps.Add(new SqlAccLiveCudStepResult(
            "PH_GR.from-link-note",
            true,
            "FROMDOCTYPE/FROMDOCKEY/FROMDTLKEY were not set: destination DO keys were not available. " +
            "Standalone PH_GR create verified without transfer links.",
            DocNo: create.DocNo));

        steps.Add(new SqlAccLiveCudStepResult(
            "PH_GR.cleanup-note",
            true,
            "POC GR left for inspection (DocNo=" + create.DocNo + "). Manual cancel/delete in UI if required.",
            DocKey: create.DocKey,
            DocNo: create.DocNo,
            OrphanPossible: true));
        return steps;
    }

    private static List<SqlAccLiveCudStepResult> RunItemUom(object nativeApp, string stamp, string suffix)
    {
        dynamic native = nativeApp;
        var steps = new List<SqlAccLiveCudStepResult>();
        var groupCode = EnsureAnyStockGroup(nativeApp, steps);
        if (string.IsNullOrWhiteSpace(groupCode))
            return steps;

        var uomCode = "SP" + stamp[^4..] + suffix; // keep short; UOM width is often small
        if (uomCode.Length > 10)
            uomCode = "SP" + suffix + stamp[^4..];
        var itemCode = "SDKPOC-IT-" + stamp + suffix;
        var item = new StockItem
        {
            Code = itemCode,
            Description = "SDK POC item " + itemCode,
            StockGroup = groupCode,
            StockControl = true,
            IsActive = true,
            Uom = uomCode,
            RefCost = "1",
            RefPrice = "1",
            Uoms =
            [
                new StockItemUom
                {
                    Uom = uomCode,
                    Rate = "1",
                    RefCost = "1",
                    RefPrice = "1",
                    IsBase = true
                }
            ]
        };

        var create = SqlAccBizObjectWriter.UpsertStockItem(native, item, itemCode, "SDKPOC-" + stamp);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_ITEM.create-with-new-uom",
            create.Success,
            create.Message + $" UOM={uomCode}",
            itemCode,
            OrphanPossible: create.Success));
        if (!create.Success)
            return steps;

        var uomOnItem = ItemHasUom(nativeApp, itemCode, uomCode);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_ITEM.cdsUOM-readback",
            uomOnItem,
            uomOnItem ? $"cdsUOM contains {uomCode}." : $"cdsUOM missing {uomCode}.",
            itemCode,
            OrphanPossible: true));

        // Probe company-wide UOM availability via a second item using the same UOM.
        var item2 = "SDKPOC-IU-" + stamp + suffix;
        var second = new StockItem
        {
            Code = item2,
            Description = "SDK POC UOM reuse " + item2,
            StockGroup = groupCode,
            StockControl = true,
            IsActive = true,
            Uom = uomCode,
            Uoms =
            [
                new StockItemUom { Uom = uomCode, Rate = "1", RefCost = "0", RefPrice = "0", IsBase = true }
            ]
        };
        var reuse = SqlAccBizObjectWriter.UpsertStockItem(native, second, item2, "SDKPOC-" + stamp);
        steps.Add(new SqlAccLiveCudStepResult(
            "ST_ITEM.uom-reuse-on-second-item",
            reuse.Success,
            reuse.Success
                ? $"New UOM '{uomCode}' accepted on a second item (company-usable via cdsUOM; no standalone UOM master used)."
                : $"Second item with UOM '{uomCode}' failed: {reuse.Message}",
            item2,
            OrphanPossible: reuse.Success));

        // Standalone UOM BizObject probe (metadata only).
        var standalone = false;
        try
        {
            dynamic? biz = null;
            foreach (var name in new[] { "ST_UOM", "UOM", "ST_UNIT", "MaintainUOM" })
            {
                try
                {
                    biz = native.BizObjects.Find(name);
                    if (biz is not null)
                    {
                        standalone = true;
                        steps.Add(new SqlAccLiveCudStepResult(
                            "UOM.standalone-bizobject",
                            true,
                            $"Found candidate BizObject '{name}' (not used for CUD in this suite).",
                            Blocked: false));
                        break;
                    }
                }
                catch { /* try next */ }
            }

            if (!standalone)
            {
                steps.Add(new SqlAccLiveCudStepResult(
                    "UOM.standalone-bizobject",
                    true,
                    "No standalone UOM BizObject found (ST_UOM/UOM/ST_UNIT). Limitation: cdsUOM-only.",
                    Blocked: true));
            }
        }
        catch (Exception ex)
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "UOM.standalone-bizobject",
                true,
                "Standalone UOM probe error (treated as unsupported): " + ex.Message,
                Blocked: true));
        }

        // Cleanup POC items (not groups).
        TryDeleteItem(nativeApp, itemCode, steps);
        if (reuse.Success)
            TryDeleteItem(nativeApp, item2, steps);

        return steps;
    }

    private static void TryDeleteItem(object nativeApp, string code, List<SqlAccLiveCudStepResult> steps)
    {
        try
        {
            dynamic native = nativeApp;
            var del = SqlAccBizObjectWriter.DeleteByCode(native, SqlAccBizObjectWriter.ItemBiz, code, "Dockey");
            steps.Add(new SqlAccLiveCudStepResult("ST_ITEM.delete-poc", del.Success, del.Message, code, OrphanPossible: !del.Success));
        }
        catch (Exception ex)
        {
            steps.Add(new SqlAccLiveCudStepResult(
                "ST_ITEM.delete-poc",
                false,
                "Delete failed: " + ex.Message,
                code,
                OrphanPossible: true));
        }
    }

    private static string EnsureAnyStockGroup(object nativeApp, List<SqlAccLiveCudStepResult> steps)
    {
        dynamic native = nativeApp;
        var existing = QueryFirst(nativeApp, "SELECT FIRST 1 TRIM(CODE) AS CODE FROM ST_GROUP ORDER BY CODE");
        var code = Field(existing, "CODE");
        if (!string.IsNullOrWhiteSpace(code))
        {
            steps.Add(new SqlAccLiveCudStepResult("ST_ITEM.prereq-group", true, "Using existing stock group " + code, code));
            return code;
        }

        var poc = "SDKPOC-G" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        var create = SqlAccBizObjectWriter.UpsertStockGroup(native, new StockGroupRecord
        {
            Code = poc,
            Description = poc,
            CostingMethod = 1,
            IsActive = true
        });
        steps.Add(new SqlAccLiveCudStepResult("ST_ITEM.prereq-group-create", create.Success, create.Message, poc));
        return create.Success ? poc : "";
    }

    private bool VerifyIsolationOnOther(
        SqlAccLiveCudRequest request,
        List<SqlAccLiveCudStepResult> steps,
        out string detail)
    {
        var codes = steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Code)
                        && s.Code!.StartsWith("SDKPOC", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            detail = "No SDKPOC codes to verify on other company.";
            return true;
        }

        ISqlAccComApp? other = null;
        try
        {
            other = _factory.Create();
            other.Login(
                request.UserName.Trim(),
                request.Password,
                request.DcfPath.Trim(),
                request.VerifyOtherDatabaseFileName!.Trim());
            if (!other.IsLogin)
            {
                detail = "Isolation check login failed on " + request.VerifyOtherDatabaseFileName;
                return false;
            }

            object nativeObj = SqlAccBizObjectWriter.NativeApp(other);
            var leaks = new List<string>();
            foreach (var code in codes)
            {
                if (ReadGlAccount(nativeObj, code) is not null)
                    leaks.Add("GL_ACC:" + code);
                if (StockGroupExists(nativeObj, code))
                    leaks.Add("ST_GROUP:" + code);
                if (ItemExists(nativeObj, code))
                    leaks.Add("ST_ITEM:" + code);
            }

            other.Logout();
            if (leaks.Count > 0)
            {
                detail = "Cross-company leak into " + request.VerifyOtherDatabaseFileName + ": " + string.Join(", ", leaks);
                return false;
            }

            detail = "No SDKPOC GL/ST_GROUP/ST_ITEM codes found on " + request.VerifyOtherDatabaseFileName + ".";
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

    private static (int DocKey, int Parent, string Code, string Description, string AccType)? FindParentAccount(object nativeApp)
    {
        var root = QueryFirst(nativeApp,
            "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC WHERE PARENT = 0 ORDER BY CODE");
        if (root is not null)
        {
            int dk;
            if (int.TryParse(Field(root, "DOCKEY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out dk) && dk > 0)
            {
                int p;
                _ = int.TryParse(Field(root, "PARENT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p);
                return (dk, p, Field(root, "CODE"), Field(root, "DESCRIPTION"), Field(root, "ACCTYPE"));
            }
        }

        var any = QueryFirst(nativeApp,
            "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC ORDER BY CODE");
        if (any is not null)
        {
            int dk2;
            if (int.TryParse(Field(any, "DOCKEY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out dk2) && dk2 > 0)
            {
                int p2;
                _ = int.TryParse(Field(any, "PARENT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p2);
                return (dk2, p2, Field(any, "CODE"), Field(any, "DESCRIPTION"), Field(any, "ACCTYPE"));
            }
        }

        return null;
    }

    private static (int DocKey, int Parent, string Description, string AccType)? ReadGlAccount(object nativeApp, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = FindBiz(native, "GL_ACC", "GL_Acc");
            object? key = null;
            try { key = biz.FindKeyByRef("CODE", code); } catch { key = null; }
            if (!SqlAccBizObjectWriter.IsPresentKey(key)) return null;
            return OpenGlAccount(biz, key, code);
        }
        catch
        {
            return null;
        }
    }

    private static (int DocKey, int Parent, string Description, string AccType)? ReadGlAccountByDocKey(
        object nativeApp, int docKey)
    {
        if (docKey <= 0)
            return null;
        try
        {
            dynamic native = nativeApp;
            dynamic biz = FindBiz(native, "GL_ACC", "GL_Acc");
            return OpenGlAccount(biz, docKey, codeHint: null);
        }
        catch
        {
            return null;
        }
    }

    private static (int DocKey, int Parent, string Description, string AccType)? ReadGlAccountFromSql(
        object nativeApp, string code, int docKey)
    {
        Dictionary<string, string>? row = null;
        if (docKey > 0)
        {
            row = QueryFirst(nativeApp,
                "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, " +
                "TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC WHERE DOCKEY = " +
                docKey.ToString(CultureInfo.InvariantCulture));
        }

        if (row is null && !string.IsNullOrWhiteSpace(code))
        {
            var safe = code.Replace("'", "''", StringComparison.Ordinal);
            row = QueryFirst(nativeApp,
                "SELECT FIRST 1 DOCKEY, PARENT, TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, " +
                "TRIM(COALESCE(ACCTYPE,'')) AS ACCTYPE FROM GL_ACC WHERE TRIM(CODE) = '" + safe + "'");
        }

        if (row is null)
            return null;
        if (!int.TryParse(Field(row, "DOCKEY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dk) || dk <= 0)
            return null;
        _ = int.TryParse(Field(row, "PARENT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent);
        return (dk, parent, Field(row, "DESCRIPTION"), Field(row, "ACCTYPE"));
    }

    private static (int DocKey, int Parent, string Description, string AccType)? OpenGlAccount(
        dynamic biz, object key, string? codeHint = null)
    {
        try
        {
            if (!SqlAccBizObjectWriter.TryOpenGlAccount((object)biz, key, codeHint, out string? _))
                return null;
            try
            {
                dynamic main = biz.DataSets.Find("MainDataSet");
                var dockey = Convert.ToInt32(main.FindField("DOCKEY").value);
                var parent = Convert.ToInt32(main.FindField("PARENT").value);
                var desc = Convert.ToString(main.FindField("DESCRIPTION").AsString)
                           ?? Convert.ToString(main.FindField("DESCRIPTION").value)
                           ?? "";
                var accType = Convert.ToString(main.FindField("ACCTYPE").AsString)
                              ?? Convert.ToString(main.FindField("ACCTYPE").value)
                              ?? "";
                var openedCode = Convert.ToString(main.FindField("CODE").AsString)
                                 ?? Convert.ToString(main.FindField("CODE").value)
                                 ?? "";
                if (SqlAccBizObjectWriter.ValidateOpenedGlIdentity(
                        key is int ik ? ik : 0,
                        codeHint,
                        dockey,
                        openedCode) is not null)
                    return null;
                return (dockey, parent, desc, accType);
            }
            finally
            {
                try { biz.Close(); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool StockGroupExists(object nativeApp, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find("ST_GROUP");
            object? key = biz.FindKeyByRef("CODE", code);
            return SqlAccBizObjectWriter.IsPresentKey(key);
        }
        catch { return false; }
    }

    private static bool ItemExists(object nativeApp, string code)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find("ST_ITEM");
            object? key = biz.FindKeyByRef("CODE", code);
            return SqlAccBizObjectWriter.IsPresentKey(key);
        }
        catch { return false; }
    }

    private static bool ItemHasUom(object nativeApp, string itemCode, string uomCode)
    {
        try
        {
            dynamic native = nativeApp;
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
            finally
            {
                try { biz.Close(); } catch { /* ignore */ }
            }
        }
        catch { return false; }
    }

    private static (bool Ok, string Detail) ReadGoodsReceived(object nativeApp, string? docNo, string? docKey, string party)
    {
        try
        {
            dynamic native = nativeApp;
            dynamic biz = native.BizObjects.Find("PH_GR");
            object? key = null;
            if (!string.IsNullOrWhiteSpace(docKey) && int.TryParse(docKey, out var dk) && dk > 0)
                key = dk;
            if (!SqlAccBizObjectWriter.IsPresentKey(key) && !string.IsNullOrWhiteSpace(docNo) && docNo is not "--" and not "--SYNC--")
            {
                try { key = biz.FindKeyByRef("DocNo", docNo); } catch { /* ignore */ }
                if (!SqlAccBizObjectWriter.IsPresentKey(key))
                {
                    try { key = biz.FindKeyByRef("DocNo;Code", new object[] { docNo!, party }); } catch { /* ignore */ }
                }
            }

            if (!SqlAccBizObjectWriter.IsPresentKey(key))
                return (false, "Could not resolve PH_GR key for readback (DocNo=" + docNo + ", DocKey=" + docKey + ").");

            try { biz.Params.Find("Dockey").Value = key; }
            catch { biz.Params.Find("DocKey").Value = key; }
            biz.Open();
            try
            {
                dynamic main = biz.DataSets.Find("MainDataSet");
                dynamic detail = biz.DataSets.Find("cdsDocDetail");
                var actualNo = Convert.ToString(main.FindField("DOCNO").AsString)
                               ?? Convert.ToString(main.FindField("DocNo").value)
                               ?? docNo
                               ?? "";
                var code = Convert.ToString(main.FindField("CODE").AsString)
                           ?? Convert.ToString(main.FindField("Code").value)
                           ?? "";
                var lineCount = Convert.ToInt32(detail.RecordCount);
                double qty = 0, recv = 0, price = 0;
                var uom = "";
                if (lineCount > 0)
                {
                    detail.First();
                    qty = Convert.ToDouble(detail.FindField("QTY").value);
                    try { recv = Convert.ToDouble(detail.FindField("RECEIVEQTY").value); } catch { recv = qty; }
                    price = Convert.ToDouble(detail.FindField("UNITPRICE").value);
                    uom = Convert.ToString(detail.FindField("UOM").AsString)
                          ?? Convert.ToString(detail.FindField("UOM").value)
                          ?? "";
                }

                var sb = new StringBuilder();
                sb.Append("DocNo=").Append(actualNo.Trim());
                sb.Append("; Code=").Append(code.Trim());
                sb.Append("; Lines=").Append(lineCount);
                sb.Append("; Qty=").Append(qty);
                sb.Append("; ReceiveQty=").Append(recv);
                sb.Append("; UOM=").Append(uom.Trim());
                sb.Append("; UnitPrice=").Append(price);
                var ok = lineCount >= 1 && qty > 0 && recv > 0 && !string.IsNullOrWhiteSpace(actualNo)
                         && actualNo.Trim() is not "--";
                return (ok, sb.ToString());
            }
            finally
            {
                try { biz.Close(); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return (false, "PH_GR readback failed: " + ex.Message);
        }
    }

    private static Dictionary<string, string>? QueryFirst(object nativeApp, string sql)
    {
        var rows = QueryAll(nativeApp, sql);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static List<Dictionary<string, string>> QueryAll(object nativeApp, string sql)
    {
        SqlGuard.EnsureSelectOnly(sql);
        dynamic native = nativeApp;
        var list = new List<Dictionary<string, string>>();
        dynamic? ds = null;
        try
        {
            ds = native.DBManager.NewDataSet(sql);
            var count = Convert.ToInt32(ds.RecordCount);
            if (count <= 0)
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
        catch
        {
            /* return what we have */
        }
        finally
        {
            if (ds is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ds); } catch { /* ignore */ }
            }
        }

        return list;
    }

    private static string Field(Dictionary<string, string>? row, string name)
    {
        if (row is null || !row.ContainsKey(name))
            return "";
        return row[name] ?? "";
    }

    private static dynamic FindBiz(dynamic app, params string[] names)
    {
        Exception? last = null;
        foreach (var name in names)
        {
            try { return app.BizObjects.Find(name); }
            catch (Exception ex) { last = ex; }
        }

        throw new InvalidOperationException("BizObject not found: " + string.Join(", ", names), last);
    }
}
