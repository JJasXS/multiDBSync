namespace SqlAccountSync.Services.Tenants;

/// <summary>Re-reads the AWS tenant JSON once a minute so plan fields such as maxDatabases stay current.</summary>
public sealed class TenantConfigRefreshWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<TenantConfigRefreshWorker> _logger;

    public TenantConfigRefreshWorker(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<TenantConfigRefreshWorker> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
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
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant config refresh failed for {Tenant}.", tenant);
        }
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
