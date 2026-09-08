using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using DotNetEnv;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Transactions;
using SqlAccountSync.Services.Validation;

const int hq = 6;
const int s1 = 7;
const int s2 = 8;
const string txnType = "DO_TO_GR";
const string customer = "300-C0001";
const string item = "Dogs";
const string grSupplier = "SYNC-GR-SU";

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
foreach (var envPath in new[]
{
    Path.Combine(root, ".env"),
    Path.Combine(root, "src", "SqlAccountSync", ".env"),
    Path.Combine(root, "_buildcheck", ".env")
})
{
    if (File.Exists(envPath)) { Env.Load(envPath); break; }
}

var results = new List<string> { "# DO → GR E2E (HQ → Sub)", $"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", "" };

var doDocNo = await EnsureHqDeliveryOrderAsync(results);
if (doDocNo is null)
{
    await WriteResultsAsync(results);
    return;
}

var host = BuildHost(root);
using var scope = host.Services.CreateScope();
var engine = scope.ServiceProvider.GetRequiredService<ITransactionEngine>();
var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
var mapping = scope.ServiceProvider.GetRequiredService<IMappingService>();

await EnsureSubSupplierAsync("S1", "proacc/tenant/TNT10006/sqlApiCredentials/S1", grSupplier, results);
await EnsureSubSupplierAsync("S2", "proacc/tenant/TNT10006/sqlApiCredentials/S2", grSupplier, results);
await EnsureSupplierMappingAsync(mapping, s1, customer, grSupplier, results);
await EnsureSupplierMappingAsync(mapping, s2, customer, grSupplier, results);
await EnsureItemMappingAsync(mapping, s1, item, "Cats", results);

foreach (var (destId, label) in new[] { (s1, "S1"), (s2, "S2") })
{
    results.Add($"## {label} dry-run ({doDocNo})");
    var dry = await engine.DryRunAsync(hq, destId, txnType, doDocNo);
    foreach (var c in dry.Checks)
        results.Add($"- {(c.Ok ? "OK" : "FAIL")} {c.Name}: {c.Detail}");
    results.Add($"Summary: {dry.Summary}");
    results.Add("");

    if (!dry.WouldSucceed)
        continue;

    results.Add($"## {label} transfer");
    var existing = (await engine.ListAsync()).FirstOrDefault(t =>
        t.TxnTypeCode == txnType && t.SourceDbId == hq && t.DestDbId == destId && t.SrcDocNo == doDocNo);
    int txnPk;
    string txnId;
    if (existing is not null && existing.Status is JobStatuses.Retrying or JobStatuses.Failed or JobStatuses.WaitingForMapping)
    {
        txnPk = existing.TxnPk;
        txnId = existing.TxnId;
        await engine.RetryAsync(txnPk, "dogre2etest@local");
        results.Add($"- Retry: txnPk={txnPk} txnId={txnId}");
    }
    else
    {
        var enq = await engine.EnqueueAsync(hq, destId, txnType, doDocNo, dryRun: false, "dogre2etest@local", confirmed: true);
        txnPk = enq.TxnPk;
        txnId = enq.TxnId;
        results.Add($"- Enqueue: txnPk={txnPk} txnId={txnId} — {enq.Message}");
    }
    if (txnPk <= 0) { results.Add(""); continue; }

    var pending = await jobs.ListAsync(JobStatuses.Pending);
    var mine = pending.FirstOrDefault(j => j.TxnId == txnPk && j.JobKind == "TXN");
    if (mine is not null)
        await engine.ProcessJobAsync(mine);

    var txn = await engine.GetAsync(txnPk);
    results.Add($"- Status: {txn?.Status} dest={txn?.DstDocNo} err={txn?.ErrorMessage}");
    results.Add("");
}

await WriteResultsAsync(results);

