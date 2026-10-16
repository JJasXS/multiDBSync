using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
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
    private readonly ISqlApiCredentialBundle _bundle;
    private readonly SyncOptions _sync;
    private readonly IAmazonSecretsManager? _aws;
    private readonly ILogger<SqlAccountApiWriteAdapter> _logger;

    public SqlAccountApiWriteAdapter(
        HttpClient http,
        ISecretResolver secrets,
        ISqlApiCredentialBundle bundle,
        IOptions<SyncOptions> sync,
        ILogger<SqlAccountApiWriteAdapter> logger,
        IAmazonSecretsManager? aws = null)
    {
        _http = http;
        _secrets = secrets;
        _bundle = bundle;
        _sync = sync.Value;
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
            EntityCodes.Account => "/account",
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
        else if (string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var row = GlAccountMapper.FromMasterRecord(record);
            var destParent = 0;
            if (record.Fields.TryGetValue(GlAccountMapper.FieldDestParent, out var dp)
                && int.TryParse(dp, out var parsedParent))
                destParent = parsedParent;
            var dict = GlAccountMapper.ToApiPayload(row, destParent);
            payload = GlAccountMapper.SerializePayload(dict);
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

    public Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        var code = (record.Code ?? "").Trim();
        if (code.Length == 0)
            return Task.FromResult(new WriteResult { Success = false, Message = "Code is required for update." });

        if (PartnerSyncRules.IsPartyEntity(targetEntityCode))
        {
            var pathPrefix = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
                ? "/customer/" : "/supplier/";
            var sourceEntity = string.IsNullOrWhiteSpace(sourceEntityCode) ? targetEntityCode : sourceEntityCode!;
            var source = sourceDb ?? db;
            var partner = BusinessPartnerMapper.FromMasterRecord(record, source, sourceEntity);
            var dict = BusinessPartnerMapper.ToApiPayload(partner, targetEntityCode, code, txnStamp);
            var payload = BusinessPartnerMapper.SerializePayload(dict);
            return SendAsync(db, HttpMethod.Put, pathPrefix + Uri.EscapeDataString(code), payload,
                "Updated.", treatNotFoundAsSuccess: false, cancellationToken);
        }

        if (!string.Equals(targetEntityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new WriteResult { Success = false, Message = $"Update is not supported for {targetEntityCode}." });

        var row = GlAccountMapper.FromMasterRecord(record);
        var dictAcc = GlAccountMapper.ToUpdatePayload(row, destParentDocKey, destDocKey);
        var payloadAcc = GlAccountMapper.SerializePayload(dictAcc);
        return SendAsync(db, HttpMethod.Put, "/account/" + Uri.EscapeDataString(code), payloadAcc,
            "Updated.", treatNotFoundAsSuccess: false, cancellationToken);
    }

    public Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default)
    {
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return Task.FromResult(new WriteResult { Success = false, Message = "Code is required for delete." });

        var path = targetEntityCode.ToUpperInvariant() switch
        {
            EntityCodes.Customer => "/customer/" + Uri.EscapeDataString(key),
            EntityCodes.Supplier => "/supplier/" + Uri.EscapeDataString(key),
            EntityCodes.Account => "/account/" + Uri.EscapeDataString(key),
            _ => ""
        };
        if (path.Length == 0)
            return Task.FromResult(new WriteResult { Success = false, Message = $"Delete is not supported for {targetEntityCode}." });

        return SendAsync(db, HttpMethod.Delete, path, null, "Deleted.", treatNotFoundAsSuccess: true, cancellationToken);
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(apiPath) ? "/purchaseinvoice" : apiPath;
        string payload;
        if (path.Contains("stocktransfer", StringComparison.OrdinalIgnoreCase))
        {
            var dict = StockTransferMapper.ToApiPayload(document, txnStamp);
            payload = StockTransferMapper.SerializePayload(dict);
        }
        else if (path.Contains("salesdebitnote", StringComparison.OrdinalIgnoreCase))
        {
            var dict = SalesNoteMapper.ToDebitNotePayload(document, txnStamp);
            payload = SalesNoteMapper.SerializePayload(dict);
        }
        else if (path.Contains("salescreditnote", StringComparison.OrdinalIgnoreCase))
        {
            var dict = SalesNoteMapper.ToCreditNotePayload(document, txnStamp);
            payload = SalesNoteMapper.SerializePayload(dict);
        }
        else if (path.Contains("purchasereturned", StringComparison.OrdinalIgnoreCase))
        {
            var dict = PurchaseReturnMapper.ToApiPayload(document, txnStamp);
            payload = PurchaseReturnMapper.SerializePayload(dict);
        }
        else if (path.Contains("goodsreceived", StringComparison.OrdinalIgnoreCase))
        {
            var dict = GoodsReceivedMapper.ToApiPayload(document, txnStamp);
            payload = GoodsReceivedMapper.SerializePayload(dict);
        }
        else if (path.Contains("purchaseinvoice", StringComparison.OrdinalIgnoreCase)
                 || path.Contains("purchase", StringComparison.OrdinalIgnoreCase))
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

    private Task<WriteResult> PostAsync(RegisteredDatabase db, string path, string json, CancellationToken cancellationToken) =>
        SendAsync(db, HttpMethod.Post, path, json, "Created.", treatNotFoundAsSuccess: false, cancellationToken);

    private async Task<WriteResult> SendAsync(
        RegisteredDatabase db, HttpMethod method, string path, string? json,
        string successMessage, bool treatNotFoundAsSuccess, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return new WriteResult { Success = false, Message = "SQL Accounting API is not configured for this database." };

        var creds = await ResolveCredsAsync(db, cancellationToken).ConfigureAwait(false);
        if (creds is null)
            return new WriteResult { Success = false, Message = "SQL Accounting API credentials could not be resolved." };

        var relative = path.StartsWith('/') ? path : "/" + path;
        var uri = new Uri(db.ApiBaseUrl.TrimEnd('/') + relative);
        var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var contentType = json is null ? null : "application/json";
        var signed = AwsV4Signer.SignedHeaders(method.Method, uri, bodyBytes, contentType,
            creds.AccessKey, creds.SecretKey, "ap-southeast-1", "execute-api");

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
            if (!response.IsSuccessStatusCode)
            {
                if (treatNotFoundAsSuccess && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new WriteResult { Success = true, Message = "Already absent." };

                TryDumpFailedPost(relative, json ?? "", (int)response.StatusCode, body);
                var detail = SummarizeApiError(body);
                _logger.LogWarning("SQL API {Method} {Path} returned {Status}: {Body}",
                    method.Method, relative, (int)response.StatusCode, detail);
                return new WriteResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(detail)
                        ? $"SQL Accounting API returned {(int)response.StatusCode}."
                        : EnhanceApiErrorMessage(detail, relative)
                };
            }

            var (docKey, docNo) = ParseCreated(body);
            return new WriteResult { Success = true, DocKey = docKey, DocNo = docNo, Message = successMessage };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var seconds = (int)Math.Max(1, _http.Timeout.TotalSeconds);
            _logger.LogWarning("SQL API {Method} {Path} timed out after {Seconds}s",
                method.Method, relative, seconds);
            return new WriteResult
            {
                Success = false,
                Message = $"Request timed out after {seconds}s."
            };
        }
    }

    private static string EnhanceApiErrorMessage(string detail, string path)
    {
        var msg = $"SQL Accounting API returned 500: {detail}";
        if (!detail.Contains("No corresponding master", StringComparison.OrdinalIgnoreCase))
            return msg;

        var hint = path.Contains("stockitem", StringComparison.OrdinalIgnoreCase)
            ? " A prerequisite master may be missing on the destination API (stock group, UOM, or category). SqlAccountSync attempts to create stock group via API before item push; if UOM API is unavailable on this tenant, contact eStream support."
            : " A related master record is missing on the destination database. Create it on the sub in SQL Account first, then retry.";
        return msg + hint;
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
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        Actions.Add($"WOULD PUT {targetEntityCode} {record.Code} on {db.DisplayName} (dockey={destDocKey})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD DELETE {targetEntityCode} {code} on {db.DisplayName}");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified." });
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD POST {apiPath} as {document.DocType} on {db.DisplayName} ({document.Lines.Count} lines, {document.Amount:N2})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified." });
    }
}
