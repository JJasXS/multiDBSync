using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Helpers;

const string baseUrl = "https://api.sql.my";
const string secretId = "proacc/tenant/TNT10006/sqlApiCredentials/S2";
const string testCode = "SYNC-TEST-001";

var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
var secretResp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
using var secretDoc = JsonDocument.Parse(secretResp.SecretString ?? "{}");
var accessKey = secretDoc.RootElement.GetProperty("accessKey").GetString()!;
var secretKey = secretDoc.RootElement.GetProperty("secretKey").GetString()!;

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

async Task<(int Status, string Body)> SendAsync(string method, string path, string? json = null)
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

Console.WriteLine("=== S2 API tests ===");

foreach (var path in new[] { "/supplier?offset=0&limit=3", "/stockitem?offset=0&limit=3", "/stockgroup?offset=0&limit=20" })
{
    var r = await SendAsync("GET", path);
    Console.WriteLine($"GET {path} => {r.Status}");
    Console.WriteLine(Short(r.Body));
}

foreach (var path in new[] { "/uom?offset=0&limit=20" })
{
    var r = await SendAsync("GET", path);
    Console.WriteLine($"GET {path} => {r.Status}");
    Console.WriteLine(Short(r.Body));
}

var itemPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["dockey"] = 0,
    ["dirty"] = true,
    ["lastmodified"] = 0,
    ["code"] = testCode,
    ["description"] = "Sync API test item",
    ["stockgroup"] = "DEFAULT",
    ["stockcontrol"] = true,
    ["costingmethod"] = 1,
    ["serialnumber"] = false,
    ["minqty"] = "0",
    ["maxqty"] = "0",
    ["suom"] = "UNIT",
    ["reorderlevel"] = "0",
    ["reorderqty"] = "1",
    ["irbm_classification"] = "022",
    ["isactive"] = true,
    ["creationdate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    ["uom"] = "UNIT",
    ["refcost"] = "0",
    ["refprice"] = "0",
    ["itemtype"] = "-",
    ["leadtime"] = 0,
    ["bom_leadtime"] = 0,
    ["bom_asmcost"] = "0",
    ["balsqty"] = "0",
    ["balsuomqty"] = "0",
    ["sdsuom"] = new[]
    {
        new Dictionary<string, object?>
        {
            ["code"] = testCode,
            ["uom"] = "UNIT",
            ["rate"] = "1",
            ["refcost"] = "0",
            ["refprice"] = "0",
            ["mincost"] = "0",
            ["maxcost"] = "0",
            ["minprice"] = "0",
            ["maxprice"] = "0",
            ["isbase"] = true
        }
    },
    ["sdsopeningbalance"] = Array.Empty<object>(),
    ["sdscustomerprice"] = Array.Empty<object>(),
    ["sdssupplierprice"] = Array.Empty<object>(),
    ["sdsbom"] = Array.Empty<object>(),
    ["sdsaltstockitem"] = Array.Empty<object>(),
    ["sdscustomeritem"] = Array.Empty<object>(),
    ["sdssupplieritem"] = Array.Empty<object>(),
    ["sdsbarcode"] = Array.Empty<object>()
}, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

var dogsDetail = await SendAsync("GET", "/stockitem/1");
Console.WriteLine($"GET /stockitem/1 (existing item) => {dogsDetail.Status}");
Console.WriteLine(Short(dogsDetail.Body));

