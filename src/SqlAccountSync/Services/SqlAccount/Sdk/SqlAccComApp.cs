using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>Late-bound SQLAcc.BizApp. Must be created and used on an STA thread.</summary>
public sealed partial class SqlAccComApp : ISqlAccComApp
{
    private dynamic? _app;
    private bool _disposed;

    /// <summary>Sanitized COM diagnostics from the last Login/IsLogin (never includes secrets).</summary>
    public string LastComDiagnostics { get; private set; } = "";

    /// <inheritdoc />
    public bool AttachedToRunningInstance { get; }

    public SqlAccComApp(string progId)
        : this(CreateNewInstance(progId), attached: false)
    {
    }

    public SqlAccComApp(object existingInstance, bool attached)
    {
        _app = existingInstance
               ?? throw new ArgumentNullException(nameof(existingInstance));
        AttachedToRunningInstance = attached;
    }

    private static object CreateNewInstance(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
                   ?? throw new InvalidOperationException(
                       $"COM ProgID '{progId}' is not registered. Install SQL Accounting / register SQLAcc.BizApp.");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException($"Failed to create COM instance for '{progId}'.");
    }

    public bool IsLogin
    {
        get
        {
            EnsureAlive();
            var raw = _app!.IsLogin;
            var coerced = CoerceComBool(raw, out string rawDesc);
            LastComDiagnostics = AppendDiag(LastComDiagnostics, "IsLoginRaw=" + rawDesc + ";IsLogin=" + coerced);
            return coerced;
        }
    }

    public void Login(string userName, string password, string dcfPath, string databaseFileName)
    {
        EnsureAlive();
        // CHM / login.html: Login(User Name, Password, DCF Path, File Name) — success is IsLogin, not a return flag.
        var apartment = Thread.CurrentThread.GetApartmentState();
        var instanceId = RuntimeHelpers.GetHashCode(_app!);
        object? loginReturn = null;
        try
        {
            loginReturn = _app!.Login(userName, password, dcfPath, databaseFileName);
        }
        catch (COMException ex)
        {
            LastComDiagnostics =
                $"stage=Login;apartment={apartment};instance={instanceId};" +
                $"dcfLen={dcfPath?.Length ?? 0};fdb={databaseFileName};userLen={userName?.Length ?? 0};" +
                $"comException=0x{ex.HResult:X8};{SanitizeComMessage(ex.Message)}";
            throw;
        }
        catch (Exception ex)
        {
            LastComDiagnostics =
                $"stage=Login;apartment={apartment};instance={instanceId};" +
                $"dcfLen={dcfPath?.Length ?? 0};fdb={databaseFileName};userLen={userName?.Length ?? 0};" +
                $"exception={ex.GetType().Name};{SanitizeComMessage(ex.Message)}";
            throw;
        }

        var rawLoginReturn = DescribeRaw(loginReturn);
        var isLoginRaw = _app!.IsLogin;
        var isLogin = CoerceComBool(isLoginRaw, out string isLoginDesc);
        LastComDiagnostics =
            $"stage=Login;apartment={apartment};instance={instanceId};" +
            $"dcfLen={dcfPath?.Length ?? 0};fdb={databaseFileName};userLen={userName?.Length ?? 0};" +
            $"loginReturn={rawLoginReturn};IsLoginRaw={isLoginDesc};IsLogin={isLogin}";
    }

    public void Logout()
    {
        EnsureAlive();
        // Logout is documented on the official wiki samples (not in local SQLSDK.chm).
        _app!.Logout();
    }

    public int QueryRecordCount(string selectSql)
    {
        EnsureAlive();
        Helpers.SqlGuard.EnsureSelectOnly(selectSql);
        // Wiki: ComServer.DBManager.NewDataSet(lSQL)
        dynamic dataSet = _app!.DBManager.NewDataSet(selectSql);
        try
        {
            return Convert.ToInt32(dataSet.RecordCount);
        }
        finally
        {
            try
            {
                Marshal.FinalReleaseComObject(dataSet);
            }
            catch
            {
                // ignore
            }
        }
    }

