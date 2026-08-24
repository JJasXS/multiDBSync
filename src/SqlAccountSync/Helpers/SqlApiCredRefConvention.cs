using System.Text.RegularExpressions;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Standardized SQL Accounting API secret path using the database <c>Code</c> field:
/// <c>sm:proacc/tenant/{tenantCode}/sqlApiCredentials/{DbCode}</c>
/// </summary>
public static partial class SqlApiCredRefConvention
{
    public const string SecretIdPrefix = "proacc/tenant/";

    public static string SanitizeDbCode(string? dbCode)
    {
        var raw = (dbCode ?? "").Trim().ToUpperInvariant();
        if (raw.Length == 0)
            return "";
        return CodeSanitizeRegex().Replace(raw, "");
    }

    public static string BuildSecretId(string tenantCode, string? dbCode)
    {
        var tenant = (tenantCode ?? "").Trim();
        var code = SanitizeDbCode(dbCode);
        if (tenant.Length == 0 || code.Length == 0)
            return "";
        return $"{SecretIdPrefix}{tenant}/sqlApiCredentials/{code}";
    }

    public static string BuildCredRef(string tenantCode, string? dbCode)
    {
        var id = BuildSecretId(tenantCode, dbCode);
        return id.Length == 0 ? "" : "sm:" + id;
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
        var prefix = $"sm:{SecretIdPrefix}{tenant}/sqlApiCredentials/";
        return (apiCredRef ?? "").Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldAutoAssign(string? apiCredRef, string tenantCode) =>
        IsBlankOrPlaceholder(apiCredRef) || IsOurConvention(apiCredRef, tenantCode);

    [GeneratedRegex(@"[^A-Z0-9._@+=-]")]
    private static partial Regex CodeSanitizeRegex();
}
