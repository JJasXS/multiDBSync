using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Caching.Memory;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Secrets;

public interface ISqlApiCredentialBundle
{
    Task<SqlApiCredentials?> ResolveAsync(string tenantCode, RegisteredDatabase db, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> SaveAsync(
        string tenantCode,
        string dbCode,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string tenantCode, string dbCode, CancellationToken cancellationToken = default);

    Task MigrateLegacyAsync(string tenantCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores every company SQL API key pair in one Secrets Manager secret per tenant.
/// </summary>
public sealed class SqlApiCredentialBundle : ISqlApiCredentialBundle
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly IAmazonSecretsManager? _sm;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SqlApiCredentialBundle> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqlApiCredentialBundle(
        IMemoryCache cache,
        ILogger<SqlApiCredentialBundle> logger,
        IAmazonSecretsManager? sm = null)
    {
        _cache = cache;
        _logger = logger;
        _sm = sm;
    }

    public async Task<SqlApiCredentials?> ResolveAsync(
        string tenantCode, RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var tenant = (tenantCode ?? "").Trim();
        var code = SqlApiCredRefConvention.SanitizeDbCode(db.DbCode);
        if (!UsesBundle(tenant, db.ApiCredRef) || code.Length == 0)
            return null;

        var map = await GetCachedAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (map.TryGetValue(code, out var hit))
            return hit;
        return await AdoptLegacyAsync(tenant, SqlApiCredRefConvention.BuildLegacySecretId(tenant, code), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool Ok, string Message)> SaveAsync(
        string tenantCode,
        string dbCode,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default)
    {
        var tenant = (tenantCode ?? "").Trim();
        var code = SqlApiCredRefConvention.SanitizeDbCode(dbCode);
        if (tenant.Length == 0 || code.Length == 0)
            return (false, "Tenant code or database Code missing.");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            return (false, "Access key and secret key are both required.");
        if (_sm is null)
            return (false, "AWS Secrets Manager client is not configured.");

        var secretId = SqlApiCredRefConvention.BuildBundleSecretId(tenant);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadFreshAsync(tenant, cancellationToken).ConfigureAwait(false);
            map[code] = new SqlApiCredentials(accessKey.Trim(), secretKey.Trim());
            await WriteMapAsync(tenant, map, cancellationToken).ConfigureAwait(false);
            _cache.Set(CacheKey(tenant), map, CacheFor);
            _logger.LogInformation("Saved SQL API keys for {Code} in {SecretId}", code, secretId);
            return (true, $"Saved keys for {code} in {secretId}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save SQL API keys for {Code} in {SecretId}", code, secretId);
            return (false, $"Could not save keys for {code}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string tenantCode, string dbCode, CancellationToken cancellationToken = default)
    {
        var tenant = (tenantCode ?? "").Trim();
        var code = SqlApiCredRefConvention.SanitizeDbCode(dbCode);
        if (tenant.Length == 0 || code.Length == 0 || _sm is null)
            return true;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoveNamedAsync(tenant, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove SQL API keys for {Code}.", code);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RemoveNamedAsync(string tenant, string code, CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            root = await ReadRootAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            return true;
        }

        if (root["databases"] is not JsonArray rows || !DropNamed(rows, code))
            return true;

        await StoreRootAsync(tenant, root, cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey(tenant), ParseDatabases(root), CacheFor);
        _logger.LogInformation("Removed SQL API keys for {Code} from {SecretId}", code, SqlApiCredRefConvention.BuildBundleSecretId(tenant));
        return true;
    }

    private static bool DropNamed(JsonArray rows, string code)
    {
        var removed = false;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i] is not JsonObject obj)
                continue;
            var name = SqlApiCredRefConvention.SanitizeDbCode(Text(obj, "name"));
            if (!name.Equals(code, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    public async Task MigrateLegacyAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var tenant = (tenantCode ?? "").Trim();
        if (tenant.Length == 0 || _sm is null)
            return;

        foreach (var id in await ListLegacyIdsAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await AdoptLegacyAsync(tenant, id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not move legacy SQL API secret {SecretId}", id);
            }
        }
    }

    private async Task<SqlApiCredentials?> AdoptLegacyAsync(string tenant, string legacyId, CancellationToken cancellationToken)
    {
        var code = CodeFromLegacyId(tenant, legacyId);
        if (code.Length == 0 || _sm is null)
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdoptLegacyCoreAsync(tenant, legacyId, code, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqlApiCredentials?> AdoptLegacyCoreAsync(
        string tenant, string legacyId, string code, CancellationToken cancellationToken)
    {
        var legacy = await ReadPairAsync(legacyId, cancellationToken).ConfigureAwait(false);
        if (legacy is null)
            return null;

        var map = await ReadFreshAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (!map.ContainsKey(code))
        {
            map[code] = legacy;
            await WriteMapAsync(tenant, map, cancellationToken).ConfigureAwait(false);
        }

        var confirmed = await ReadFreshAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (!confirmed.TryGetValue(code, out var kept))
            return null;

        await DeleteLegacyAsync(legacyId, cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey(tenant), confirmed, CacheFor);
        _logger.LogInformation("Moved SQL API keys for {Code} into {SecretId}", code, SqlApiCredRefConvention.BuildBundleSecretId(tenant));
        return kept;
    }

    private async Task<Dictionary<string, SqlApiCredentials>> GetCachedAsync(string tenant, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(tenant), out Dictionary<string, SqlApiCredentials>? hit) && hit is not null)
            return hit;

        var map = await ReadFreshAsync(tenant, cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey(tenant), map, CacheFor);
        return map;
    }

    private async Task<Dictionary<string, SqlApiCredentials>> ReadFreshAsync(string tenant, CancellationToken cancellationToken)
    {
        if (_sm is null)
            return new Dictionary<string, SqlApiCredentials>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = await ReadRootAsync(tenant, cancellationToken).ConfigureAwait(false);
            return ParseDatabases(root);
        }
        catch (ResourceNotFoundException)
        {
            return new Dictionary<string, SqlApiCredentials>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteMapAsync(
        string tenant, Dictionary<string, SqlApiCredentials> map, CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            root = await ReadRootAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            root = EmptyRoot();
        }

        root["databases"] = ToDatabaseArray(map);
        await StoreRootAsync(tenant, root, cancellationToken).ConfigureAwait(false);
    }

    private async Task StoreRootAsync(string tenant, JsonObject root, CancellationToken cancellationToken)
    {
        var payload = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var secretId = SqlApiCredRefConvention.BuildBundleSecretId(tenant);
        try
        {
            await _sm!.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secretId,
                Description = $"SqlAccountSync SQL API keys for tenant {tenant}.",
                SecretString = payload,
                Tags =
                [
                    new Tag { Key = "tenant", Value = tenant },
                    new Tag { Key = "app", Value = "SqlAccountSync" }
                ]
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceExistsException)
        {
            await _sm!.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretId,
                SecretString = payload
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> ListLegacyIdsAsync(string tenant, CancellationToken cancellationToken)
    {
        var prefix = SqlApiCredRefConvention.LegacyPrefix(tenant);
        var ids = new List<string>();
        string? token = null;
        do
        {
            var page = await _sm!.ListSecretsAsync(new ListSecretsRequest
            {
                NextToken = token,
                Filters = [new Filter { Key = FilterNameStringType.Name, Values = [prefix] }]
            }, cancellationToken).ConfigureAwait(false);
            var bundleId = SqlApiCredRefConvention.BuildBundleSecretId(tenant);
            ids.AddRange(page.SecretList
                .Select(s => s.Name)
                .Where(name => !name.Equals(bundleId, StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && CodeFromLegacyId(tenant, name).Length > 0));
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));
        return ids;
    }

    private async Task<SqlApiCredentials?> ReadPairAsync(string secretId, CancellationToken cancellationToken)
    {
        try
        {
            var resp = await _sm!.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken)
                .ConfigureAwait(false);
            return ParsePair(resp.SecretString);
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    private async Task DeleteLegacyAsync(string secretId, CancellationToken cancellationToken)
    {
        if (!secretId.Contains("/sqlApiCredentials/", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            await _sm!.DeleteSecretAsync(new DeleteSecretRequest
            {
                SecretId = secretId,
                RecoveryWindowInDays = 7
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Scheduled deletion of per-company secret {SecretId}", secretId);
        }
        catch (ResourceNotFoundException)
        {
        }
        catch (InvalidRequestException ex) when (ex.Message.Contains("scheduled for deletion", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static bool UsesBundle(string tenant, string? credRef) =>
        tenant.Length > 0 && SqlApiCredRefConvention.ShouldAutoAssign(credRef, tenant);

    private static string CodeFromLegacyId(string tenant, string secretId)
    {
        var prefix = SqlApiCredRefConvention.LegacyPrefix(tenant);
        if (prefix.Length == 0 || !secretId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "";
        var suffix = secretId[prefix.Length..];
        return suffix.Contains('/') ? "" : SqlApiCredRefConvention.SanitizeDbCode(suffix);
    }

    private static string CacheKey(string tenant) => "sqlapi-bundle:" + tenant.ToUpperInvariant();

    private async Task<JsonObject> ReadRootAsync(string tenant, CancellationToken cancellationToken)
    {
        var resp = await _sm!.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = SqlApiCredRefConvention.BuildBundleSecretId(tenant)
        }, cancellationToken).ConfigureAwait(false);
        return ParseRoot(resp.SecretString);
    }

    private static JsonObject EmptyRoot() => new()
    {
        ["accessKey"] = "",
        ["secretKey"] = ""
    };

    private static JsonObject ParseRoot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyRoot();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? EmptyRoot();
        }
        catch (JsonException)
        {
            return EmptyRoot();
        }
    }

    private static JsonArray ToDatabaseArray(Dictionary<string, SqlApiCredentials> map)
    {
        var rows = new JsonArray();
        foreach (var pair in map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new JsonObject
            {
                ["name"] = pair.Key,
                ["accessKey"] = pair.Value.AccessKey,
                ["secretKey"] = pair.Value.SecretKey
            });
        }
        return rows;
    }

    private static Dictionary<string, SqlApiCredentials> ParseDatabases(JsonObject root)
    {
        var map = new Dictionary<string, SqlApiCredentials>(StringComparer.OrdinalIgnoreCase);
        if (root["databases"] is not JsonArray rows)
            return map;
        foreach (var row in rows)
        {
            if (row is not JsonObject obj)
                continue;
            var code = SqlApiCredRefConvention.SanitizeDbCode(Text(obj, "name"));
            var pair = ParsePair(obj.ToJsonString());
            if (code.Length > 0 && pair is not null)
                map[code] = pair;
        }
        return map;
    }

    private static string Text(JsonObject obj, string name)
    {
        foreach (var prop in obj)
        {
            if (!prop.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return prop.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";
        }
        return "";
    }

    private static SqlApiCredentials? ParsePair(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParsePairElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SqlApiCredentials? ParsePairElement(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        var access = Find(obj, ["accessKey", "access_key", "accessKeyId"]);
        var secret = Find(obj, ["secretKey", "secret_key", "secretAccessKey"]);
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
            return null;
        return new SqlApiCredentials(access, secret);
    }

    private static string? Find(JsonElement obj, string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        return null;
    }
}
