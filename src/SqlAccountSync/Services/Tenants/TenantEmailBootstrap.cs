using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Tenants;

public interface ITenantEmailBootstrap
{
    Task<TenantEmailOverride?> GetEmailOverrideAsync(string tenantCode, CancellationToken cancellationToken = default);
}

public interface ITenantDatabaseBootstrap
{
    Task<TenantDatabaseInfo?> GetDatabaseAsync(string tenantCode, CancellationToken cancellationToken = default);

    Task<int?> GetMaxDatabasesAsync(string tenantCode, CancellationToken cancellationToken = default);

    Task RefreshAsync(string tenantCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads tenant fields from the ProAcc tenant config API
/// (same source as eQuotation <c>tenant_bootstrap</c> / ApprovalPO).
/// </summary>
public sealed class TenantEmailBootstrap : ITenantEmailBootstrap, ITenantDatabaseBootstrap
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantEmailBootstrap> _logger;

    public TenantEmailBootstrap(
        HttpClient http,
        IConfiguration config,
        IMemoryCache cache,
        ILogger<TenantEmailBootstrap> logger)
    {
        _http = http;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TenantEmailOverride?> GetEmailOverrideAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var code = (tenantCode ?? "").Trim();
        if (code.Length == 0)
            return null;

        var cacheKey = "tenant-email:" + code.ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out TenantEmailOverride? cached))
            return cached;

        var raw = await FetchPayloadJsonAsync(code, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return null;

        using var doc = JsonDocument.Parse(raw);
        var email = TryParseTenantEmailFromRoot(doc.RootElement);
        if (email is not null)
            _cache.Set(cacheKey, email, CacheFor);
        return email;
    }

    public async Task<TenantDatabaseInfo?> GetDatabaseAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var code = (tenantCode ?? "").Trim();
        if (code.Length == 0)
            return null;

