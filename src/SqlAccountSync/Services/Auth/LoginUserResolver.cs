using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Services.Auth;

/// <summary>
/// Resolves login users from SQL Accounting <c>SY_USER.EMAIL</c> on the tenant login FDB
/// (AWS <c>TENANT_CONFIG.database</c>, e.g. LOGIN.FDB), then ensures a matching <c>SYNC_USER</c> for roles/ACL.
/// </summary>
public interface ILoginUserResolver
{
    Task<AppUser?> ResolveForLoginAsync(string email, CancellationToken cancellationToken = default);
}

public sealed class LoginUserResolver : ILoginUserResolver
{
    private readonly IUserDirectory _users;
    private readonly ITenantDatabaseBootstrap _tenantDb;
    private readonly ISecretResolver _secrets;
    private readonly IConfiguration _config;
    private readonly CentralDbOptions _central;
    private readonly ILogger<LoginUserResolver> _logger;

    public LoginUserResolver(
        IUserDirectory users,
        ITenantDatabaseBootstrap tenantDb,
        ISecretResolver secrets,
        IConfiguration config,
        IOptions<CentralDbOptions> central,
        ILogger<LoginUserResolver> logger)
    {
        _users = users;
        _tenantDb = tenantDb;
        _secrets = secrets;
        _config = config;
        _central = central.Value;
        _logger = logger;
    }

    public async Task<AppUser?> ResolveForLoginAsync(string email, CancellationToken cancellationToken = default)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            return null;

        var db = await ResolveLoginDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (db is null || string.IsNullOrWhiteSpace(db.DbPath))
        {
            _logger.LogWarning("No login database path; falling back to SYNC_USER.");
            var fallback = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            return fallback is null
                ? null
                : await KeepKnownRoleAsync(fallback, cancellationToken).ConfigureAwait(false);
        }

        var displayName = await LookupSyUserDisplayNameAsync(db, email, cancellationToken).ConfigureAwait(false);
        if (displayName is null)
            return null;

        return await EnsureSyncUserAsync(email, displayName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TenantDatabaseInfo?> ResolveLoginDatabaseAsync(CancellationToken cancellationToken)
    {
        var overridePath = (_config["LoginDb:Path"]
                            ?? Environment.GetEnvironmentVariable("LOGIN_DB_PATH")
                            ?? "").Trim();
        if (overridePath.Length > 0)
        {
            return new TenantDatabaseInfo
            {
                DbHost = (_config["LoginDb:Host"] ?? "localhost").Trim(),
                DbPort = int.TryParse(_config["LoginDb:Port"], out var p) && p > 0 ? p : 3050,
                DbPath = overridePath,
                DbCharset = (_config["LoginDb:Charset"] ?? "UTF8").Trim(),
                DbDialect = int.TryParse(_config["LoginDb:Dialect"], out var d) && d > 0 ? d : 3
            };
        }

        var tenantCode = (_config["TenantBootstrap:TenantCode"]
                          ?? _config["Sync:TenantCode"]
                          ?? Environment.GetEnvironmentVariable("TENANT_CODE")
                          ?? "").Trim();
        if (tenantCode.Length == 0)
            return null;

        return await _tenantDb.GetDatabaseAsync(tenantCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> LookupSyUserDisplayNameAsync(
        TenantDatabaseInfo db, string email, CancellationToken cancellationToken)
    {
        var password = await ResolveFirebirdPasswordAsync(cancellationToken).ConfigureAwait(false);
        var csb = new FbConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(db.DbHost) ? "localhost" : db.DbHost.Trim(),
            Port = db.DbPort <= 0 ? 3050 : db.DbPort,
            Database = db.DbPath.Replace('\\', '/'),
            UserID = string.IsNullOrWhiteSpace(_central.User) ? "SYSDBA" : _central.User,
            Password = password ?? "",
            Charset = string.IsNullOrWhiteSpace(db.DbCharset) ? "UTF8" : db.DbCharset,
            Dialect = (byte)Math.Clamp(db.DbDialect <= 0 ? 3 : db.DbDialect, 1, 3)
        };

        try
        {
            await using var conn = new FbConnection(csb.ToString());
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await QuerySyUserAsync(conn, email, includeIsActive: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("ISACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return await QuerySyUserAsync(conn, email, includeIsActive: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SY_USER login lookup failed against {Path}.", db.DbPath);
            return null;
        }
    }

    private static async Task<string?> QuerySyUserAsync(
        FbConnection conn, string email, bool includeIsActive, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeIsActive
            ? """
              SELECT FIRST 1 TRIM(NAME) AS DISP_NAME, ISACTIVE
              FROM SY_USER
              WHERE UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = UPPER(TRIM(@Email))
              """
            : """
              SELECT FIRST 1 TRIM(NAME) AS DISP_NAME
              FROM SY_USER
              WHERE UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = UPPER(TRIM(@Email))
              """;
        cmd.Parameters.AddWithValue("@Email", email);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        if (includeIsActive && !reader.IsDBNull(1) && !IsActive(reader.GetValue(1)))
            return null;

        var name = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
        return name.Length > 0 ? name : email;
    }

    private async Task<AppUser?> EnsureSyncUserAsync(string email, string displayName, CancellationToken cancellationToken)
    {
        var existing = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await KeepKnownRoleAsync(existing, cancellationToken).ConfigureAwait(false);

        var id = await _users.SaveAsync(new AppUser
        {
            Email = email.ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
            Role = AppRoles.Viewer,
            IsActive = true
        }, cancellationToken).ConfigureAwait(false);

        return await _users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppUser?> KeepKnownRoleAsync(AppUser user, CancellationToken cancellationToken)
    {
        var role = AppRoles.Normalize(user.Role);
        if (string.Equals(user.Role, role, StringComparison.OrdinalIgnoreCase))
            return user;

        user.Role = role;
        await _users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
        return await _users.GetByIdAsync(user.UserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveFirebirdPasswordAsync(CancellationToken cancellationToken)
    {
        var fromEnv = Environment.GetEnvironmentVariable("FIREBIRD_PASSWORD")
                      ?? Environment.GetEnvironmentVariable("CENTRAL_FB_MASTERKEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        return await _secrets.ResolveAsync(_central.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsActive(object? value)
    {
        if (value is null || value is DBNull)
            return true;
        if (value is bool b)
            return b;
        if (value is short s)
            return s != 0;
        if (value is int i)
            return i != 0;
        var t = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return t is null or "" or "1" or "T" or "Y" or "TRUE";
    }
}
