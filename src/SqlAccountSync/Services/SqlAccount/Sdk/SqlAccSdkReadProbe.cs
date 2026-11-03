using SqlAccountSync.Helpers;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccReadProbeRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    string SelectSql,
    bool AuthorizeLanRead = false,
    string? AuthorizedExactTarget = null);

public sealed record SqlAccReadProbeResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    bool LoginOk = false,
    bool IsLoginOk = false,
    bool ReadOk = false,
    bool LogoutOk = false,
    bool ComCleanupOk = false,
    int? RecordCount = null,
    string? DcfPath = null,
    string? DatabaseFileName = null);

/// <summary>
/// Isolated Login → SELECT-only NewDataSet → Logout probe. Does not touch app workers or sync logic.
/// </summary>
public sealed class SqlAccSdkReadProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkReadProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccReadProbeResult> RunAsync(
        SqlAccReadProbeRequest request,
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
            return Task.FromResult(new SqlAccReadProbeResult(
                Success: false,
                SafetyBlocked: true,
                Message: ex.Message,
                DcfPath: request.DcfPath,
                DatabaseFileName: request.DatabaseFileName));
        }

        if (string.IsNullOrWhiteSpace(request.UserName))
            return Task.FromResult(new SqlAccReadProbeResult(false, false, "UserName is required."));
        if (string.IsNullOrWhiteSpace(request.Password))
            return Task.FromResult(new SqlAccReadProbeResult(false, false, "Password is required."));

        var sql = string.IsNullOrWhiteSpace(request.SelectSql)
            ? SqlAccReadPocSafety.MinimalMetadataSql
            : request.SelectSql.Trim();
        try
        {
            SqlGuard.EnsureSelectOnly(sql);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccReadProbeResult(false, false, ex.Message));
        }

        return _sta.RunAsync(() => ExecuteOnSta(request, sql), cancellationToken);
    }

    private SqlAccReadProbeResult ExecuteOnSta(SqlAccReadProbeRequest request, string sql)
    {
        ISqlAccComApp? app = null;
        var loginOk = false;
        var isLoginOk = false;
        var readOk = false;
        var logoutOk = false;
        var cleanupOk = false;
        int? count = null;
        string message;
        var failed = false;

        try
        {
            app = _factory.Create();
            // Official login.html: call Login(...), then require ComServer.IsLogin == true.
            // Login() itself is not a success flag (often returns void / null even on failure).
            app.Login(
                request.UserName.Trim(),
                request.Password,
                request.DcfPath.Trim(),
                request.DatabaseFileName.Trim());
            isLoginOk = app.IsLogin;
            loginOk = isLoginOk;
            var diag = app is SqlAccComApp com ? com.LastComDiagnostics : app.LastComDiagnostics;
            if (!isLoginOk)
            {
                failed = true;
                message =
                    "SDK Login did not establish a session (IsLogin=false after Login). " +
                    "Per SQLSDK login.html, IsLogin is the success check — LoginOk is not inferred from a missing exception. " +
                    "Likely auth/DCF/FDB/connection failure or a blocked SQL Acc UI prompt. " +
                    "diag={" + diag + "}";
            }
            else
            {
                count = app.QueryRecordCount(sql);
                readOk = true;
                app.Logout();
                logoutOk = !app.IsLogin;
                message = logoutOk
                    ? "SDK Login + SELECT metadata + Logout completed. diag={" + diag + "}"
                    : "Logout called but IsLogin remained true. diag={" + diag + "}";
                if (!logoutOk) failed = true;
            }
        }
        catch (Exception ex)
        {
            failed = true;
            message = "SDK Read POC failed: " + ex.GetType().Name + ": " + ex.Message;
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
                catch
                {
                    // best effort
                }

                try
                {
                    app.Dispose();
                    cleanupOk = true;
                }
                catch
                {
                    cleanupOk = false;
                }
            }
        }

        return new SqlAccReadProbeResult(
            Success: !failed && loginOk && isLoginOk && readOk && logoutOk && cleanupOk,
            SafetyBlocked: false,
            Message: message!,
            LoginOk: loginOk,
            IsLoginOk: isLoginOk,
            ReadOk: readOk,
            LogoutOk: logoutOk,
            ComCleanupOk: cleanupOk,
            RecordCount: count,
            DcfPath: request.DcfPath,
            DatabaseFileName: request.DatabaseFileName);
    }
}
