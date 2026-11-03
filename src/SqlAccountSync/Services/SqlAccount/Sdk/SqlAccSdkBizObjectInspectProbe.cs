namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccBizObjectInspectRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    bool AuthorizeLanRead = false,
    string? AuthorizedExactTarget = null,
    /// <summary>When true, inspect only migration-blocker targets + discovered candidates (compact).</summary>
    bool TargetsOnly = false);

/// <summary>
/// Login → list/inspect BizObject metadata (CHM application_objects4) → Logout.
/// Never calls New/Edit/Save/Delete/Post/PostData/Open on business objects.
/// </summary>
public sealed class SqlAccSdkBizObjectInspectProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkBizObjectInspectProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccBizObjectInspectReport> RunAsync(
        SqlAccBizObjectInspectRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath,
                request.DatabaseFileName,
                request.AuthorizeLanRead,
                request.AuthorizedExactTarget);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccBizObjectInspectReport(
                Success: false,
                SafetyBlocked: true,
                Message: ex.Message,
                DcfPath: request.DcfPath,
                DatabaseFileName: request.DatabaseFileName));
        }

        if (string.IsNullOrWhiteSpace(request.UserName))
            return Task.FromResult(new SqlAccBizObjectInspectReport(false, false, "UserName is required."));
        if (string.IsNullOrWhiteSpace(request.Password))
            return Task.FromResult(new SqlAccBizObjectInspectReport(false, false, "Password is required."));

        return _sta.RunAsync(() => ExecuteOnSta(request), cancellationToken);
    }

    private SqlAccBizObjectInspectReport ExecuteOnSta(SqlAccBizObjectInspectRequest request)
    {
        ISqlAccComApp? app = null;
        var loginOk = false;
        var isLoginOk = false;
        var logoutOk = false;
        var cleanupOk = false;
        try
        {
            app = _factory.Create();
            app.Login(
                request.UserName.Trim(),
                request.Password,
                request.DcfPath.Trim(),
                request.DatabaseFileName.Trim());
            loginOk = true;
            isLoginOk = app.IsLogin;
            if (!isLoginOk)
            {
                var diag = app.LastComDiagnostics;
                return new SqlAccBizObjectInspectReport(
                    false, false,
                    "Login completed but IsLogin=false. diag={" + diag + "}",
                    LoginOk: true, IsLoginOk: false,
                    DcfPath: request.DcfPath, DatabaseFileName: request.DatabaseFileName);
            }

            var all = app.ListBizObjectNames();
            var related = all.Where(SqlAccBizObjectInspectTargets.IsRelatedName)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var toInspect = request.TargetsOnly
                ? SqlAccBizObjectInspectTargets.ResolveTargetsOnlyList(all)
                : SqlAccBizObjectInspectTargets.ResolveInspectList(all);
            var inspected = new List<SqlAccBizObjectMetadata>();
            foreach (var name in toInspect)
                inspected.Add(app.InspectBizObject(name));

            try
            {
                app.Logout();
                logoutOk = true;
            }
            catch
            {
                logoutOk = false;
            }

            try
            {
                app.Dispose();
                cleanupOk = true;
                app = null;
            }
            catch
            {
                cleanupOk = false;
            }

            return new SqlAccBizObjectInspectReport(
                Success: loginOk && isLoginOk && logoutOk,
                SafetyBlocked: false,
                Message: request.TargetsOnly
                    ? $"Targets-only: inspected {inspected.Count} name(s); catalog has {all.Count} object(s) (not listed)."
                    : $"Inspected {inspected.Count} BizObject name(s); catalog has {all.Count} object(s).",
                LoginOk: loginOk,
                IsLoginOk: isLoginOk,
                LogoutOk: logoutOk,
                ComCleanupOk: cleanupOk,
                DcfPath: request.DcfPath,
                DatabaseFileName: request.DatabaseFileName,
                AllBizObjectNames: all,
                RelatedNames: related,
                Inspected: inspected,
                TargetsOnly: request.TargetsOnly);
        }
        catch (Exception ex)
        {
            if (app is not null)
            {
                try { app.Dispose(); cleanupOk = true; app = null; } catch { cleanupOk = false; }
            }

            return new SqlAccBizObjectInspectReport(
                false, false,
                Sanitize(ex.Message),
                LoginOk: loginOk,
                IsLoginOk: isLoginOk,
                LogoutOk: logoutOk,
                ComCleanupOk: cleanupOk,
                DcfPath: request.DcfPath,
                DatabaseFileName: request.DatabaseFileName);
        }
        finally
        {
            if (app is not null)
            {
                try { app.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private static string Sanitize(string? message)
    {
        var m = (message ?? "").Trim();
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase))
            return "messageRedacted";
        return m.Length <= 240 ? m : m[..240];
    }
}
