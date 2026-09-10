using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using DotNetEnv;
using SqlAccountSync.Helpers;

var root = FindRepoRoot();
foreach (var p in new[] { Path.Combine(root, "src", "SqlAccountSync", ".env"), Path.Combine(root, ".env") })
{
    if (File.Exists(p)) { Env.Load(p); break; }
}

const string fromLoc = "SYNC-FROM";
const string toLoc = "SYNC-TO";
const string item = "Dogs";
const string qty = "10";
var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";

using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

var creds = await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials")
    ?? await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials/HQ");
if (creds is null)
{
    Console.WriteLine("FAIL: HQ SQL API credentials missing");
    return;
}
var (ak, sk) = creds.Value;

await EnsureLocation(http, ak, sk, region, fromLoc);
await EnsureLocation(http, ak, sk, region, toLoc);

for (var i = 1; i <= 2; i++)
{
    var tag = $"SYNC-ST10-{DateTime.UtcNow:HHmmss}-{i}";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["docdate"] = today,
        ["postdate"] = today,
        ["currencyrate"] = "1",
        ["description"] = tag,
        ["area"] = "----",
        ["agent"] = "----",
        ["docamt"] = "0",
        ["localdocamt"] = "0",
        ["cancelled"] = false,
        ["status"] = 0,
        ["note"] = tag,
        ["printcount"] = 0,
        ["changed"] = true,
        ["sdsdocdetail"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["seq"] = 1000,
                ["itemcode"] = item,
                ["fromlocation"] = fromLoc,
                ["tolocation"] = toLoc,
                ["project"] = "----",
                ["description"] = tag,
                ["uom"] = "UNIT",
                ["qty"] = qty,
                ["sqty"] = qty,
                ["suomqty"] = "0",
                ["rate"] = "1",
                ["unitprice"] = "0",
                ["taxinclusive"] = false,
                ["amount"] = "0",
                ["localamount"] = "0",
                ["unitcost"] = "0",
                ["printable"] = true,
                ["changed"] = true
            }
        }
    });

    var post = await Send(http, ak, sk, region, "POST", "/stocktransfer", payload);
    Console.WriteLine($"POST /stocktransfer #{i} => {post.Status}");
    Console.WriteLine(Short(post.Body));
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "SqlAccountSync", "SqlAccountSync.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static async Task<(string, string)?> LoadCreds(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        return (doc.RootElement.GetProperty("accessKey").GetString()!, doc.RootElement.GetProperty("secretKey").GetString()!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Secret {secretId}: {ex.Message}");
        return null;
    }
}

static async Task EnsureLocation(HttpClient http, string ak, string sk, string region, string code)
{
    var list = await Send(http, ak, sk, region, "GET", "/location?$filter=" + Uri.EscapeDataString($"code eq '{code}'"));
    if (list.Status is >= 200 and < 300 && list.Body.Contains(code, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Location {code}: exists");
        return;
    }
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var body = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code,
        ["description"] = code,
        ["docdate"] = today,
        ["changed"] = true
    });
    var post = await Send(http, ak, sk, region, "POST", "/location", body);
    Console.WriteLine($"Location {code}: POST => {post.Status} {Short(post.Body)}");
}

static async Task<(int Status, string Body)> Send(HttpClient http, string ak, string sk, string region, string method, string path, string? json = null)
{
    const string baseUrl = "https://api.sql.my";
    var uri = new Uri(baseUrl.TrimEnd('/') + path);
    var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
    var signed = AwsV4Signer.SignedHeaders(method, uri, bodyBytes, json is null ? null : "application/json", ak, sk, region, "execute-api");
    using var request = new HttpRequestMessage(new HttpMethod(method), uri);
    foreach (var (k, v) in signed) request.Headers.TryAddWithoutValidation(k, v);
    if (json is not null)
        request.Content = new ByteArrayContent(bodyBytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } };
    using var resp = await http.SendAsync(request);
    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
}

static string Short(string s) => s.Length <= 400 ? s : s[..400] + "…";
