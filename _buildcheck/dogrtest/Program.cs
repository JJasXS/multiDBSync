using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Helpers;

const string baseUrl = "https://api.sql.my";
var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
var subs = new[]
{
    ("S1", "proacc/tenant/TNT10006/sqlApiCredentials/S1"),
    ("S2", "proacc/tenant/TNT10006/sqlApiCredentials/S2")
};

using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

var results = new List<string>();
results.Add("# DO → GR API smoke test");
results.Add($"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
results.Add("");

foreach (var (label, secretId) in subs)
{
    results.Add($"## {label}");
    var creds = await LoadCredsAsync(sm, secretId);
    if (creds is null)
    {
        results.Add("- Credentials: FAILED");
        results.Add("");
        continue;
    }

    var (accessKey, secretKey) = creds.Value;
    var grList = await SendAsync(http, accessKey, secretKey, region, "GET", "/goodsreceived?offset=0&limit=3");
    results.Add($"- GET /goodsreceived => {grList.Status}");
    results.Add($"  {Short(grList.Body)}");

    var supplier = await FirstListCodeAsync(http, accessKey, secretKey, region, "/supplier?offset=0&limit=20", "code");
    var item = await FirstListCodeAsync(http, accessKey, secretKey, region, "/stockitem?offset=0&limit=20", "code");
    results.Add($"- First supplier: {supplier ?? "(none)"}");
    results.Add($"- First item: {item ?? "(none)"}");

    if (supplier is null || item is null)
    {
        results.Add("- POST /goodsreceived: SKIPPED (need supplier + item on sub)");
        results.Add("");
        continue;
    }

    var testCode = $"SYNC-GR-{label}-{DateTime.UtcNow:HHmmss}";
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["docdate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        ["postdate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        ["taxdate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        ["code"] = supplier,
        ["companyname"] = supplier,
        ["country"] = "MY",
        ["area"] = "----",
        ["agent"] = "----",
        ["project"] = "----",
        ["terms"] = "30 Days",
        ["currencycode"] = "----",
        ["currencyrate"] = "1",
        ["shipper"] = "----",
        ["description"] = testCode,
        ["cancelled"] = false,
        ["status"] = 0,
        ["docamt"] = "0",
        ["localdocamt"] = "0",
        ["landingcost1"] = "0",
        ["landingcost2"] = "0",
        ["localtotalwithcost"] = "0",
        ["d_amount"] = "0",
        ["branchname"] = "BILLING",
        ["dcountry"] = "MY",
        ["sic"] = "00000",
        ["submissiontype"] = 0,
        ["note"] = $"SYNC|{testCode}",
        ["transferable"] = true,
        ["printcount"] = 0,
        ["changed"] = true,
        ["sdsdocdetail"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["seq"] = 1000,
                ["itemcode"] = item,
                ["location"] = "----",
                ["project"] = "----",
                ["description"] = testCode,
                ["receiveqty"] = "1",
                ["returnqty"] = "0",
                ["qty"] = "1",
                ["uom"] = "UNIT",
                ["rate"] = "1",
                ["sqty"] = "1",
                ["suomqty"] = "1",
                ["unitprice"] = "0",
                ["taxinclusive"] = false,
                ["amount"] = "0",
                ["localamount"] = "0",
                ["landingcost1"] = "0",
                ["landingcost2"] = "0",
                ["amountwithtax"] = "0",
                ["printable"] = true,
                ["transferable"] = true,
                ["changed"] = true,
                ["sdsserialnumber"] = Array.Empty<object>()
            }
        }
    });

    var post = await SendAsync(http, accessKey, secretKey, region, "POST", "/goodsreceived", payload);
    results.Add($"- POST /goodsreceived ({testCode}) => {post.Status}");
    results.Add($"  {Short(post.Body)}");
    results.Add("");
}

var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "dogrtest-results.md"));
await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, results));
Console.WriteLine(string.Join(Environment.NewLine, results));
Console.WriteLine($"Wrote {outPath}");

static async Task<(string AccessKey, string SecretKey)?> LoadCredsAsync(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        var ak = doc.RootElement.GetProperty("accessKey").GetString();
        var sk = doc.RootElement.GetProperty("secretKey").GetString();
        return ak is null || sk is null ? null : (ak, sk);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Credential load failed for {secretId}: {ex.Message}");
        return null;
    }
}

static async Task<(int Status, string Body)> SendAsync(
    HttpClient http, string accessKey, string secretKey, string region,
    string method, string path, string? json = null)
{
    var uri = new Uri(baseUrl.TrimEnd('/') + path);
    var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
    var contentType = json is null ? null : "application/json";
    var signed = AwsV4Signer.SignedHeaders(method, uri, bodyBytes, contentType,
        accessKey, secretKey, region, "execute-api");

    using var request = new HttpRequestMessage(new HttpMethod(method), uri);
    foreach (var (k, v) in signed)
        request.Headers.TryAddWithoutValidation(k, v);
    if (json is not null)
    {
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    return ((int)response.StatusCode, body);
}

static async Task<string?> FirstListCodeAsync(
    HttpClient http, string accessKey, string secretKey, string region, string path, string field)
{
    var r = await SendAsync(http, accessKey, secretKey, region, "GET", path);
    if (r.Status != 200 || !r.Body.TrimStart().StartsWith('{'))
        return null;
    try
    {
        using var doc = JsonDocument.Parse(r.Body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        if (data.GetArrayLength() == 0)
            return null;
        var row = data[0];
        return row.TryGetProperty(field, out var c) ? c.GetString()?.Trim() : null;
    }
    catch
    {
        return null;
    }
}

static string Short(string body)
{
    if (body.Contains("Unsupported Browser", StringComparison.OrdinalIgnoreCase))
        return "Unsupported Browser";
    return body.Length <= 600 ? body : body[..600] + "...";
}
