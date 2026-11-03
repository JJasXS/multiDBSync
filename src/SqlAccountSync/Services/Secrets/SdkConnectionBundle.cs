using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Sdk;

namespace SqlAccountSync.Services.Secrets;

public interface ISdkConnectionBundle
{
    string SecretId { get; }

    Task<SdkCompanyCredentials?> ResolveAsync(string dbCode, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> SaveCompanyAsync(
        SdkCompanyCredentials company, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListCompanyNamesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One Secrets Manager secret per tenant holding every company SDK login:
/// proacc/tenant/{TENANT}/sdkConnections with a companies[] array.
/// </summary>
public sealed class SdkConnectionBundle : ISdkConnectionBundle
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly IAmazonSecretsManager? _sm;
    private readonly IMemoryCache _cache;
    private readonly SyncOptions _sync;
    private readonly ILogger<SdkConnectionBundle> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SdkConnectionBundle(
        IMemoryCache cache,
        IOptions<SyncOptions> sync,
        ILogger<SdkConnectionBundle> logger,
        IAmazonSecretsManager? sm = null)
    {
        _cache = cache;
        _sync = sync.Value;
        _logger = logger;
        _sm = sm;
    }

    public string SecretId
    {
        get
        {
            var overrideId = (Environment.GetEnvironmentVariable("SDK_SECRET_ID")
                ?? _sync.SdkSecretId ?? "").Trim();
            if (overrideId.Length > 0)
                return overrideId.StartsWith("sm:", StringComparison.OrdinalIgnoreCase)
                    ? overrideId[3..]
                    : overrideId;
            var tenant = (_sync.TenantCode ?? "").Trim();
            return tenant.Length == 0
                ? "proacc/tenant/unknown/sdkConnections"
                : $"proacc/tenant/{tenant}/sdkConnections";
        }
    }

    public async Task<SdkCompanyCredentials?> ResolveAsync(
        string dbCode, CancellationToken cancellationToken = default)
    {
        var code = Sanitize(dbCode);
        if (code.Length == 0)
            return null;

        var map = await GetCachedAsync(cancellationToken).ConfigureAwait(false);
        return map.TryGetValue(code, out var hit) ? hit : null;
    }

    public async Task<IReadOnlyList<string>> ListCompanyNamesAsync(CancellationToken cancellationToken = default)
    {
        var map = await GetCachedAsync(cancellationToken).ConfigureAwait(false);
        return map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<(bool Ok, string Message)> SaveCompanyAsync(
        SdkCompanyCredentials company, CancellationToken cancellationToken = default)
    {
        var code = Sanitize(company.Name);
        if (code.Length == 0)
            return (false, "Company name is required.");
        if (string.IsNullOrWhiteSpace(company.DcfPath) || string.IsNullOrWhiteSpace(company.Database))
            return (false, "dcfPath and database are required.");
        if (_sm is null)
            return (false, "AWS Secrets Manager client is not configured.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadFreshAsync(cancellationToken).ConfigureAwait(false);
            map[code] = new SdkCompanyCredentials
            {
                Name = code,
                DcfPath = company.DcfPath.Trim(),
                Database = company.Database.Trim(),
                Username = string.IsNullOrWhiteSpace(company.Username) ? "ADMIN" : company.Username.Trim(),
                Password = company.Password ?? ""
            };
            await WriteMapAsync(map, cancellationToken).ConfigureAwait(false);
            _cache.Set(CacheKey(), map, CacheFor);
            return (true, $"Saved SDK connection for {code} in {SecretId}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save SDK connection for {Code}", code);
            return (false, $"Could not save {code}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, SdkCompanyCredentials>> GetCachedAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(), out Dictionary<string, SdkCompanyCredentials>? hit) && hit is not null)
            return hit;
        var map = await ReadFreshAsync(cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey(), map, CacheFor);
        return map;
    }

    private async Task<Dictionary<string, SdkCompanyCredentials>> ReadFreshAsync(CancellationToken cancellationToken)
    {
        if (_sm is null)
            return new Dictionary<string, SdkCompanyCredentials>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var resp = await _sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = SecretId }, cancellationToken)
                .ConfigureAwait(false);
            return ParseCompanies(resp.SecretString);
        }
        catch (ResourceNotFoundException)
        {
            return new Dictionary<string, SdkCompanyCredentials>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteMapAsync(
        Dictionary<string, SdkCompanyCredentials> map, CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["companies"] = ToArray(map)
        };
        var payload = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            await _sm!.CreateSecretAsync(new CreateSecretRequest
            {
                Name = SecretId,
                Description = $"SqlAccountSync SDK company logins for tenant {_sync.TenantCode}.",
                SecretString = payload,
                Tags =
                [
                    new Tag { Key = "tenant", Value = _sync.TenantCode ?? "" },
                    new Tag { Key = "app", Value = "SqlAccountSync" },
                    new Tag { Key = "kind", Value = "sdkConnections" }
                ]
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceExistsException)
        {
            await _sm!.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = SecretId,
                SecretString = payload
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Dictionary<string, SdkCompanyCredentials> ParseCompanies(string? json)
    {
        var map = new Dictionary<string, SdkCompanyCredentials>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("companies", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Text(row, "name", "dbCode", "code");
                var code = Sanitize(name);
                if (code.Length == 0)
                    continue;
                map[code] = new SdkCompanyCredentials
                {
                    Name = code,
                    DcfPath = Text(row, "dcfPath", "dcf", "dcf_path"),
                    Database = Text(row, "database", "db", "databaseName"),
                    Username = Text(row, "username", "user") is { Length: > 0 } u ? u : "ADMIN",
                    Password = Text(row, "password", "pwd")
                };
            }
        }
        catch (JsonException)
        {
            return map;
        }

        return map;
    }

    private static JsonArray ToArray(Dictionary<string, SdkCompanyCredentials> map)
    {
        var rows = new JsonArray();
        foreach (var pair in map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new JsonObject
            {
                ["name"] = pair.Value.Name,
                ["dcfPath"] = pair.Value.DcfPath,
                ["database"] = pair.Value.Database,
                ["username"] = pair.Value.Username,
                ["password"] = pair.Value.Password
            });
        }
        return rows;
    }

    private static string Text(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString() ?? "";
            }
        }
        return "";
    }

    internal static string Sanitize(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private string CacheKey() => "sdk-bundle:" + SecretId.ToUpperInvariant();
}
