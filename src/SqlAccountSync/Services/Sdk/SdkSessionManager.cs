using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Sdk;

public interface ISdkSessionManager
{
    Task<T> UseCompanyAsync<T>(
        string dbCode,
        Func<dynamic, T> work,
        CancellationToken cancellationToken = default);

    Task UseCompanyAsync(
        string dbCode,
        Action<dynamic> work,
        CancellationToken cancellationToken = default);

    SdkHealthStatus GetHealth();

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes all SDK work behind one STA worker and one async lock.
/// Switches companies with Logout + Login when the requested DbCode differs.
/// </summary>
public sealed class SdkSessionManager : ISdkSessionManager, IDisposable
{
    private readonly SdkWorker _worker = new();
    private readonly ISdkConnectionBundle _bundle;
    private readonly SyncOptions _sync;
    private readonly ILogger<SdkSessionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private dynamic? _bizApp;
    private bool _sessionOwnedByUs;
    private string _currentCompany = "";
    private string _lastError = "";
    private bool _disposed;

    public SdkSessionManager(
        ISdkConnectionBundle bundle,
        IOptions<SyncOptions> sync,
        ILogger<SdkSessionManager> logger)
    {
        _bundle = bundle;
        _sync = sync.Value;
        _logger = logger;
    }

    public async Task UseCompanyAsync(
        string dbCode,
        Action<dynamic> work,
        CancellationToken cancellationToken = default)
    {
        await UseCompanyAsync<object>(dbCode, biz =>
        {
            work(biz);
            return null!;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> UseCompanyAsync<T>(
        string dbCode,
        Func<dynamic, T> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var code = (dbCode ?? "").Trim();
        if (code.Length == 0)
            throw new InvalidOperationException("Database code is required for SDK login.");

        var creds = await _bundle.ResolveAsync(code, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No SDK connection settings for '{code}' in Secrets Manager. " +
                "Add a companies[] row (name/dcfPath/database/username/password).");

        ValidateCreds(creds);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeoutSec = Math.Clamp(_sync.SdkCallTimeoutSeconds, 5, 600);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                return await _worker.InvokeAsync(() =>
                {
                    EnsureLoggedIn(creds, code);
                    return work(_bizApp!);
                }, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _lastError = $"SDK call timed out after {timeoutSec}s for {code}.";
                _logger.LogError("{Error}", _lastError);
                await RecreateBizAppAsync().ConfigureAwait(false);
                throw new TimeoutException(_lastError);
            }
            catch (Exception ex)
            {
                _lastError = ComHelper.FormatException(ex);
                _logger.LogWarning(ex, "SDK work failed for {Code}", code);
                // Access violations / RPC failures poison the COM instance — force a fresh BizApp.
                if (IsFatalComFailure(ex))
                {
                    try { await RecreateBizAppAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public SdkHealthStatus GetHealth()
    {
        var reg = ComHelper.ProbeRegistration();
        var loggedIn = false;
        try
        {
            if (_bizApp is not null)
            {
                loggedIn = _worker.Invoke(() =>
                {
                    try { return (bool)_bizApp!.IsLogin; }
                    catch { return false; }
                });
            }
        }
        catch
        {
            loggedIn = false;
        }

        var guidance = reg.Guidance;
        if (!reg.ProgIdFound)
            guidance = reg.Guidance;
        else if (_bizApp is null)
            guidance = "COM registered. Waiting for first SDK login.";
        else if (!loggedIn)
            guidance = "SDK offline / login failed. Check Secrets Manager credentials and SQL Account.";
        else if (!_sessionOwnedByUs)
            guidance = "Attached to an existing SQL Account session.";
        else
            guidance = "SDK logged in to " + _currentCompany;

        return new SdkHealthStatus
        {
            ProgIdRegistered = reg.ProgIdFound,
            LoggedIn = loggedIn,
            CurrentCompany = _currentCompany,
            SessionOwnedByUs = _sessionOwnedByUs,
            LastError = _lastError,
            Guidance = guidance,
            LocalServer32 = reg.LocalServer32
        };
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _worker.InvokeAsync(ReleaseSession, cancellationToken).ConfigureAwait(false);
            _lastError = "";
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoggedIn(SdkCompanyCredentials creds, string dbCode)
    {
        EnsureBizAppCreated();

        bool alreadyLogin;
        try { alreadyLogin = (bool)_bizApp!.IsLogin; }
        catch { alreadyLogin = false; }

        var sameCompany = alreadyLogin
            && _sessionOwnedByUs
            && _currentCompany.Equals(dbCode, StringComparison.OrdinalIgnoreCase);

        if (sameCompany)
            return;

        // Company switch / foreign session: never Logout+Login on the same instance —
        // that path crashes SQLAcc.AccountUI.bpl (AV) when SQL Account UI is also open.
        // Always tear down and create a fresh owned BizApp, then Login once.
        if (alreadyLogin || _bizApp is not null)
        {
            try { ReleaseSession(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Release before company switch failed");
                ComHelper.SafeRelease(_bizApp);
                _bizApp = null;
                _sessionOwnedByUs = false;
                _currentCompany = "";
            }
        }

        EnsureBizAppCreated();
        var sw = Stopwatch.StartNew();
        try
        {
            _bizApp!.Login(creds.Username, creds.Password, creds.DcfPath, creds.Database);
        }
        catch (Exception ex)
        {
            ComHelper.SafeRelease(_bizApp);
            _bizApp = null;
            _sessionOwnedByUs = false;
            _currentCompany = "";
            throw new InvalidOperationException(
                "SDK Login failed for " + dbCode + " (" + creds.Database + "). " +
                "Close SQL Account desktop windows for this company, then retry. " +
                ComHelper.FormatException(ex), ex);
        }

        bool ok;
        try { ok = (bool)_bizApp.IsLogin; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Login returned but IsLogin check failed: " + ComHelper.FormatException(ex), ex);
        }
        if (!ok)
            throw new InvalidOperationException(
                "Login completed but IsLogin is false. Check username, password, DCF and company database.");

        _sessionOwnedByUs = true;
        _currentCompany = dbCode;
        _lastError = "";
        _logger.LogInformation(
            "SDK logged into {Code} ({Database}) in {Ms}ms",
            dbCode, creds.Database, sw.ElapsedMilliseconds);
    }

    private void EnsureBizAppCreated()
    {
        if (_bizApp is not null) return;

        var type = Type.GetTypeFromProgID(ComHelper.ProgId)
            ?? throw new InvalidOperationException(
                "SQL Account SDK COM component is not registered (ProgID " + ComHelper.ProgId + ").");

        try
        {
            _bizApp = Activator.CreateInstance(type);
        }
        catch (COMException cex)
        {
            throw new InvalidOperationException(
                "Failed to create " + ComHelper.ProgId + ". " +
                ComHelper.DecodeHResult(cex.ErrorCode) + " " + cex.Message, cex);
        }

        if (_bizApp is null)
            throw new InvalidOperationException("Activator.CreateInstance returned null for " + ComHelper.ProgId);

        _logger.LogInformation("SQLAcc.BizApp instance created");
    }

    private async Task RecreateBizAppAsync()
    {
        try
        {
            await _worker.InvokeAsync(() =>
            {
                ReleaseSession();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recreate BizApp after timeout");
        }
    }

    private void ReleaseSession()
    {
        if (_bizApp is null) return;
        if (_sessionOwnedByUs)
        {
            try
            {
                if ((bool)_bizApp.IsLogin)
                    _bizApp.Logout();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Logout failed during release");
            }
        }

        ComHelper.SafeRelease(_bizApp);
        _bizApp = null;
        _sessionOwnedByUs = false;
        _currentCompany = "";
    }

    private static void ValidateCreds(SdkCompanyCredentials creds)
    {
        if (string.IsNullOrWhiteSpace(creds.Username))
            throw new InvalidOperationException("SDK username is required.");
        if (string.IsNullOrWhiteSpace(creds.DcfPath))
            throw new InvalidOperationException("SDK DCF path is required.");
        if (!File.Exists(creds.DcfPath))
            throw new InvalidOperationException("DCF file not found: " + creds.DcfPath);
        if (string.IsNullOrWhiteSpace(creds.Database))
            throw new InvalidOperationException("SDK company database filename is required.");
    }

    private static bool IsFatalComFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is COMException com)
            {
                var hr = unchecked((uint)com.ErrorCode);
                if (hr is 0x8000FFFF or 0x800706BA or 0x800706BE or 0x80010001 or 0x80004005)
                    return true;
            }
            var msg = cur.Message ?? "";
            if (msg.Contains("Access violation", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("RPC_E", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("RPC server", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _gate.Wait(TimeSpan.FromSeconds(5));
            try { _worker.Invoke(ReleaseSession); }
            finally { _gate.Release(); }
        }
        catch { /* ignore on dispose */ }
        _worker.Dispose();
        _gate.Dispose();
    }
}
