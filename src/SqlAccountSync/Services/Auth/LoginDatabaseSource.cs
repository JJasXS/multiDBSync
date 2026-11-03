using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Services.Auth;

public interface ILoginDatabaseSource
{
    Task<TenantDatabaseInfo> RequireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Login database: local <c>LoginDb</c> path when configured; otherwise AWS tenant config <c>database.dbPath</c>.
/// </summary>
public sealed class LoginDatabaseSource : ILoginDatabaseSource
{
    private readonly ITenantDatabaseBootstrap _tenantDb;
    private readonly IConfiguration _config;
    private readonly LoginDbOptions _loginDb;

    public LoginDatabaseSource(
        ITenantDatabaseBootstrap tenantDb,
        IConfiguration config,
        IOptions<LoginDbOptions> loginDb)
    {
        _tenantDb = tenantDb;
        _config = config;
        _loginDb = loginDb.Value;
    }

    public async Task<TenantDatabaseInfo> RequireAsync(CancellationToken cancellationToken = default)
    {
        var localPath = (_loginDb.Path ?? "").Trim();
        if (localPath.Length > 0)
        {
            return new TenantDatabaseInfo
            {
                DbPath = localPath.Replace('\\', '/'),
                DbHost = string.IsNullOrWhiteSpace(_loginDb.Host) ? "localhost" : _loginDb.Host.Trim(),
                DbPort = _loginDb.Port <= 0 ? 3050 : _loginDb.Port
            };
        }

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
