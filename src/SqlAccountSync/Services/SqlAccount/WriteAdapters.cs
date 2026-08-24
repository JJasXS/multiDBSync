using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Documents;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Partners;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.SqlAccount;

public sealed record SqlApiResponse(bool Available, int Status, string Body)
{
    public bool IsSuccess => Available && Status is >= 200 and < 300;
    public static SqlApiResponse Unavailable => new(false, 0, "");
}

public sealed record SqlApiCredentials(string AccessKey, string SecretKey);

/// <summary>Official SQL Accounting REST API is the only write path.</summary>
public sealed class SqlAccountApiWriteAdapter : ISqlAccountWriteAdapter
{
    private readonly HttpClient _http;
    private readonly ISecretResolver _secrets;
    private readonly IAmazonSecretsManager? _aws;
    private readonly ILogger<SqlAccountApiWriteAdapter> _logger;

    public SqlAccountApiWriteAdapter(
        HttpClient http,
        ISecretResolver secrets,
        ILogger<SqlAccountApiWriteAdapter> logger,
        IAmazonSecretsManager? aws = null)
    {
        _http = http;
        _secrets = secrets;
        _logger = logger;
        _aws = aws;
    }

    public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default)
    {
        var path = targetEntityCode.ToUpperInvariant() switch
        {
            EntityCodes.Item => "/stockitem",
            EntityCodes.Customer => "/customer",
            EntityCodes.Supplier => "/supplier",
            _ => ""
        };
        if (path.Length == 0)
            return Task.FromResult(new WriteResult { Success = false, Message = $"No API path configured for {targetEntityCode}." });

        string payload;
        if (PartnerSyncRules.IsPartyEntity(targetEntityCode) && sourceDb is not null && !string.IsNullOrWhiteSpace(sourceEntityCode))
        {
            var partner = BusinessPartnerMapper.FromMasterRecord(record, sourceDb, sourceEntityCode);
            var dict = BusinessPartnerMapper.ToApiPayload(partner, targetEntityCode, record.Code, txnStamp);
            payload = BusinessPartnerMapper.SerializePayload(dict);
        }
        else if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
        {
            var dict = BusinessPartnerMapper.ToApiPayload(
                new BusinessPartner { CompanyName = record.Description, SourceCode = record.Code, SourceType = targetEntityCode },
                targetEntityCode, record.Code, txnStamp);
            payload = BusinessPartnerMapper.SerializePayload(dict);
        }
        else if (string.Equals(targetEntityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
        {
            var item = StockItemMapper.FromMasterRecord(record);
            var dict = StockItemMapper.ToApiPayload(item, record.Code, txnStamp);
            payload = StockItemMapper.SerializePayload(dict);
        }
        else
        {
            payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["code"] = record.Code,
                ["description"] = record.Description,
                ["udf_txnid"] = txnStamp
            });
        }
        return PostAsync(db, path, payload, cancellationToken);
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(apiPath) ? "/purchaseinvoice" : apiPath;
        var isPurchaseInvoice = path.Contains("purchaseinvoice", StringComparison.OrdinalIgnoreCase)
                                || path.Contains("purchase", StringComparison.OrdinalIgnoreCase);
        string payload;
        if (isPurchaseInvoice)
        {
            var dict = PurchaseInvoiceMapper.ToApiPayload(document, txnStamp);
            payload = PurchaseInvoiceMapper.SerializePayload(dict);
        }
        else
        {
            payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["docno"] = document.DocNo,
                ["docdate"] = document.DocDate?.ToString("yyyy-MM-dd"),
                ["code"] = document.PartyCode,
                ["companyname"] = document.PartyName,
                ["docamt"] = document.Amount,
                ["currencycode"] = document.Currency,
                ["note"] = string.IsNullOrWhiteSpace(txnStamp) ? null : $"SYNC|{txnStamp}",
                ["sdsdocdetail"] = document.Lines.Select(l => new Dictionary<string, object?>
                {
                    ["itemcode"] = l.ItemCode,
                    ["description"] = l.Description,
                    ["qty"] = l.Qty,
                    ["unitprice"] = l.UnitPrice,
                    ["amount"] = l.Amount,
                    ["tax"] = l.TaxCode,
                    ["location"] = l.Warehouse,
                    ["uom"] = l.Uom
                }).ToList()
            }, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        return PostAsync(db, path, payload, cancellationToken);
    }

    private async Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return new WriteResult { Success = false, Message = "SQL Accounting API is not configured for this database." };

        var creds = await ResolveCredsAsync(db, cancellationToken).ConfigureAwait(false);
        if (creds is null)
            return new WriteResult { Success = false, Message = "SQL Accounting API credentials could not be resolved." };

        var relative = path.StartsWith('/') ? path : "/" + path;
        var uri = new Uri(db.ApiBaseUrl.TrimEnd('/') + relative);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var signed = AwsV4Signer.SignedHeaders("POST", uri, bodyBytes, "application/json",
            creds.AccessKey, creds.SecretKey, "ap-southeast-1", "execute-api");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        foreach (var (k, v) in signed)
            request.Headers.TryAddWithoutValidation(k, v);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            TryDumpFailedPost(relative, json, (int)response.StatusCode, body);
            var detail = SummarizeApiError(body);
            _logger.LogWarning("SQL API {Path} returned {Status}: {Body}", relative, (int)response.StatusCode, detail);
            return new WriteResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(detail)
                    ? $"SQL Accounting API returned {(int)response.StatusCode}."
                    : $"SQL Accounting API returned {(int)response.StatusCode}: {detail}"
            };
        }

        var (docKey, docNo) = ParseCreated(body);
        return new WriteResult { Success = true, DocKey = docKey, DocNo = docNo, Message = "Created." };
    }

    private static string SummarizeApiError(string? body)
    {
        var raw = (body ?? "").Trim();
        if (raw.Length == 0)
            return "";

        // SQL Accounting sometimes returns Delphi/HTML error pages instead of JSON.
        if (raw.StartsWith("<!", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            if (raw.Contains("Unsupported Browser", StringComparison.OrdinalIgnoreCase))
                return "Unsupported Browser (SQL API requires a modern User-Agent header).";
            var title = ExtractHtmlTag(raw, "title");
            var h1 = ExtractHtmlTag(raw, "h1");
            var strong = ExtractHtmlTag(raw, "strong");
            var pre = ExtractHtmlTag(raw, "pre");
            var parts = new[] { strong, h1, title, pre }.Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join(" | ", parts);
            if (joined.Length > 0)
                return TruncateBody(joined);
        }

        return TruncateBody(raw);
    }

    private static string ExtractHtmlTag(string html, string tag)
    {
        var open = "<" + tag;
        var i = html.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var gt = html.IndexOf('>', i);
        if (gt < 0) return "";
        var close = "</" + tag + ">";
        var j = html.IndexOf(close, gt + 1, StringComparison.OrdinalIgnoreCase);
        if (j < 0) return "";
        var inner = html[(gt + 1)..j];
        return System.Text.RegularExpressions.Regex.Replace(inner, "<[^>]+>", " ")
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static void TryDumpFailedPost(string path, string requestJson, int status, string responseBody)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            File.WriteAllText(Path.Combine(dir, "_last_api_request.json"), requestJson);
            File.WriteAllText(Path.Combine(dir, "_last_api_response.txt"),
                $"PATH {path}\nSTATUS {status}\n\n{responseBody}");
        }
        catch
        {
            // best-effort diagnostics only
        }
    }

    private static string TruncateBody(string? body)
    {
        var s = CredentialRedactor.Redact((body ?? "").Trim().Replace('\r', ' ').Replace('\n', ' '));
        if (s.Length <= 800)
            return s;
        return s[..800] + "...";
    }

    private async Task<SqlApiCredentials?> ResolveCredsAsync(RegisteredDatabase db, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Accepts sm:secret-id or a bare Secrets Manager id (e.g. proacc/tenant/.../S2).
    /// Bare env-style names without '/' are not treated as SM ids.
    /// </summary>
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
        // Bare path that looks like an SM secret id (contains a slash).
        if (r.Contains('/') && !r.Contains('\\'))
            return r;
        return null;
    }

    private static SqlApiCredentials? ParseCreds(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0)
            return null;
        if (!s.StartsWith('{'))
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

public sealed class DryRunWriteAdapter : ISqlAccountWriteAdapter
{
    public List<string> Actions { get; } = [];

    public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp, RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null, CancellationToken cancellationToken = default)
    {
        var flip = sourceEntityCode is not null
            && !string.Equals(sourceEntityCode, targetEntityCode, StringComparison.OrdinalIgnoreCase);
        var label = flip ? $"{sourceEntityCode}→{targetEntityCode}" : targetEntityCode;
        Actions.Add($"WOULD create {label} {record.Code} on {db.DisplayName}");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run — no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD POST {apiPath} as {document.DocType} on {db.DisplayName} ({document.Lines.Count} lines, {document.Amount:N2})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run — no data was modified." });
    }
}
