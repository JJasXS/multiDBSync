using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

public interface ISqlAccSessionManager
{
    /// <summary>
    /// Ensures COM is logged into the company identified by <paramref name="db"/> (DCF + FDB file name).
    /// Serializes multi-company access: one Login at a time.
    /// </summary>
    Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> on the STA thread while logged into <paramref name="db"/>.
    /// Company target comes from <see cref="RegisteredDatabase.FbPath"/> file name + configured DCF — never last-used defaults.
    /// </summary>
    Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Multi-company SDK session: single COM app, Logout+Login when the target company changes.
/// Does not open Firebird directly and does not perform BizObject writes.
/// </summary>
public sealed class SqlAccSessionManager : ISqlAccSessionManager, IAsyncDisposable, IDisposable
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly ISecretResolver _secrets;
    private readonly SqlAccSdkOptions _options;
    private readonly ILogger<SqlAccSessionManager> _logger;
    private readonly StaWorkQueue _sta = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISqlAccComApp? _app;
    private string? _activeCompanyKey;
    private bool _disposed;

    public SqlAccSessionManager(
        ISqlAccComAppFactory factory,
        ISecretResolver secrets,
        IOptions<SqlAccSdkOptions> options,
        ILogger<SqlAccSessionManager> logger)
    {
        _factory = factory;
        _secrets = secrets;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.AllowLiveCom)
            throw new InvalidOperationException("SqlAccSdk:AllowLiveCom is false; SDK session is disabled.");

        var company = ResolveCompanyTarget(db);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoggedInAsync(company, cancellationToken).ConfigureAwait(false);
            return new SessionLease(_gate);
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
        await using var lease = new AsyncSessionLease(
            await UseCompanyAsync(db, cancellationToken).ConfigureAwait(false));
        return await _sta.RunAsync(() =>
        {
            if (_app is null)
                throw new InvalidOperationException("SDK COM app is not initialized after Login.");
            return action(_app);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureLoggedInAsync(CompanyTarget company, CancellationToken cancellationToken)
    {
        var user = (_options.UserName ?? "").Trim();
        if (user.Length == 0)
            throw new InvalidOperationException("SqlAccSdk:UserName is not configured.");

        var password = await _secrets.ResolveAsync(_options.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("SqlAccSdk:PasswordSecretRef did not resolve a password.");

        await _sta.RunAsync(() =>
        {
            _app ??= _factory.Create();
            var key = company.Key;
            if (_app.IsLogin && string.Equals(_activeCompanyKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            if (_app.IsLogin)
            {
                _logger.LogInformation("SDK Logout before switching company from {From} to {To}", _activeCompanyKey, key);
                _app.Logout();
                _activeCompanyKey = null;
            }

            _logger.LogInformation("SDK Login company file {DatabaseFile}", company.DatabaseFileName);
            _app.Login(user, password.Trim(), company.DcfPath, company.DatabaseFileName);
            if (!_app.IsLogin)
            {
                var diag = _app is SqlAccComApp com ? com.LastComDiagnostics : _app.LastComDiagnostics;
                throw new InvalidOperationException(
                    "SQLAcc.BizApp Login did not establish a session (IsLogin=false). diag={" + diag + "}");
            }
            _activeCompanyKey = key;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static CompanyTarget ResolveCompanyTarget(RegisteredDatabase db, string? defaultDcfPath = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var dcf = (defaultDcfPath ?? "").Trim();
        // Prefer explicit future fields via Notes convention? Keep deterministic: options DefaultDcfPath only for now.
        if (dcf.Length == 0)
            throw new InvalidOperationException(
                "SqlAccSdk:DefaultDcfPath is required for SDK Login (CHM requires DCF path for automation hosts).");

        var file = Path.GetFileName((db.FbPath ?? "").Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException(
                $"Registered database '{db.DbCode}' has no FbPath file name for SDK Login (4th parameter).");

        return new CompanyTarget(dcf, file);
    }

    private CompanyTarget ResolveCompanyTarget(RegisteredDatabase db) =>
        ResolveCompanyTarget(db, _options.DefaultDcfPath);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _sta.RunAsync(() =>
            {
                _app?.Dispose();
                _app = null;
                _activeCompanyKey = null;
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

    private sealed class SessionLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
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