    public IReadOnlyList<string> ListBizObjectNames()
    {
        EnsureAlive();
        // CHM application_objects4.html GetBizObjects
        var names = new List<string>();
        dynamic bizObjects = _app!.BizObjects;
        int count = Convert.ToInt32(bizObjects.Count);
        for (var i = 0; i < count; i++)
        {
            var name = Convert.ToString(bizObjects.Items(i))?.Trim() ?? "";
            if (name.Length > 0)
                names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public SqlAccBizObjectMetadata InspectBizObject(string name)
    {
        EnsureAlive();
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return new SqlAccBizObjectMetadata("", false, "Name is required.", [], "");

        dynamic? biz = null;
        try
        {
            // Metadata only — do not call New / Edit / Save / Delete / Post / PostData / Open.
            biz = _app!.BizObjects.Find(name);
            if (biz is null)
                return new SqlAccBizObjectMetadata(name, false, "Find returned null.", [], "");

            var dataSets = new List<SqlAccBizObjectDataSetInfo>();
            dynamic sets = biz.DataSets;
            int setCount = Convert.ToInt32(sets.Count);
            for (var s = 0; s < setCount; s++)
            {
                dynamic ds = sets.Items(s);
                var dsName = Convert.ToString(ds.Name)?.Trim() ?? $"DataSet[{s}]";
                var fields = new List<SqlAccBizObjectFieldInfo>();
                try
                {
                    dynamic fieldColl = ds.Fields;
                    int fieldCount = Convert.ToInt32(fieldColl.Count);
                    for (var f = 0; f < fieldCount; f++)
                    {
                        dynamic field = fieldColl.Items(f);
                        fields.Add(new SqlAccBizObjectFieldInfo(
                            Convert.ToString(field.FieldName) ?? "",
                            Convert.ToString(field.DataType) ?? "",
                            Convert.ToString(field.DataSize) ?? ""));
                    }
                }
                catch (Exception ex)
                {
                    fields.Add(new SqlAccBizObjectFieldInfo(
                        "(fields-unavailable)",
                        ex.GetType().Name,
                        SanitizeMeta(ex.Message)));
                }

                dataSets.Add(new SqlAccBizObjectDataSetInfo(dsName, fields));
            }

            string xmlDefs = "";
            try
            {
                xmlDefs = Convert.ToString(biz.XMLFieldDefs) ?? "";
                if (xmlDefs.Length > 4000)
                    xmlDefs = xmlDefs[..4000] + "…(truncated)";
            }
            catch
            {
                xmlDefs = "";
            }

            return new SqlAccBizObjectMetadata(name, true, null, dataSets, xmlDefs);
        }
        catch (Exception ex)
        {
            return new SqlAccBizObjectMetadata(
                name,
                Found: false,
                FindError: SanitizeMeta(ex.Message),
                DataSets: [],
                XmlFieldDefs: "");
        }
        finally
        {
            if (biz is not null)
            {
                try { Marshal.FinalReleaseComObject(biz); } catch { /* ignore */ }
            }
        }
    }

    private static string SanitizeMeta(string? message)
    {
        var m = (message ?? "").Trim();
        if (m.Length == 0) return "messageEmpty";
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("passwd", StringComparison.OrdinalIgnoreCase))
            return "messageRedacted";
        return m.Length <= 200 ? m : m[..200];
    }

    public bool AgentExists(string code)
    {
        EnsureAlive();
        code = (code ?? "").Trim();
        if (code.Length == 0)
            throw new ArgumentException("Agent code is required.", nameof(code));

        // Wiki Agent sample: FindKeyByRef("CODE", code); null/0/empty => not found.
        dynamic biz = _app!.BizObjects.Find("Agent");
        try
        {
            object? key = biz.FindKeyByRef("CODE", code);
            return SqlAccBizObjectWriter.IsPresentKey(key);
        }
        catch
        {
            // Fallback: CHM-style open by Params (Open fails / empty if missing).
            try
            {
                biz.Params.Find("Code").Value = code;
                biz.Open();
                try
                {
                    dynamic main = biz.DataSets.Find("MainDataSet");
                    var found = Convert.ToString(main.FindField("Code").value) ?? "";
                    return string.Equals(found.Trim(), code, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    try { biz.Close(); } catch { /* ignore */ }
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public void AgentCreate(string code, string description)
    {
        EnsureAlive();
        code = (code ?? "").Trim();
        description = description ?? "";
        if (code.Length == 0)
            throw new ArgumentException("Agent code is required.", nameof(code));

        // CHM add_agent.html
        dynamic biz = _app!.BizObjects.Find("Agent");
        dynamic main = biz.DataSets.Find("MainDataSet");
        biz.New();
        main.FindField("Code").value = code;
        main.FindField("Description").value = description;
        biz.Save();
    }

    public void AgentUpdate(string code, string description)
    {
        EnsureAlive();
        code = (code ?? "").Trim();
        description = description ?? "";
        if (code.Length == 0)
            throw new ArgumentException("Agent code is required.", nameof(code));

        // CHM edit_agent.html
        dynamic biz = _app!.BizObjects.Find("Agent");
        dynamic main = biz.DataSets.Find("MainDataSet");
        biz.Params.Find("Code").Value = code;
        biz.Open();
        biz.Edit();
        main.FindField("Code").value = code;
        main.FindField("Description").value = description;
        biz.Save();
        biz.Close();
    }

    public string AgentReadDescription(string code)
    {
        EnsureAlive();
        code = (code ?? "").Trim();
        if (code.Length == 0)
            throw new ArgumentException("Agent code is required.", nameof(code));

        dynamic biz = _app!.BizObjects.Find("Agent");
        dynamic main = biz.DataSets.Find("MainDataSet");
        biz.Params.Find("Code").Value = code;
        biz.Open();
        try
        {
            return Convert.ToString(main.FindField("Description").value) ?? "";
        }
        finally
        {
            try { biz.Close(); } catch { /* ignore */ }
        }
    }

    public void AgentDelete(string code)
    {
        EnsureAlive();
        code = (code ?? "").Trim();
        if (code.Length == 0)
            throw new ArgumentException("Agent code is required.", nameof(code));

        // CHM delete_agent.html
        dynamic biz = _app!.BizObjects.Find("Agent");
        biz.Params.Find("Code").Value = code;
        biz.Open();
        biz.Delete();
        biz.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_app is not null)
        {
            if (AttachedToRunningInstance)
            {
                // Leave the user's SQL Account window logged in; only drop our RCW reference.
                try { Marshal.ReleaseComObject(_app); }
                catch { /* ignore */ }
            }
            else
            {
                try
                {
                    if ((bool)_app.IsLogin)
                        _app.Logout();
                }
                catch
                {
                    // Best-effort cleanup only.
                }

                try { Marshal.FinalReleaseComObject(_app); }
                catch { /* ignore */ }
            }

            _app = null;
        }

        GC.SuppressFinalize(this);
    }

    private void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_app is null)
            throw new ObjectDisposedException(nameof(SqlAccComApp));
    }

    /// <summary>
    /// COM VT_BOOL and late-bound IsLogin may surface as bool, short (-1/0), int, or string.
    /// Official samples treat a true-ish value as logged in.
    /// </summary>
    internal static bool CoerceComBool(object? raw, out string description)
    {
        description = DescribeRaw(raw);
        switch (raw)
        {
            case null:
                return false;
            case bool b:
                return b;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                try { return Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture) != 0; }
                catch { return false; }
            case string s:
                if (bool.TryParse(s, out var parsed)) return parsed;
                if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var n))
                    return n != 0;
                return false;
            default:
                try { return Convert.ToBoolean(raw, System.Globalization.CultureInfo.InvariantCulture); }
                catch { return false; }
        }
    }

