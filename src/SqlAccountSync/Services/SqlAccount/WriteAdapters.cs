using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
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

    public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string entityCode, MasterRecord record, string txnStamp, CancellationToken cancellationToken = default)
    {
        var path = entityCode.ToUpperInvariant() switch
        {
            EntityCodes.Item => "/stockitem",
            EntityCodes.Customer => "/customer",
            EntityCodes.Supplier => "/supplier",
            _ => ""
        };
        if (path.Length == 0)
            return Task.FromResult(new WriteResult { Success = false, Message = $"No API path configured for {entityCode}." });

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["code"] = record.Code,
            ["description"] = record.Description,
            ["udf_txnid"] = txnStamp
        });
        return PostAsync(db, path, payload, cancellationToken);
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(apiPath) ? "/purchaseinvoice" : apiPath;
        var payload = JsonSerializer.Serialize(new
        {
            docNo = document.DocNo,
            docDate = document.DocDate,
            code = document.PartyCode,
            companyName = document.PartyName,
            docAmt = document.Amount,
            currency = document.Currency,
            udf_txnid = txnStamp,
            lines = document.Lines.Select(l => new
            {
                itemCode = l.ItemCode,
                description = l.Description,
                qty = l.Qty,
                unitPrice = l.UnitPrice,
                amount = l.Amount,
                taxCode = l.TaxCode,
                location = l.Warehouse
            })
        });
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
            _logger.LogWarning("SQL API {Path} returned {Status}", relative, (int)response.StatusCode);
            return new WriteResult { Success = false, Message = $"SQL Accounting API returned {(int)response.StatusCode}." };
        }

        var (docKey, docNo) = ParseCreated(body);
        return new WriteResult { Success = true, DocKey = docKey, DocNo = docNo, Message = "Created." };
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

        if (_aws is not null && db.ApiCredRef.StartsWith("sm:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var resp = await _aws.GetSecretValueAsync(new GetSecretValueRequest { SecretId = db.ApiCredRef[3..].Trim() }, cancellationToken);
                return ParseCreds(resp.SecretString);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQL API credential resolve failed.");
            }
        }
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
            if (obj.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        return null;
    }

    private static (string DocKey, string DocNo) ParseCreated(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            string? Get(params string[] names)
            {
                foreach (var n in names)
                {
                    if (root.TryGetProperty(n, out var p))
                        return p.ToString();
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.TryGetProperty(n, out var q))
                        return q.ToString();
                }
                return null;
            }
            return (Get("docKey", "dockey", "id") ?? "", Get("docNo", "docno") ?? "");
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

    public Task<WriteResult> CreateMasterAsync(RegisteredDatabase db, string entityCode, MasterRecord record, string txnStamp, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD create {entityCode} {record.Code} on {db.DisplayName}");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run — no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> CreateDocumentAsync(RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD POST {apiPath} as {document.DocType} on {db.DisplayName} ({document.Lines.Count} lines, {document.Amount:N2})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run — no data was modified." });
    }
}
