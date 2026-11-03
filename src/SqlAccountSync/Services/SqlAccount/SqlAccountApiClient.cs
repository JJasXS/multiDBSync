using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.SqlAccount;

public interface ISqlAccountApiClient
{
    Task<SqlApiResponse> GetAsync(RegisteredDatabase db, string path, CancellationToken cancellationToken = default);
    Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken = default);
    Task<bool> ExistsInListAsync(RegisteredDatabase db, string listPath, string codeField, string code, CancellationToken cancellationToken = default);
}

public sealed class SqlAccountApiClient : ISqlAccountApiClient
{
    private readonly HttpClient _http;
    private readonly ISecretResolver _secrets;
    private readonly ISqlApiCredentialBundle _bundle;
    private readonly SyncOptions _sync;
    private readonly SqlAccountingWriteOptions _writeOptions;
    private readonly IAmazonSecretsManager? _aws;
    private readonly ILogger<SqlAccountApiClient> _logger;
    private const string Region = "ap-southeast-1";

    public SqlAccountApiClient(
        HttpClient http,
        ISecretResolver secrets,
        ISqlApiCredentialBundle bundle,
        IOptions<SyncOptions> sync,
        ILogger<SqlAccountApiClient> logger,
        IOptions<SqlAccountingWriteOptions>? writeOptions = null,
        IAmazonSecretsManager? aws = null)
    {
        _http = http;
        _secrets = secrets;
        _bundle = bundle;
        _sync = sync.Value;
        _writeOptions = writeOptions?.Value ?? new SqlAccountingWriteOptions();
        _logger = logger;
        _aws = aws;
    }

    public Task<SqlApiResponse> GetAsync(RegisteredDatabase db, string path, CancellationToken cancellationToken = default) =>
        SendAsync(db, HttpMethod.Get, path, null, cancellationToken);

    public async Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(db, HttpMethod.Post, path, json, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            var detail = SummarizeApiError(response.Body);
            return new WriteResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(detail)
                    ? $"SQL Accounting API returned {response.Status}."
                    : $"SQL Accounting API returned {response.Status}: {detail}"
            };
        }

        var (docKey, docNo) = ParseCreated(response.Body);
        return new WriteResult { Success = true, DocKey = docKey, DocNo = docNo, Message = "Created." };
    }

    public async Task<bool> ExistsInListAsync(
        RegisteredDatabase db, string listPath, string codeField, string code, CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return false;

        var response = await GetAsync(db, listPath, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || !IsJson(response.Body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var row in data.EnumerateArray())
            {
                if (row.TryGetProperty(codeField, out var c)
                    && string.Equals(c.GetString()?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private async Task<SqlApiResponse> SendAsync(
        RegisteredDatabase db, HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        if (_writeOptions.ShouldRefuseRestHttp())
        {
            _logger.LogWarning(
                "Refusing SQL Accounting REST {Method} {Path}: Provider={Provider} BlockRestHttp={Block} (no silent REST fallback).",
                method.Method, path, _writeOptions.Provider, _writeOptions.BlockRestHttp);
            return SqlApiResponse.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return SqlApiResponse.Unavailable;

        var creds = await ResolveCredsAsync(db, cancellationToken).ConfigureAwait(false);
        if (creds is null)
            return SqlApiResponse.Unavailable;

        var relative = path.StartsWith('/') ? path : "/" + path;
        var uri = new Uri(db.ApiBaseUrl.TrimEnd('/') + relative);
        var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var contentType = json is null ? null : "application/json";
        var signed = AwsV4Signer.SignedHeaders(method.Method, uri, bodyBytes, contentType,
            creds.AccessKey, creds.SecretKey, Region, "execute-api");

        using var request = new HttpRequestMessage(method, uri);
        foreach (var (k, v) in signed)
            request.Headers.TryAddWithoutValidation(k, v);

        if (json is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new SqlApiResponse(true, (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL API {Method} {Path} failed for {Db}.", method, relative, db.DbCode);
            return SqlApiResponse.Unavailable;
        }
    }

    internal static bool IsJson(string body)
    {
        var t = (body ?? "").TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    internal static string SummarizeApiError(string? body)
    {
        var raw = (body ?? "").Trim();
        if (raw.Length == 0)
            return "";

        if (raw.StartsWith("<!", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            if (raw.Contains("Unsupported Browser", StringComparison.OrdinalIgnoreCase))
                return "Unsupported Browser (SQL API requires a modern User-Agent header).";
            return "HTML response (endpoint may not be available on this tenant).";
        }

        return raw.Length <= 800 ? raw : raw[..800] + "...";
    }

    private async Task<SqlApiCredentials?> ResolveCredsAsync(RegisteredDatabase db, CancellationToken cancellationToken)
    {
        var bundled = await _bundle.ResolveAsync(_sync.TenantCode, db, cancellationToken).ConfigureAwait(false);
        if (bundled is not null)
            return bundled;

        var raw = await _secrets.ResolveAsync(db.ApiCredRef, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parsed = ParseCreds(raw);
            if (parsed is not null)
                return parsed;
        }

        if (_aws is null)
            return null;

        var secretId = NormalizeSecretsManagerId(db.ApiCredRef);
        if (secretId is null)
            return null;

        try
        {
            var resp = await _aws.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken)
                .ConfigureAwait(false);
            return ParseCreds(resp.SecretString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL API credential resolve failed for {SecretId}.", secretId);
            return null;
        }
    }

    private static string? NormalizeSecretsManagerId(string? apiCredRef)
    {
        var r = (apiCredRef ?? "").Trim();
        if (r.Length == 0)
            return null;
        if (r.StartsWith("sm:", StringComparison.OrdinalIgnoreCase))
            return r[3..].Trim();
        if (r.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (r.Contains('/') && !r.Contains('\\'))
            return r;
        return null;
    }

    private static SqlApiCredentials? ParseCreds(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || !s.StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(s);
            var ak = Find(doc.RootElement, ["accessKey", "access_key", "accessKeyId"]);
            var sk = Find(doc.RootElement, ["secretKey", "secret_key", "secretAccessKey"]);
            if (string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk))
                return null;
            return new SqlApiCredentials(ak, sk);
        }
        catch
        {
            return null;
        }
    }

    private static string? Find(JsonElement obj, string[] names)
    {
        foreach (var n in names)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (!p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
            }
        }
        return null;
    }

    private static (string DocKey, string DocNo) ParseCreated(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string? Get(JsonElement el, params string[] names)
            {
                foreach (var n in names)
                {
                    if (el.TryGetProperty(n, out var p))
                    {
                        if (p.ValueKind == JsonValueKind.String)
                            return p.GetString();
                        return p.ToString();
                    }
                }
                return null;
            }

            string? docNo = Get(root, "docNo", "docno");
            string? docKey = Get(root, "docKey", "dockey", "id");
            string? code = Get(root, "code");
            if ((docNo is null || docKey is null) && root.TryGetProperty("data", out var data))
            {
                JsonElement el = default;
                if (data.ValueKind == JsonValueKind.Object)
                    el = data;
                else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    el = data[0];
                if (el.ValueKind is JsonValueKind.Object)
                {
                    docNo ??= Get(el, "docNo", "docno");
                    docKey ??= Get(el, "docKey", "dockey", "id");
                    code ??= Get(el, "code");
                }
            }

            return (docKey ?? code ?? docNo ?? "", docNo ?? code ?? docKey ?? "");
        }
        catch
        {
            return ("", "");
        }
    }
}
