using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccSessionStatus(
    bool LoggedIn,
    string? ActiveCompany,
    bool AttachedToUserWindow);

public interface ISqlAccSessionManager
{
    /// <summary>
    /// Ensures COM is logged into the company identified by <paramref name="db"/> (DCF + FDB file name).
    /// Serializes multi-company access: one Login at a time.
    /// </summary>
    Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> on the STA thread while logged into <paramref name="db"/>.
    /// Company target comes from AWS sdkConnections (preferred) or DefaultDcfPath + FbPath file name.
    /// </summary>
    Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default);

    /// <summary>Current SDK session state for health (real write-path session, not legacy).</summary>
    SqlAccSessionStatus GetStatus();
}

/// <summary>
/// Multi-company SDK session for auto-push: owns <b>one</b> SQLACC.exe and switches companies with
/// Logout → Login → write on that same COM instance (no second tab per company).
/// After each write: Logout to free the concurrent seat. If attached to a user window, keep
/// the window open at the login screen; otherwise close SQLACC.exe that sync opened.
/// </summary>
public sealed class SqlAccSessionManager : ISqlAccSessionManager, IAsyncDisposable, IDisposable
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly ISecretResolver _secrets;
    private readonly ISdkConnectionBundle _bundle;
    private readonly SqlAccSdkOptions _options;
    private readonly ILogger<SqlAccSessionManager> _logger;
    private readonly StaWorkQueue _sta = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISqlAccComApp? _app;
    private string? _activeCompanyKey;
    /// <summary>True when this cycle attached to a user-owned SQLACC window (do not Kill).</summary>
    private bool _keepSqlAccOpenAfterUse;
    private bool _disposed;

    public SqlAccSessionManager(
        ISqlAccComAppFactory factory,
        ISecretResolver secrets,
        ISdkConnectionBundle bundle,
        IOptions<SqlAccSdkOptions> options,
        ILogger<SqlAccSessionManager> logger)
    {
        _factory = factory;
        _secrets = secrets;
        _bundle = bundle;
        _options = options.Value;
        _logger = logger;
    }

    public SqlAccSessionStatus GetStatus()
    {
        var app = _app;
        if (app is null)
            return new SqlAccSessionStatus(false, null, false);

        var attached = false;
        var loggedIn = false;
        try
        {
            attached = app.AttachedToRunningInstance;
            loggedIn = app.IsLogin;
        }
        catch
        {
            return new SqlAccSessionStatus(false, null, attached);
        }

        return new SqlAccSessionStatus(loggedIn, loggedIn ? _activeCompanyKey : null, attached);
    }

    public async Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.AllowLiveCom)
            throw new InvalidOperationException("SqlAccSdk:AllowLiveCom is false; SDK session is disabled.");

        var login = await ResolveLoginAsync(db, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoggedInAsync(login, cancellationToken).ConfigureAwait(false);
            return new SessionLease(_gate, ReleaseSeatAfterUseSync);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<T> RunAsync<T>(
        RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var login = await ResolveLoginAsync(db, cancellationToken).ConfigureAwait(false);
        var expectedKey = login.Target.Key;
        await using var lease = new AsyncSessionLease(
            await UseCompanyWithLoginAsync(login, cancellationToken).ConfigureAwait(false));
        return await _sta.RunAsync(() =>
        {
            if (_app is null)
                throw new InvalidOperationException("SDK COM app is not initialized after Login.");
            if (!string.Equals(_activeCompanyKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SDK session company mismatch (fail closed): active={_activeCompanyKey ?? "(none)"}, requested={expectedKey}.");
            }

            if (!_app.IsLogin)
                throw new InvalidOperationException(
                    $"SDK session is not logged in for requested company {expectedKey}.");
            return action(_app);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDisposable> UseCompanyWithLoginAsync(CompanyLogin login, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.AllowLiveCom)
            throw new InvalidOperationException("SqlAccSdk:AllowLiveCom is false; SDK session is disabled.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoggedInAsync(login, cancellationToken).ConfigureAwait(false);
            return new SessionLease(_gate, ReleaseSeatAfterUseSync);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task EnsureLoggedInAsync(CompanyLogin login, CancellationToken cancellationToken)
    {
        await _sta.RunAsync(() =>
        {
            var key = login.Target.Key;
            if (IsUsableSessionFor(key))
                return;

            // Always one window: Logout → Login on the same BizApp (company A → B → C).
            EnsureSingleInstanceLogin(login, key);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One SQL Account tab: create/attach once, then Logout + Login when the destination company changes.
    /// Keep-window only when COM attached to a running instance (not process-count alone).
    /// </summary>
    private void EnsureSingleInstanceLogin(CompanyLogin login, string key)
    {
        EnforceAtMostOneSqlAccProcess();

        if (_app is null || !IsComAlive(_app))
        {
            ReleaseComApp_NoThrow(reason: "dead-or-missing", fromKey: _activeCompanyKey, toKey: key);
            _app = _factory.Create();
        }

        if (_app.AttachedToRunningInstance)
        {
            _keepSqlAccOpenAfterUse = true;
            _logger.LogInformation(
                "SDK attached to running SQL Account; will keep window after sync (Logout only)");
        }

        try
        {
            if (IsUsableSessionFor(key))
                return;

            if (_app.IsLogin)
            {
                _logger.LogInformation(
                    "SDK Logout before switch {From} → {To}",
                    _activeCompanyKey ?? "(none)", key);
                try { _app.Logout(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SDK Logout before company switch failed; continuing with Login");
                }

                _activeCompanyKey = null;
            }

            PerformLogin(login, key, "single SQL Account window");
        }
        catch
        {
            // Drop dead owned handle so the next attempt can Create a clean single instance.
            if (_app is null || !_app.AttachedToRunningInstance)
                ReleaseComApp_NoThrow(reason: "login-failed", fromKey: key, toKey: null);
            else
                _activeCompanyKey = null;
            throw;
        }
    }

    /// <summary>If more than one SQLACC.exe is alive, kill all and drop our COM handle so Login uses one seat.</summary>
    private void EnforceAtMostOneSqlAccProcess()
    {
        var count = ComRunningObject.CountSqlAccProcesses();
        if (count <= 1)
            return;

        _logger.LogWarning(
            "Found {Count} SQLACC.exe processes; closing all so only one concurrent user seat is used",
            count);
        ReleaseComApp_NoThrow(reason: "multi-sqlacc", fromKey: _activeCompanyKey, toKey: null);
        ComRunningObject.KillAllSqlAccProcesses();
    }

    /// <summary>
    /// Called when a session lease ends (end of RunAsync / UseCompany).
    /// Logout to free the concurrent seat; keep user window or close sync-owned SQLACC.
    /// </summary>
    private void ReleaseSeatAfterUseSync()
    {
        try
        {
            ReleaseSeatAfterUseAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK failed to release session after use");
        }
    }

    private async Task ReleaseSeatAfterUseAsync()
    {
        try
        {
            var attached = _keepSqlAccOpenAfterUse
                || (_app is not null && _app.AttachedToRunningInstance);
            var decision = SeatReleaseRules.Decide(
                attached, _options.LogoutAfterUse, _options.ReleaseSqlAccAfterUse);

            if (decision == SeatReleaseRules.Action.None)
                return;

            await _sta.RunAsync(() => ApplyReleaseOnSta(decision)).ConfigureAwait(false);
        }
        finally
        {
            // Each write re-decides; one earlier attach must not stick forever.
            _keepSqlAccOpenAfterUse = false;
        }
    }

    private void ApplyReleaseOnSta(SeatReleaseRules.Action decision)
    {
        if (decision == SeatReleaseRules.Action.LogoutOnly)
        {
            LogoutKeepWindowOnSta();
            _logger.LogInformation(
                "SDK seat freed (logout, kept user window)");
            return;
        }

        // LogoutAndClose
        if (_app is null)
        {
            _activeCompanyKey = null;
            if (_options.ReleaseSqlAccAfterUse)
                ComRunningObject.KillAllSqlAccProcesses();
            _logger.LogInformation("SDK seat freed (logout + closed SQLACC)");
            return;
        }

        if (_options.LogoutAfterUse)
        {
            try
            {
                if (_app.IsLogin)
                    _app.Logout();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SDK Logout after use failed");
            }

            _activeCompanyKey = null;
        }

        if (_options.ReleaseSqlAccAfterUse)
        {
            ReleaseComApp_NoThrow(reason: "after-use-release", fromKey: null, toKey: null);
            var killed = ComRunningObject.KillAllSqlAccProcesses();
            if (killed > 0)
            {
                _logger.LogInformation(
                    "SDK seat freed (logout + closed SQLACC); closed {Count} SQLACC.exe",
                    killed);
            }
            else
            {
                _logger.LogInformation("SDK seat freed (logout + closed SQLACC)");
            }
        }
        else
        {
            _logger.LogInformation("SDK seat freed (logout, kept user window)");
        }
    }

    private void LogoutKeepWindowOnSta()
    {
        if (_app is null)
        {
            _activeCompanyKey = null;
            return;
        }

        try
        {
            if (_app.IsLogin)
                _app.Logout();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK Logout (keep window) failed");
        }

        _activeCompanyKey = null;
    }

    private void PerformLogin(CompanyLogin login, string key, string modeLabel)
    {
        if (_app is null)
            throw new InvalidOperationException("SDK COM app is not initialized before Login.");

        _logger.LogInformation(
            "SDK Login company file {DatabaseFile} ({Mode})", login.Target.DatabaseFileName, modeLabel);
        _app.Login(login.Username, login.Password, login.Target.DcfPath, login.Target.DatabaseFileName);
        if (!_app.IsLogin)
        {
            var diag = _app.LastComDiagnostics;
            throw new InvalidOperationException(
                "SQLAcc.BizApp Login did not establish a session (IsLogin=false). diag={" + diag + "}");
        }

        _activeCompanyKey = key;
    }

    private static bool IsComAlive(ISqlAccComApp app)
    {
        try
        {
            _ = app.IsLogin;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CompanyLogin> ResolveLoginAsync(RegisteredDatabase db, CancellationToken cancellationToken)
    {
        var code = (db.DbCode ?? "").Trim();
        if (code.Length > 0)
        {
            var fromBundle = await _bundle.ResolveAsync(code, cancellationToken).ConfigureAwait(false);
            if (fromBundle is not null)
                return FromBundle(db, fromBundle);
        }

        return await FromOptionsAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private static CompanyLogin FromBundle(RegisteredDatabase db, SdkCompanyCredentials creds)
    {
        var dcf = (creds.DcfPath ?? "").Trim();
        if (dcf.Length == 0)
            throw new InvalidOperationException(
                $"SDK connection for '{db.DbCode}' has empty dcfPath in Secrets Manager.");

        var file = FileName(creds.Database);
        if (file.Length == 0)
            file = FileName(db.FbPath);
        if (file.Length == 0)
            throw new InvalidOperationException(
                $"SDK connection for '{db.DbCode}' has no database file name (companies[].database or FbPath).");

        var user = string.IsNullOrWhiteSpace(creds.Username) ? "ADMIN" : creds.Username.Trim();
        var password = creds.Password ?? "";
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                $"SDK connection for '{db.DbCode}' has empty password in Secrets Manager.");

        return new CompanyLogin(new CompanyTarget(dcf, file), user, password.Trim());
    }

    private async Task<CompanyLogin> FromOptionsAsync(RegisteredDatabase db, CancellationToken cancellationToken)
    {
        var target = ResolveCompanyTarget(db, _options.DefaultDcfPath);
        var user = (_options.UserName ?? "").Trim();
        if (user.Length == 0)
            throw new InvalidOperationException(
                "No SDK connection for '" + (db.DbCode ?? "") + "' in Secrets Manager, and SqlAccSdk:UserName is empty.");

        var password = await _secrets.ResolveAsync(_options.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "No SDK connection for '" + (db.DbCode ?? "") +
                "' in Secrets Manager, and SqlAccSdk:PasswordSecretRef did not resolve a password.");

        return new CompanyLogin(target, user, password.Trim());
    }

    private bool IsUsableSessionFor(string companyKey)
    {
        if (_app is null || string.IsNullOrEmpty(_activeCompanyKey))
            return false;
        if (!string.Equals(_activeCompanyKey, companyKey, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return _app.IsLogin;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK IsLogin check failed for {Company}; treating session as unusable", companyKey);
            return false;
        }
    }

    private void ReleaseComApp_NoThrow(string reason, string? fromKey, string? toKey)
    {
        if (_app is null)
        {
            _activeCompanyKey = null;
            return;
        }

        _logger.LogInformation(
            "SDK releasing COM session ({Reason}) from {From} to {To}",
            reason, fromKey ?? "(none)", toKey ?? "(none)");
        try
        {
            _app.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDK COM dispose failed during {Reason}", reason);
        }

        _app = null;
        _activeCompanyKey = null;
    }

    internal static CompanyTarget ResolveCompanyTarget(RegisteredDatabase db, string? defaultDcfPath = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var dcf = (defaultDcfPath ?? "").Trim();
        if (dcf.Length == 0)
            throw new InvalidOperationException(
                "SqlAccSdk:DefaultDcfPath is required for SDK Login when company credentials are not in Secrets Manager.");

        var file = FileName(db.FbPath);
        if (file.Length == 0)
            throw new InvalidOperationException(
                $"Registered database '{db.DbCode}' has no FbPath file name for SDK Login (4th parameter).");

        return new CompanyTarget(dcf, file);
    }

    private static string FileName(string? path)
    {
        var raw = (path ?? "").Trim().Replace('\\', '/');
        if (raw.Length == 0)
            return "";
        return Path.GetFileName(raw) ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _sta.RunAsync(() =>
            {
                if (_app is not null)
                {
                    try
                    {
                        if (_app.IsLogin)
                        {
                            _app.Logout();
                            _logger.LogInformation(
                                "SDK Logout on shutdown — freed SQL Account concurrent user seat");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SDK Logout on shutdown failed");
                    }

                    try { _app.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "SDK COM dispose on shutdown failed");
                    }
                }

                _app = null;
                _activeCompanyKey = null;
                _keepSqlAccOpenAfterUse = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDK session dispose encountered an error.");
        }

        _sta.Dispose();
        _gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal readonly record struct CompanyTarget(string DcfPath, string DatabaseFileName)
    {
        public string Key => DcfPath + "|" + DatabaseFileName;
    }

    private readonly record struct CompanyLogin(CompanyTarget Target, string Username, string Password);

    private sealed class SessionLease(SemaphoreSlim gate, Action? onRelease = null) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            try { onRelease?.Invoke(); }
            finally { gate.Release(); }
        }
    }

    private sealed class AsyncSessionLease(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