static IHost BuildHost(string root)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Environment.EnvironmentName = "Development";
    builder.Configuration["CentralDb:Path"] = builder.Configuration["CentralDb:Path"] ?? @"C:\SQLData\SqlAccountSync\SYNC.FDB";
    builder.Configuration["CentralDb:Host"] = builder.Configuration["CentralDb:Host"] ?? "localhost";
    builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
    builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
    builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
    builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
    builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(c =>
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"));
    builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(c =>
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"));
    try
    {
        var region = builder.Configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
        builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
            new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region)));
    }
    catch { /* optional */ }
    builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
    builder.Services.AddSingleton<CentralDb>();
    builder.Services.AddSingleton<CompanyConnectionFactory>();
    builder.Services.AddScoped<IDatabaseRegistry, DatabaseRegistry>();
    builder.Services.AddScoped<ISqlAccountReadAdapter, FirebirdReadAdapter>();
    builder.Services.AddScoped<ISqlAccountWriteAdapter>(sp => sp.GetRequiredService<SqlAccountApiWriteAdapter>());
    builder.Services.AddScoped<IMappingService, MappingService>();
    builder.Services.AddScoped<IValidationService, ValidationService>();
    builder.Services.AddScoped<IJobQueue, JobQueue>();
    builder.Services.AddScoped<IAuditService, AuditService>();
    builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
    builder.Services.AddScoped<ITransactionEngine, TransactionEngine>();
    return builder.Build();
}

static async Task<string?> EnsureHqDeliveryOrderAsync(List<string> results)
{
    results.Add("## HQ delivery order");
    var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
    using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    var creds = await LoadCredsAsync(sm, "proacc/tenant/TNT10006/sqlApiCredentials");
    if (creds is null)
    {
        results.Add("- HQ credentials: FAILED");
        return null;
    }
    var (ak, sk) = creds.Value;
    var list = await SendAsync(http, ak, sk, region, "GET", "/deliveryorder?offset=0&limit=5");
    results.Add($"- GET /deliveryorder => {list.Status}");
    if (list.Status == 200 && list.Body.Contains("\"data\"", StringComparison.Ordinal))
    {
        using var doc = JsonDocument.Parse(list.Body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            var docno = data[0].GetProperty("docno").GetString()?.Trim();
            results.Add($"- Using existing DO: {docno}");
            results.Add("");
            return docno;
        }
    }

    var tag = $"SYNC-DO-{DateTime.UtcNow:HHmmss}";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["docdate"] = today, ["postdate"] = today, ["taxdate"] = today,
        ["code"] = customer, ["companyname"] = "Customer EXO1", ["country"] = "MY",
        ["area"] = "----", ["agent"] = "----", ["project"] = "----", ["terms"] = "30 Days",
        ["currencycode"] = "----", ["currencyrate"] = "1", ["shipper"] = "----",
        ["description"] = tag, ["cancelled"] = false, ["status"] = 0,
        ["docamt"] = "0", ["localdocamt"] = "0", ["d_amount"] = "0",
        ["branchname"] = "BILLING", ["dcountry"] = "MY", ["submissiontype"] = 0,
        ["transferable"] = true, ["printcount"] = 0, ["changed"] = true,
        ["sdsdocdetail"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["seq"] = 1000, ["itemcode"] = item, ["location"] = "----", ["project"] = "----",
                ["description"] = tag, ["receiveqty"] = "0", ["returnqty"] = "0", ["qty"] = "1",
                ["uom"] = "UNIT", ["rate"] = "1", ["sqty"] = "1", ["suomqty"] = "1",
                ["unitprice"] = "0", ["taxinclusive"] = false, ["amount"] = "0", ["localamount"] = "0",
                ["amountwithtax"] = "0", ["printable"] = true, ["transferable"] = true, ["changed"] = true,
                ["sdsserialnumber"] = Array.Empty<object>()
            }
        }
    });
    var post = await SendAsync(http, ak, sk, region, "POST", "/deliveryorder", payload);
    results.Add($"- POST /deliveryorder ({tag}) => {post.Status}");
    results.Add($"  {Short(post.Body)}");
    if (post.Status is < 200 or >= 300)
    {
        results.Add("");
        return null;
    }
    using (var created = JsonDocument.Parse(post.Body))
    {
        var docno = created.RootElement.GetProperty("docno").GetString()?.Trim();
        results.Add($"- Created DO: {docno}");
        results.Add("");
        return docno;
    }
}