        var cacheKey = "tenant-db:" + code.ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out TenantDatabaseInfo? cached))
            return cached;

        var raw = await FetchPayloadJsonAsync(code, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return null;

        using var doc = JsonDocument.Parse(raw);
        if (!TryFindSection(doc.RootElement, "database", 0, out var databaseAttr))
            return null;

        var db = UnwrapDynamoMap(databaseAttr);
        if (!TryScalar(db, "dbPath", out var dbPath) || dbPath.Length == 0)
            return null;

        var info = new TenantDatabaseInfo { DbPath = dbPath };
        if (TryScalar(db, "dbHost", out var host) && host.Length > 0)
            info.DbHost = host;
        if (TryScalar(db, "dbCharset", out var charset) && charset.Length > 0)
            info.DbCharset = charset;
        if (TryScalar(db, "dbPort", out var portText)
            && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port > 0)
            info.DbPort = port;
        if (TryScalar(db, "dbDialect", out var dialectText)
            && int.TryParse(dialectText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dialect)
            && dialect > 0)
            info.DbDialect = dialect;

        _cache.Set(cacheKey, info, CacheFor);
        return info;
    }

    public async Task<int?> GetMaxDatabasesAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var code = (tenantCode ?? "").Trim();
        if (code.Length == 0)
            return null;

        var cacheKey = "tenant-maxdb:" + code.ToUpperInvariant();
        if (_cache.TryGetValue(cacheKey, out int cached) && cached > 0)
            return cached;

        var raw = await FetchPayloadJsonAsync(code, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return null;

        using var doc = JsonDocument.Parse(raw);
        var max = ReadMaxDatabases(doc.RootElement);
        if (max is > 0)
            _cache.Set(cacheKey, max.Value, CacheFor);
        return max;
    }

    public async Task RefreshAsync(string tenantCode, CancellationToken cancellationToken = default)
    {
        var code = (tenantCode ?? "").Trim();
        if (code.Length == 0)
            return;

        var key = code.ToUpperInvariant();
        _cache.Remove("tenant-payload:" + key);
        _cache.Remove("tenant-email:" + key);
        _cache.Remove("tenant-db:" + key);
        _cache.Remove("tenant-maxdb:" + key);
        await FetchPayloadJsonAsync(code, cancellationToken).ConfigureAwait(false);
        await GetEmailOverrideAsync(code, cancellationToken).ConfigureAwait(false);
        await GetDatabaseAsync(code, cancellationToken).ConfigureAwait(false);
        await GetMaxDatabasesAsync(code, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FetchPayloadJsonAsync(string code, CancellationToken cancellationToken)
    {
        var payloadKey = "tenant-payload:" + code.ToUpperInvariant();
        if (_cache.TryGetValue(payloadKey, out string? cachedJson) && !string.IsNullOrWhiteSpace(cachedJson))
            return cachedJson;

        var baseUrl = (_config["TenantBootstrap:AwsApiBaseUrl"]
                       ?? Environment.GetEnvironmentVariable("TENANT_BOOTSTRAP_API_URL")
                       ?? "").Trim();
        if (baseUrl.Length == 0)
        {
            _logger.LogWarning("TenantBootstrap:AwsApiBaseUrl / TENANT_BOOTSTRAP_API_URL is empty; cannot load tenant config.");
            return null;
        }

        try
        {
            var sep = baseUrl.Contains('?') ? "&" : "?";
            var url = $"{baseUrl.TrimEnd('/')}{sep}tenantCode={Uri.EscapeDataString(code)}";
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenant config API returned {Status} for {Tenant}.", (int)resp.StatusCode, code);
                return null;
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            JsonDocument? inner = null;
            try
            {
                var root = UnwrapPayload(doc.RootElement, ref inner);
                var raw = root.GetRawText();
                _cache.Set(payloadKey, raw, CacheFor);
                return raw;
            }
            finally
            {
                inner?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tenant config for {Tenant}.", code);
            return null;
        }
    }

    private static JsonElement UnwrapPayload(JsonElement root, ref JsonDocument? inner)
    {
        if (TryProp(root, "body", out var bodyEl))
        {
            if (bodyEl.ValueKind == JsonValueKind.String)
            {
                var s = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    inner = JsonDocument.Parse(s.Trim());
                    root = inner.RootElement;
                }
            }
            else if (bodyEl.ValueKind == JsonValueKind.Object)
            {
                root = bodyEl;
            }
        }

        if (TryProp(root, "Item", out var item) && item.ValueKind == JsonValueKind.Object)
            root = item;
        if (TryProp(root, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            root = data;
        return root;
    }

    private static TenantEmailOverride? TryParseTenantEmailFromRoot(JsonElement root)
    {
        TenantEmailOverride? merged = null;
        if (TryFindSection(root, "email", 0, out var emailAttr))
            merged = ParseMap(emailAttr);
        if (TryFindSection(root, "proaccEmail", 0, out var proaccAttr))
            merged = Merge(merged, ParseMap(proaccAttr));
        if (merged is null || !HasAny(merged))
            return null;
        return merged;
    }

    private static TenantEmailOverride Merge(TenantEmailOverride? baseline, TenantEmailOverride next)
    {
        baseline ??= new TenantEmailOverride();
        if (next.SmtpHost != null) baseline.SmtpHost = next.SmtpHost;
        if (next.SmtpPort != null) baseline.SmtpPort = next.SmtpPort;
        if (next.SmtpSenderEmail != null) baseline.SmtpSenderEmail = next.SmtpSenderEmail;
        if (next.SmtpAppPasswordSecretRef != null) baseline.SmtpAppPasswordSecretRef = next.SmtpAppPasswordSecretRef;
        if (next.SmtpCredentialsSecretRef != null) baseline.SmtpCredentialsSecretRef = next.SmtpCredentialsSecretRef;
        if (next.OtpEnabled != null) baseline.OtpEnabled = next.OtpEnabled;
        return baseline;
    }

    private static bool HasAny(TenantEmailOverride o) =>
        o.SmtpHost is not null || o.SmtpPort is not null || o.SmtpSenderEmail is not null
        || o.SmtpAppPasswordSecretRef is not null || o.SmtpCredentialsSecretRef is not null || o.OtpEnabled is not null;

    private static TenantEmailOverride ParseMap(JsonElement map)
    {
        var o = new TenantEmailOverride();
        if (TryScalar(map, "smtpHost", out var host)) o.SmtpHost = host;
        if (TryScalar(map, "smtpPort", out var portText)
            && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port > 0)
            o.SmtpPort = port;
        if (TryScalar(map, "smtpSenderEmail", out var sender)) o.SmtpSenderEmail = sender;
        if (TryScalar(map, "smtpAppPasswordSecretRef", out var appRef)) o.SmtpAppPasswordSecretRef = appRef;
        if (TryScalar(map, "smtpCredentialsSecretRef", out var credRef)) o.SmtpCredentialsSecretRef = credRef;
        if (TryScalar(map, "otpEnabled", out var otpText))
            o.OtpEnabled = otpText.Equals("true", StringComparison.OrdinalIgnoreCase) || otpText == "1";
        return o;
    }

    private static int? ReadMaxDatabases(JsonElement root)
    {
        foreach (var name in new[] { "maxDatabases", "MAX_DBCREATE", "maxDbCreate" })
        {
            if (TryFindSection(root, name, 0, out var el) && TryLooseNumber(el, out var max))
                return max;
        }
        return null;
    }

    private static bool TryLooseNumber(JsonElement el, out int number)
    {
        number = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out number))
            return number > 0;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number > 0;
        if (el.ValueKind == JsonValueKind.Object && TryProp(el, "N", out var n))
        {
            var text = n.ValueKind == JsonValueKind.String ? n.GetString() : n.GetRawText();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0;
        }
        return false;
    }

    private static JsonElement UnwrapDynamoMap(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return el;
        if (TryProp(el, "M", out var m) && m.ValueKind == JsonValueKind.Object)
            return m;
        return el;
    }

    private static bool TryFindSection(JsonElement root, string name, int depth, out JsonElement found)
    {
        found = default;
        if (depth > 8 || root.ValueKind != JsonValueKind.Object)
            return false;
        if (TryProp(root, name, out found))
            return true;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object && TryFindSection(p.Value, name, depth + 1, out found))
                return true;
        }
        return false;
    }

    private static bool TryScalar(JsonElement map, string name, out string value)
    {
        value = "";
        if (!TryProp(map, name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (TryProp(el, "S", out var s) && s.ValueKind == JsonValueKind.String)
            {
                value = s.GetString()?.Trim() ?? "";
                return value.Length > 0;
            }
            if (TryProp(el, "N", out var n))
            {
                value = n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "") : n.ToString();
                value = value.Trim();
                return value.Length > 0;
            }
            if (TryProp(el, "BOOL", out var b))
            {
                value = b.ValueKind == JsonValueKind.True ? "true" : b.ValueKind == JsonValueKind.False ? "false" : b.ToString();
                return true;
            }
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString()?.Trim() ?? "";
            return value.Length > 0;
        }
        if (el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = el.ToString();
            return true;
        }
        return false;
    }

    private static bool TryProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }
}
