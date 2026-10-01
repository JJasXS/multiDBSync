using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Tenants;

/// <summary>Re-reads the AWS tenant JSON once a minute so plan fields such as maxDatabases stay current.</summary>
public sealed class TenantConfigRefreshWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<TenantConfigRefreshWorker> _logger;
    private readonly TenantMenuFlags _menu;
    private int _secretsMigrated;

    public TenantConfigRefreshWorker(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<TenantConfigRefreshWorker> logger,
        TenantMenuFlags menu)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _menu = menu;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tenant = TenantCode();
        if (tenant.Length == 0)
        {
            _logger.LogWarning("Tenant config refresh skipped because TENANT_CODE is empty.");
            return;
        }

        _logger.LogInformation("Tenant config refresh every {Minutes} minute for {Tenant}.", Interval.TotalMinutes, tenant);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(tenant, stoppingToken).ConfigureAwait(false);
            if (!await WaitAsync(stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    private string TenantCode() =>
        (_config["TenantBootstrap:TenantCode"]
         ?? _config["Sync:TenantCode"]
         ?? Environment.GetEnvironmentVariable("TENANT_CODE")
         ?? "").Trim();

    private async Task RefreshOnceAsync(string tenant, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantDatabaseBootstrap>();
            await tenants.RefreshAsync(tenant, stoppingToken).ConfigureAwait(false);
            var menu = await tenants.GetMenuFlagsAsync(tenant, stoppingToken).ConfigureAwait(false);
            _menu.Apply(menu.ShowSynchronization, menu.ShowDocumentTransfer);
            await MigrateSecretsOnceAsync(scope.ServiceProvider, tenant, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant config refresh failed for {Tenant}.", tenant);
        }
    }

    private async Task MigrateSecretsOnceAsync(IServiceProvider services, string tenant, CancellationToken cancellationToken)
    {
        if (_secretsMigrated != 0)
            return;
        var bundle = services.GetRequiredService<ISqlApiCredentialBundle>();
        var databases = services.GetRequiredService<IDatabaseRegistry>();
        await bundle.MigrateLegacyAsync(tenant, cancellationToken).ConfigureAwait(false);
        await databases.PointSqlApiCredRefsAtBundleAsync(tenant, cancellationToken).ConfigureAwait(false);
        _secretsMigrated = 1;
    }

    private static async Task<bool> WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
