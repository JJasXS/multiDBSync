using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlAccountSync.Helpers;

public static class IdempotencyKey
{
    public static string ForTransaction(
        string srcDbCode, string dstDbCode, string txnType,
        string srcDocType, string srcDocKey, string srcDocNo)
    {
        return Hash(Join(srcDbCode, dstDbCode, txnType, srcDocType, srcDocKey, srcDocNo));
    }

    public static string ForMasterData(
        string sourceDbCode, string destDbCode, string sourceEntity, string destEntity,
        string masterCode, string contentHash)
    {
        return Hash(Join(sourceDbCode, destDbCode, sourceEntity, destEntity, masterCode, contentHash));
    }

    public static string ContentHash(string payload) => Hash(payload ?? "");

    public static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string Join(params string[] parts) =>
        string.Join('|', parts.Select(p => (p ?? "").Trim().ToUpperInvariant()));
}

public static class SqlGuard
{
    private static readonly Regex Leading = new(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void EnsureSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("Empty SQL is not allowed.");
        if (sql.Contains(';'))
            throw new InvalidOperationException("Compound SQL is not allowed.");
        if (!Leading.IsMatch(sql))
            throw new InvalidOperationException("Only SELECT/WITH statements are allowed against SQL Account databases.");
        var upper = sql.ToUpperInvariant();
        foreach (var banned in new[] { " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " DROP ", " ALTER ", " CREATE ", " EXECUTE ", " GRANT ", " REVOKE " })
        {
            if (upper.Contains(banned))
                throw new InvalidOperationException("Mutating SQL is not allowed against SQL Account databases.");
        }
    }
}

public static class CredentialRedactor
{
    private static readonly Regex PasswordInConn = new(
        @"(Password|Pwd)\s*=\s*[^;]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var s = PasswordInConn.Replace(text, "$1=***");
        s = Regex.Replace(s, @"(secret|password|passwd|apikey|accesskey)\s*[:=]\s*\S+", "$1=***", RegexOptions.IgnoreCase);
        return s;
    }
}

public static class FirebirdErrorMapper
{
    public static string Map(Exception ex)
    {
        var msg = ex.Message ?? "";
        var combined = msg + " " + (ex.InnerException?.Message ?? "");
        if (ex is TimeoutException || combined.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return Models.ConnectionResults.Timeout;
        if (combined.Contains("network", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("unable to complete network", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || ex is System.Net.Sockets.SocketException)
            return Models.ConnectionResults.NetworkError;
        if (combined.Contains("Your user name and password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("335544472")
            || combined.Contains("login", StringComparison.OrdinalIgnoreCase) && combined.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return Models.ConnectionResults.AuthenticationFailed;
        if (combined.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("335544344")
            || combined.Contains("I/O error", StringComparison.OrdinalIgnoreCase))
            return Models.ConnectionResults.DatabaseNotFound;
        if (combined.Contains("335544345") || combined.Contains("lock conflict", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("335544336") || combined.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
            return "LOCK_CONFLICT";
        return Models.ConnectionResults.UnknownError;
    }

    /// <summary>User-facing detail: keep known codes, otherwise the redacted exception text.</summary>
    public static string Describe(Exception ex)
    {
        var code = Map(ex);
        var detail = CredentialRedactor.Redact(ex.Message);
        if (string.IsNullOrWhiteSpace(detail))
            return code;
        if (code is Models.ConnectionResults.UnknownError or "LOCK_CONFLICT")
            return detail;
        return $"{code}: {detail}";
    }
}

public static class RequestId
{
    public const string Header = "X-Request-Id";
    public const string ItemKey = "RequestId";

    public static string Current(HttpContext? http)
    {
        if (http?.Items.TryGetValue(ItemKey, out var v) == true && v is string s && s.Length > 0)
            return s;
        return "";
    }
}