static async Task EnsureSubSupplierAsync(string label, string secretId, string code, List<string> results)
{
    var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
    using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    var creds = await LoadCredsAsync(sm, secretId);
    if (creds is null)
    {
        results.Add($"## {label} supplier {code}: creds FAILED");
        return;
    }
    var (ak, sk) = creds.Value;
    var get = await SendAsync(http, ak, sk, region, "GET", $"/supplier?code={Uri.EscapeDataString(code)}");
    if (get.Status == 200 && get.Body.Contains(code, StringComparison.Ordinal))
    {
        results.Add($"## {label} supplier {code}: exists");
        return;
    }
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code, ["companyname"] = $"GR test supplier ({label})", ["country"] = "MY",
        ["area"] = "----", ["agent"] = "----", ["terms"] = "30 Days", ["currencycode"] = "----",
        ["cancelled"] = false, ["changed"] = true
    });
    var post = await SendAsync(http, ak, sk, region, "POST", "/supplier", payload);
    results.Add($"## {label} supplier {code}: POST => {post.Status} {Short(post.Body)}");
}

static async Task EnsureSupplierMappingAsync(IMappingService mapping, int destId, string master, string sub, List<string> results)
{
    var existing = await mapping.FindBySourceAsync(hq, destId, EntityCodes.Customer, master);
    if (existing is not null
        && string.Equals(existing.DstEntityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase))
        return;

    var row = existing ?? new EntityMapping
    {
        MasterDbId = hq, SubDbId = destId, MasterCode = master, MasterDesc = master
    };
    row.SrcEntityCode = EntityCodes.Customer;
    row.DstEntityCode = EntityCodes.Supplier;
    row.EntityCode = EntityCodes.Customer;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "dogre2etest@local");
    results.Add($"## Supplier mapping {master}->{sub} on dest {destId}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureItemMappingAsync(IMappingService mapping, int destId, string master, string sub, List<string> results)
{
    var existing = await mapping.FindAsync(hq, destId, EntityCodes.Item, master);
    if (existing is not null) return;
    var (ok, msg, _) = await mapping.ConfirmAsync(new EntityMapping
    {
        MasterDbId = hq, SubDbId = destId, EntityCode = EntityCodes.Item,
        SrcEntityCode = EntityCodes.Item, DstEntityCode = EntityCodes.Item,
        MasterCode = master, SubCode = sub, MasterDesc = master, SubDesc = sub
    }, "dogre2etest@local");
    results.Add($"## S1 item mapping {master}->{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task WriteResultsAsync(List<string> results)
{
    var text = string.Join(Environment.NewLine, results);
    Console.WriteLine(text);
    var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "dogre2etest-results.md"));
    await File.WriteAllTextAsync(outPath, text);
    Console.WriteLine($"Wrote {outPath}");
}

static async Task<(string AccessKey, string SecretKey)?> LoadCredsAsync(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        return (doc.RootElement.GetProperty("accessKey").GetString()!, doc.RootElement.GetProperty("secretKey").GetString()!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Credential load failed: {ex.Message}");
        return null;
    }
}

static async Task<(int Status, string Body)> SendAsync(HttpClient http, string ak, string sk, string region, string method, string path, string? json = null)
{
    const string baseUrl = "https://api.sql.my";
    var uri = new Uri(baseUrl.TrimEnd('/') + path);
    var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
    var signed = AwsV4Signer.SignedHeaders(method, uri, bodyBytes, json is null ? null : "application/json", ak, sk, region, "execute-api");
    using var request = new HttpRequestMessage(new HttpMethod(method), uri);
    foreach (var (k, v) in signed) request.Headers.TryAddWithoutValidation(k, v);
    if (json is not null)
    {
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }
    using var response = await http.SendAsync(request);
    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
}

static string Short(string body) => body.Length <= 400 ? body : body[..400] + "...";

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "dogre2etest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