    private static string DescribeRaw(object? raw)
    {
        if (raw is null) return "null";
        var type = raw.GetType().FullName ?? raw.GetType().Name;
        try
        {
            if (raw is bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return type + ":" + Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (raw is string s)
                return type + ":len=" + s.Length;
            return type;
        }
        catch
        {
            return type;
        }
    }

    private static string AppendDiag(string existing, string next) =>
        string.IsNullOrEmpty(existing) ? next : existing + "|" + next;

    private static string SanitizeComMessage(string? message)
    {
        var m = (message ?? "").Trim();
        if (m.Length == 0) return "messageEmpty";
        // Never echo credential-like fragments; keep a short sanitized prefix.
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("passwd", StringComparison.OrdinalIgnoreCase))
            return "messageRedacted";
        return m.Length <= 160 ? m : m[..160];
    }
}

public sealed class SqlAccComAppFactory : ISqlAccComAppFactory
{
    private readonly SqlAccSdkOptions _options;
    private readonly ILogger<SqlAccComAppFactory>? _logger;

    public SqlAccComAppFactory(IOptions<SqlAccSdkOptions> options, ILogger<SqlAccComAppFactory>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ISqlAccComApp Create()
    {
        if (!_options.AllowLiveCom)
            throw new InvalidOperationException(
                "SqlAccSdk:AllowLiveCom is false. Refusing to create SQLAcc.BizApp (no live COM in this mode).");

        var progId = string.IsNullOrWhiteSpace(_options.ProgId) ? "SQLAcc.BizApp" : _options.ProgId.Trim();
        var runningCount = ComRunningObject.CountSqlAccProcesses();

        // Always prefer an already-registered automation object when the UI exposes one.
        if (ComRunningObject.TryAttach(progId, out var attached) && attached is not null)
        {
            _logger?.LogInformation(
                "SDK attached to running {ProgId} (SQLACC processes={Count}).", progId, runningCount);
            return new SqlAccComApp(attached, attached: true);
        }

        if (_options.PreferRunningInstance && runningCount > 0)
        {
            // Interactive SQL Account usually does NOT RegisterActiveObject, so attach fails.
            // CreateInstance is required for writes; it may open a second SQLACC tab (SQL Acc limitation).
            _logger?.LogWarning(
                "SQLACC.exe is running ({Count}) but COM attach failed (not in ROT). " +
                "Creating automation instance so auto-push can continue — a second SQL Account window may appear.",
                runningCount);
        }
        else
        {
            _logger?.LogInformation("SDK creating new {ProgId} instance (SQLACC processes={Count}).", progId, runningCount);
        }

        return new SqlAccComApp(progId);
    }
}
