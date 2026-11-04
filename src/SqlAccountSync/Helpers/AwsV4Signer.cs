using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Minimal AWS Signature V4 signer for AWS API Gateway (execute-api) requests.
/// Ported from ApprovalPO — the proven SQL Accounting API client path.
/// </summary>
public static class AwsV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    public static IReadOnlyDictionary<string, string> SignedHeaders(
        string method,
        Uri uri,
        byte[] body,
        string? contentType,
        string accessKey,
        string secretKey,
        string region,
        string service,
        DateTime? utcNow = null)
    {
        var now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var payloadHash = ToHex(Sha256(body ?? Array.Empty<byte>()));
        var hostHeader = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = hostHeader,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (!string.IsNullOrWhiteSpace(contentType))
            headers["content-type"] = contentType.Trim();

        var signedHeaderNames = string.Join(";", headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(kv => $"{kv.Key}:{kv.Value.Trim()}\n"));

        var canonicalRequest = string.Join("\n",
            method.ToUpperInvariant(),
            CanonicalUri(uri.AbsolutePath),
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaderNames,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n",
            Algorithm,
            amzDate,
            credentialScope,
            ToHex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = SigningKey(secretKey, dateStamp, region, service);
        var signature = ToHex(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"{Algorithm} Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaderNames}, Signature={signature}",
            ["x-amz-date"] = amzDate,
            ["x-amz-content-sha256"] = payloadHash,
        };
    }

    private static string CanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = UriEncode(segments[i], encodeSlash: false);
        var joined = string.Join("/", segments);
        return joined.Length == 0 ? "/" : joined;
    }

    private static string CanonicalQuery(Uri uri)
    {
        var q = uri.Query;
        if (string.IsNullOrEmpty(q) || q == "?")
            return string.Empty;

        var pairs = q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new List<(string Key, string Value)>(pairs.Length);
        foreach (var p in pairs)
        {
            var eq = p.IndexOf('=');
            var key = eq < 0 ? p : p[..eq];
            var val = eq < 0 ? "" : p[(eq + 1)..];
            parsed.Add((UriEncode(Uri.UnescapeDataString(key), true), UriEncode(Uri.UnescapeDataString(val), true)));
        }
        parsed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key) is var c && c != 0 ? c : string.CompareOrdinal(a.Value, b.Value));
        return string.Join("&", parsed.Select(p => $"{p.Key}={p.Value}"));
    }

    private static string UriEncode(string value, bool encodeSlash)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c is '-' or '_' or '.' or '~')
            {
                sb.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    private static byte[] SigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static byte[] Sha256(byte[] data)
    {
        return SHA256.HashData(data);
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        return HMACSHA256.HashData(key, data);
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
