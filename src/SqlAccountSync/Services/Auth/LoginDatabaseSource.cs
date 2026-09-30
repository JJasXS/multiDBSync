using SqlAccountSync.Models;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Services.Auth;

public interface ILoginDatabaseSource
{
    Task<TenantDatabaseInfo> RequireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Login database path comes from the AWS tenant config <c>database.dbPath</c>.</summary>
public sealed class LoginDatabaseSource : ILoginDatabaseSource
{
    private readonly ITenantDatabaseBootstrap _tenantDb;
    private readonly IConfiguration _config;

    public LoginDatabaseSource(ITenantDatabaseBootstrap tenantDb, IConfiguration config)
    {
        _tenantDb = tenantDb;
        _config = config;
    }

    public async Task<TenantDatabaseInfo> RequireAsync(CancellationToken cancellationToken = default)
    {
        var tenant = (_config["TenantBootstrap:TenantCode"]
                      ?? _config["Sync:TenantCode"]
                      ?? Environment.GetEnvironmentVariable("TENANT_CODE")
                      ?? "").Trim();
        if (tenant.Length == 0)
            throw new InvalidOperationException("Tenant code is not set, so the login database cannot be loaded from AWS.");

        var db = await _tenantDb.GetDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (db is null || string.IsNullOrWhiteSpace(db.DbPath))
            throw new InvalidOperationException("AWS tenant config has no login database path.");
        return db;
    }
}