// Clone existing item shape with new code
if (dogsDetail.Status == 200)
{
    using var doc = JsonDocument.Parse(dogsDetail.Body);
    var root = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var dataArr)
        ? dataArr[0]
        : doc.RootElement;
    var cloneCode = "SYNC-CLONE-001";
    var clone = new Dictionary<string, object?>
    {
        ["dockey"] = 0,
        ["dirty"] = true,
        ["lastmodified"] = 0,
        ["code"] = cloneCode,
        ["description"] = "Cloned from existing S2 item",
        ["stockgroup"] = root.GetProperty("stockgroup").GetString(),
        ["stockcontrol"] = root.GetProperty("stockcontrol").GetBoolean(),
        ["costingmethod"] = root.GetProperty("costingmethod").GetInt32(),
        ["serialnumber"] = root.TryGetProperty("serialnumber", out var sn) && sn.GetBoolean(),
        ["minqty"] = "0",
        ["maxqty"] = "0",
        ["suom"] = root.TryGetProperty("suom", out var su) && su.ValueKind != JsonValueKind.Null ? su.GetString() : "UNIT",
        ["reorderlevel"] = "0",
        ["reorderqty"] = "1",
        ["irbm_classification"] = root.GetProperty("irbm_classification").GetString(),
        ["isactive"] = true,
        ["creationdate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        ["uom"] = "UNIT",
        ["refcost"] = "0",
        ["refprice"] = "0",
        ["itemtype"] = "-",
        ["leadtime"] = 0,
        ["bom_leadtime"] = 0,
        ["bom_asmcost"] = "0",
        ["balsqty"] = "0",
        ["balsuomqty"] = "0",
        ["sdsuom"] = new[] { new Dictionary<string, object?> { ["code"] = cloneCode, ["uom"] = "UNIT", ["rate"] = "1", ["refcost"] = "0", ["refprice"] = "0", ["mincost"] = "0", ["maxcost"] = "0", ["minprice"] = "0", ["maxprice"] = "0", ["isbase"] = true } },
        ["sdsopeningbalance"] = Array.Empty<object>(),
        ["sdscustomerprice"] = Array.Empty<object>(),
        ["sdssupplierprice"] = Array.Empty<object>(),
        ["sdsbom"] = Array.Empty<object>(),
        ["sdsaltstockitem"] = Array.Empty<object>(),
        ["sdscustomeritem"] = Array.Empty<object>(),
        ["sdssupplieritem"] = Array.Empty<object>(),
        ["sdsbarcode"] = Array.Empty<object>()
    };
    var cloneJson = JsonSerializer.Serialize(clone);
    var clonePost = await SendAsync("POST", "/stockitem", cloneJson);
    Console.WriteLine($"POST /stockitem clone ({cloneCode}) => {clonePost.Status}");
    Console.WriteLine(Short(clonePost.Body));
}

var postItem = await SendAsync("POST", "/stockitem", itemPayload);
Console.WriteLine($"POST /stockitem ({testCode}) => {postItem.Status}");
Console.WriteLine(Short(postItem.Body));

var postGroup = await SendAsync("POST", "/stockgroup", JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["code"] = "DEFAULT",
    ["description"] = "DEFAULT",
    ["sales"] = "",
    ["salesreturned"] = "",
    ["cashsales"] = "",
    ["purchase"] = "",
    ["cashpurchase"] = "",
    ["purchasereturned"] = "",
    ["balancestock"] = "",
    ["costingmethod"] = 1,
    ["isactive"] = true,
    ["lastmodified"] = 0
}));
Console.WriteLine($"POST /stockgroup (DEFAULT) => {postGroup.Status}");
Console.WriteLine(Short(postGroup.Body));

var postItem2 = await SendAsync("POST", "/stockitem", itemPayload.Replace(testCode, testCode + "-B", StringComparison.Ordinal));
Console.WriteLine($"POST /stockitem retry after stockgroup => {postItem2.Status}");
Console.WriteLine(Short(postItem2.Body));

static string Short(string body)
{
    if (body.Contains("Unsupported Browser", StringComparison.OrdinalIgnoreCase))
        return "Unsupported Browser (missing/invalid User-Agent)";
    if (body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
        return body.Length <= 500 ? body : body[..500] + "...";
    var titleStart = body.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
    if (titleStart >= 0)
    {
        titleStart += 7;
        var titleEnd = body.IndexOf("</title>", titleStart, StringComparison.OrdinalIgnoreCase);
        if (titleEnd > titleStart)
            return "HTML: " + body[titleStart..titleEnd];
    }
    return body.Length <= 500 ? body : body[..500] + "...";
}
