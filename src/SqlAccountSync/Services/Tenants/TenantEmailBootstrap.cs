using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Tenants;

public interface ITenantEmailBootstrap
{
    Task<TenantEmailOverride?> GetEmailOverrideAsync(string tenantCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads <c>email</c> + <c>proaccEmail</c> from the ProAcc tenant config API
/// (same source as eQuotation <c>tenant_bootstrap</c> / ApprovalPO).
/// </summary>
public sealed class TenantEmailBootstrap : ITenantEmailBootstrap
{
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

        var baseUrl = (_config["TenantBootstrap:AwsApiBaseUrl"]
                       ?? Environment.GetEnvironmentVariable("TENANT_BOOTSTRAP_API_URL")
                       ?? "").Trim();
        if (baseUrl.Length == 0)
        {
            _logger.LogWarning("TenantBootstrap:AwsApiBaseUrl / TENANT_BOOTSTRAP_API_URL is empty; cannot load SMTP from tenant.");
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
                var email = TryParseTenantEmailFromRoot(root);
                if (email is not null)
                    _cache.Set(cacheKey, email, TimeSpan.FromMinutes(10));
                return email;
            }
            finally
            {
                inner?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tenant email overrides for {Tenant}.", code);
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
        // DynamoDB attribute shape { "S": "..." } / { "N": "..." }
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
