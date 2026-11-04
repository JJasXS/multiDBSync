using System.Text.RegularExpressions;

namespace SqlAccountSync.Helpers;

/// <summary>
/// One Secrets Manager secret per tenant holds every company key pair:
/// <c>sm:proacc/tenant/{tenantCode}/sqlApiCredentials</c>, inside a <c>databases</c> array.
/// Older per-code secrets <c>sqlApiCredentials/{DbCode}</c> are only read during migration.
/// </summary>
public static partial class SqlApiCredRefConvention
{
    public const string SecretIdPrefix = "proacc/tenant/";
    public const string BundleName = "sqlApiCredentials";

    public static string SanitizeDbCode(string? dbCode)
    {
        var raw = (dbCode ?? "").Trim().ToUpperInvariant();
        if (raw.Length == 0)
            return "";
        return CodeSanitizeRegex().Replace(raw, "");
    }

    public static string BuildBundleSecretId(string? tenantCode)
    {
        var tenant = (tenantCode ?? "").Trim();
        return tenant.Length == 0 ? "" : $"{SecretIdPrefix}{tenant}/{BundleName}";
    }

    public static string BuildBundleCredRef(string? tenantCode)
    {
        var id = BuildBundleSecretId(tenantCode);
        return id.Length == 0 ? "" : "sm:" + id;
    }

    public static string BuildLegacySecretId(string? tenantCode, string? dbCode)
    {
        var tenant = (tenantCode ?? "").Trim();
        var code = SanitizeDbCode(dbCode);
        if (tenant.Length == 0 || code.Length == 0)
            return "";
        return $"{SecretIdPrefix}{tenant}/sqlApiCredentials/{code}";
    }

    public static string LegacyPrefix(string? tenantCode)
    {
        var tenant = (tenantCode ?? "").Trim();
        return tenant.Length == 0 ? "" : $"{SecretIdPrefix}{tenant}/sqlApiCredentials/";
    }

    public static string ForDisplay(string? apiCredRef)
    {
        var r = (apiCredRef ?? "").Trim();
        return r.StartsWith("sm:", StringComparison.OrdinalIgnoreCase) ? r[3..].Trim() : r;
    }

    public static string ForStorage(string? apiCredRef)
    {
        var r = (apiCredRef ?? "").Trim();
        if (r.Length == 0)
            return "";
        if (r.StartsWith("sm:", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
            return r;
        return "sm:" + r;
    }

    public static bool IsBlankOrPlaceholder(string? apiCredRef)
    {
        var r = (apiCredRef ?? "").Trim();
        if (r.Length == 0)
            return true;
        return r.Contains("{DBID}", StringComparison.OrdinalIgnoreCase)
            || r.Contains("{CODE}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOurConvention(string? apiCredRef, string tenantCode)
    {
        var tenant = (tenantCode ?? "").Trim();
        if (tenant.Length == 0)
            return false;
        var cred = (apiCredRef ?? "").Trim();
        if (cred.Equals(BuildBundleCredRef(tenant), StringComparison.OrdinalIgnoreCase)
            || cred.Equals(BuildBundleSecretId(tenant), StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = "sm:" + LegacyPrefix(tenant);
        var bare = LegacyPrefix(tenant);
        return cred.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || cred.StartsWith(bare, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldAutoAssign(string? apiCredRef, string tenantCode) =>
        IsBlankOrPlaceholder(apiCredRef) || IsOurConvention(apiCredRef, tenantCode);

    [GeneratedRegex(@"[^A-Z0-9._@+=-]")]
    private static partial Regex CodeSanitizeRegex();
}
