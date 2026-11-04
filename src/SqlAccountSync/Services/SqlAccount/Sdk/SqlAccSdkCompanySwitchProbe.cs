namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccCompanySwitchRequest(
    string DcfPath,
    string FirstDatabaseFileName,
    string SecondDatabaseFileName,
    string UserName,
    string Password,
    bool AuthorizeLanRead,
    string AuthorizedExactTarget,
    /// <summary>Optional third hop (e.g. back to Miracle) for A→B→A isolation checks.</summary>
    string? ThirdDatabaseFileName = null);

public sealed record SqlAccCompanySwitchResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    bool FirstLoginOk = false,
    bool FirstReadOk = false,
    bool FirstLogoutOk = false,
    int? FirstRecordCount = null,
    string? FirstFdb = null,
    bool SecondLoginOk = false,
    bool SecondReadOk = false,
    bool SecondLogoutOk = false,
    int? SecondRecordCount = null,
    string? SecondFdb = null,
    bool ThirdLoginOk = false,
    bool ThirdReadOk = false,
    bool ThirdLogoutOk = false,
    int? ThirdRecordCount = null,
    string? ThirdFdb = null,
    bool ComCleanupOk = false);

/// <summary>
/// Explicit DCF/FDB Login → SELECT → Logout per company (no last-used defaults).
/// Supports A→B or A→B→A when <see cref="SqlAccCompanySwitchRequest.ThirdDatabaseFileName"/> is set.
/// </summary>
public sealed class SqlAccSdkCompanySwitchProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkCompanySwitchProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccCompanySwitchResult> RunAsync(
        SqlAccCompanySwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath, request.FirstDatabaseFileName, request.AuthorizeLanRead, request.AuthorizedExactTarget);
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath, request.SecondDatabaseFileName, request.AuthorizeLanRead, request.AuthorizedExactTarget);
            if (!string.IsNullOrWhiteSpace(request.ThirdDatabaseFileName))
            {
                SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                    request.DcfPath, request.ThirdDatabaseFileName, request.AuthorizeLanRead, request.AuthorizedExactTarget);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccCompanySwitchResult(false, true, ex.Message));
        }

        if (string.Equals(request.FirstDatabaseFileName.Trim(), request.SecondDatabaseFileName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new SqlAccCompanySwitchResult(
                false, true, "Company switch requires two different FDB file names."));

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            return Task.FromResult(new SqlAccCompanySwitchResult(false, false, "UserName and Password are required."));

        return _sta.RunAsync(() => ExecuteOnSta(request), cancellationToken);
    }

    private SqlAccCompanySwitchResult ExecuteOnSta(SqlAccCompanySwitchRequest request)
    {
        ISqlAccComApp? app = null;
        var firstLogin = false;
        var firstRead = false;
        var firstLogout = false;
        int? firstCount = null;
        var secondLogin = false;
        var secondRead = false;
        var secondLogout = false;
        int? secondCount = null;
        var thirdLogin = false;
        var thirdRead = false;
        var thirdLogout = false;
        int? thirdCount = null;
        var wantThird = !string.IsNullOrWhiteSpace(request.ThirdDatabaseFileName);
        var thirdFdb = wantThird ? request.ThirdDatabaseFileName!.Trim() : null;
        var cleanup = false;
        var message = "";
        var failed = false;

        try
        {
            app = _factory.Create();

            if (!TryHop(app, request, request.FirstDatabaseFileName.Trim(), "First",
                    out firstLogin, out firstRead, out firstLogout, out firstCount, out message))
                failed = true;
            else if (!TryHop(app, request, request.SecondDatabaseFileName.Trim(), "Second",
                         out secondLogin, out secondRead, out secondLogout, out secondCount, out message))
                failed = true;
            else if (wantThird)
            {
                if (!TryHop(app, request, thirdFdb!, "Third",
                        out thirdLogin, out thirdRead, out thirdLogout, out thirdCount, out message))
                    failed = true;
                else
                    message =
                        $"Company switch OK: {request.FirstDatabaseFileName} → {request.SecondDatabaseFileName} → {thirdFdb} (explicit DCF/FDB each hop).";
            }
            else
            {
                message =
                    $"Company switch OK: {request.FirstDatabaseFileName} → Logout → {request.SecondDatabaseFileName} (explicit DCF/FDB).";
            }
        }
        catch (Exception ex)
        {
            failed = true;
            message = "SDK company switch failed: " + ex.GetType().Name + ": " + ex.Message;
            try
            {
                if (app?.IsLogin == true)
                    app.Logout();
            }
            catch { /* ignore */ }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    if (app.IsLogin) app.Logout();
                }
                catch { /* ignore */ }

                try
                {
                    app.Dispose();
                    cleanup = true;
                }
                catch { cleanup = false; }
            }
        }

        var hopsOk = firstLogin && firstRead && firstLogout && secondLogin && secondRead && secondLogout
                     && (!wantThird || (thirdLogin && thirdRead && thirdLogout));

        return new SqlAccCompanySwitchResult(
            Success: !failed && hopsOk && cleanup,
            SafetyBlocked: false,
            Message: message,
            FirstLoginOk: firstLogin,
            FirstReadOk: firstRead,
            FirstLogoutOk: firstLogout,
            FirstRecordCount: firstCount,
            FirstFdb: request.FirstDatabaseFileName.Trim(),
            SecondLoginOk: secondLogin,
            SecondReadOk: secondRead,
            SecondLogoutOk: secondLogout,
            SecondRecordCount: secondCount,
            SecondFdb: request.SecondDatabaseFileName.Trim(),
            ThirdLoginOk: thirdLogin,
            ThirdReadOk: thirdRead,
            ThirdLogoutOk: thirdLogout,
            ThirdRecordCount: thirdCount,
            ThirdFdb: thirdFdb,
            ComCleanupOk: cleanup);
    }

    private static bool TryHop(
        ISqlAccComApp app,
        SqlAccCompanySwitchRequest request,
        string fdb,
        string label,
        out bool loginOk,
        out bool readOk,
        out bool logoutOk,
        out int? count,
        out string message)
    {
        loginOk = false;
        readOk = false;
        logoutOk = false;
        count = null;
        message = "";

        app.Login(request.UserName.Trim(), request.Password, request.DcfPath.Trim(), fdb);
        loginOk = app.IsLogin;
        if (!loginOk)
        {
            message = $"{label} Login IsLogin=false for {fdb}.";
            return false;
        }

        count = app.QueryRecordCount(SqlAccReadPocSafety.MinimalMetadataSql);
        readOk = true;
        app.Logout();
        logoutOk = !app.IsLogin;
        if (!logoutOk)
        {
            message = $"{label} Logout left IsLogin=true ({fdb}).";
            return false;
        }

        return true;
    }
}
