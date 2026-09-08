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
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;

const int s1 = 7;
const int s2 = 8;
const string txnType = "DO_TO_GR";

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
foreach (var envPath in new[]
{
    Path.Combine(root, ".env"), Path.Combine(root, "src", "SqlAccountSync", ".env"), Path.Combine(root, "_buildcheck", ".env")
})
    if (File.Exists(envPath)) { Env.Load(envPath); break; }

var results = new List<string> { "# Sub ↔ Sub DO→GR test", $"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", "" };
var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

var host = BuildHost(root);
using var scope = host.Services.CreateScope();
var engine = scope.ServiceProvider.GetRequiredService<ITransactionEngine>();
var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
var mapping = scope.ServiceProvider.GetRequiredService<IMappingService>();

var legs = new[]
{
    (Src: s1, Dst: s2, SrcLabel: "S1", DstLabel: "S2", Secret: "proacc/tenant/TNT10006/sqlApiCredentials/S1",
        Customer: "300-W0001", Item: "Dogs", SupplierOnDst: "SYNC-GR-SU"),
    (Src: s2, Dst: s1, SrcLabel: "S2", DstLabel: "S1", Secret: "proacc/tenant/TNT10006/sqlApiCredentials/S2",
        Customer: "300-C0001", Item: "Dogs", SupplierOnDst: "SYNC-GR-SU")
};

foreach (var leg in legs)
{
    results.Add($"## {leg.SrcLabel} → {leg.DstLabel}");
    var creds = await LoadCredsAsync(sm, leg.Secret);
    if (creds is null) { results.Add("- Source API creds FAILED"); results.Add(""); continue; }
    var (ak, sk) = creds.Value;

    await EnsureSubSupplierAsync(http, ak, sk, region, leg.DstLabel, leg.SupplierOnDst, results);
    await EnsureSupplierMappingAsync(mapping, leg.Src, leg.Dst, leg.Customer, leg.SupplierOnDst, results);
    var itemOnDst = leg.DstLabel == "S1" ? "Cats" : leg.Item;
    await EnsureItemMappingAsync(mapping, leg.Src, leg.Dst, leg.Item, itemOnDst, results);

    var doNo = await EnsureDeliveryOrderAsync(http, ak, sk, region, leg.SrcLabel, leg.Customer, leg.Item, results);
    if (doNo is null) { results.Add(""); continue; }

    var dry = await engine.DryRunAsync(leg.Src, leg.Dst, txnType, doNo);
    foreach (var c in dry.Checks)
        results.Add($"- {(c.Ok ? "OK" : "FAIL")} {c.Name}: {c.Detail}");
    results.Add($"Dry-run: {dry.Summary}");

    if (!dry.WouldSucceed) { results.Add(""); continue; }

    var enq = await engine.EnqueueAsync(leg.Src, leg.Dst, txnType, doNo, dryRun: false, "sub2subtest@local", confirmed: true);
    results.Add($"- Enqueue: {enq.TxnId} — {enq.Message}");
    if (enq.TxnPk <= 0) { results.Add(""); continue; }

    var pending = await jobs.ListAsync(JobStatuses.Pending);
    var mine = pending.FirstOrDefault(j => j.TxnId == enq.TxnPk && j.JobKind == "TXN");
    if (mine is not null) await engine.ProcessJobAsync(mine);

    var txn = await engine.GetAsync(enq.TxnPk);
    results.Add($"- Transfer: {txn?.Status} dest={txn?.DstDocNo} err={txn?.ErrorMessage}");
    results.Add("");
}

var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "sub2sub-results.md"));
await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, results));
Console.WriteLine(string.Join(Environment.NewLine, results));
Console.WriteLine($"Wrote {outPath}");

static IHost BuildHost(string root)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Environment.EnvironmentName = "Development";
    builder.Configuration["CentralDb:Path"] = @"C:\SQLData\SqlAccountSync\SYNC.FDB";
    builder.Configuration["CentralDb:Host"] = "localhost";
    builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
    builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
    builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
    builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
    builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0"));
    builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0"));
    try
    {
        var region = builder.Configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
        builder.Services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region)));
    }
    catch { }
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

static async Task<string?> EnsureDeliveryOrderAsync(HttpClient http, string ak, string sk, string region, string label, string customer, string item, List<string> results)
{
    var list = await SendAsync(http, ak, sk, region, "GET", "/deliveryorder?offset=0&limit=10");
    if (list.Status == 200 && list.Body.Contains("\"docno\"", StringComparison.Ordinal))
    {
        using var doc = JsonDocument.Parse(list.Body);
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var row in data.EnumerateArray())
            {
                var code = row.TryGetProperty("code", out var c) ? c.GetString()?.Trim() : null;
                var docno = row.GetProperty("docno").GetString()?.Trim();
                if (code == customer && !string.IsNullOrEmpty(docno))
                {
                    results.Add($"- Using existing DO on {label}: {docno} (customer {customer})");
                    return docno;
                }
            }
    }

    var tag = $"SYNC-S{label}-{DateTime.UtcNow:HHmmss}";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["docdate"] = today, ["postdate"] = today, ["taxdate"] = today,
        ["code"] = customer, ["companyname"] = customer, ["country"] = "MY",
        ["area"] = "----", ["agent"] = "----", ["project"] = "----", ["terms"] = "30 Days",
        ["currencycode"] = "----", ["currencyrate"] = "1", ["shipper"] = "----",
        ["description"] = tag, ["cancelled"] = false, ["status"] = 0,
        ["docamt"] = "0", ["localdocamt"] = "0", ["d_amount"] = "0",
        ["branchname"] = "BILLING", ["dcountry"] = "MY", ["submissiontype"] = 0,
        ["transferable"] = true, ["printcount"] = 0, ["changed"] = true,
        ["sdsdocdetail"] = new[] { new Dictionary<string, object?>
        {
            ["seq"] = 1000, ["itemcode"] = item, ["location"] = "----", ["project"] = "----",
            ["description"] = tag, ["receiveqty"] = "0", ["returnqty"] = "0", ["qty"] = "1",
            ["uom"] = "UNIT", ["rate"] = "1", ["sqty"] = "1", ["suomqty"] = "1",
            ["unitprice"] = "0", ["taxinclusive"] = false, ["amount"] = "0", ["localamount"] = "0",
            ["amountwithtax"] = "0", ["printable"] = true, ["transferable"] = true, ["changed"] = true,
            ["sdsserialnumber"] = Array.Empty<object>()
        }}
    });
    var post = await SendAsync(http, ak, sk, region, "POST", "/deliveryorder", payload);
    results.Add($"- POST DO on {label} ({customer}/{item}) => {post.Status}");
    if (post.Status is < 200 or >= 300) { results.Add($"  {Short(post.Body)}"); return null; }
    using var created = JsonDocument.Parse(post.Body);
    var no = created.RootElement.GetProperty("docno").GetString()?.Trim();
    results.Add($"- Created DO: {no}");
    return no;
}

static async Task EnsureSubSupplierAsync(HttpClient http, string ak, string sk, string region, string label, string code, List<string> results)
{
    var get = await SendAsync(http, ak, sk, region, "GET", $"/supplier?code={Uri.EscapeDataString(code)}");
    if (get.Status == 200 && get.Body.Contains(code, StringComparison.Ordinal)) return;
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code, ["companyname"] = $"Sub2Sub supplier ({label})", ["country"] = "MY",
        ["area"] = "----", ["agent"] = "----", ["terms"] = "30 Days", ["currencycode"] = "----",
        ["cancelled"] = false, ["changed"] = true
    });
    var post = await SendAsync(http, ak, sk, region, "POST", "/supplier", payload);
    results.Add($"- Supplier on {label} ({code}): POST => {post.Status}");
}

static async Task EnsureSupplierMappingAsync(IMappingService mapping, int src, int dst, string master, string sub, List<string> results)
{
    var existing = await mapping.FindBySourceAsync(src, dst, EntityCodes.Customer, master);
    if (existing is not null && string.Equals(existing.DstEntityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase)) return;
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Customer; row.DstEntityCode = EntityCodes.Supplier; row.EntityCode = EntityCodes.Customer;
    row.SubCode = sub; row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "sub2subtest@local");
    results.Add($"- Map customer {master}→supplier {sub} ({src}→{dst}): {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureItemMappingAsync(IMappingService mapping, int src, int dst, string master, string sub, List<string> results)
{
    var existing = await mapping.FindAsync(src, dst, EntityCodes.Item, master);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase)) return;
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Item; row.DstEntityCode = EntityCodes.Item; row.EntityCode = EntityCodes.Item;
    row.SubCode = sub; row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "sub2subtest@local");
    results.Add($"- Map item {master}→{sub} ({src}→{dst}): {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task<(string, string)?> LoadCredsAsync(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        return (doc.RootElement.GetProperty("accessKey").GetString()!, doc.RootElement.GetProperty("secretKey").GetString()!);
    }
    catch { return null; }
}

static async Task<(int Status, string Body)> SendAsync(HttpClient http, string ak, string sk, string region, string method, string path, string? json = null)
{
    const string baseUrl = "https://api.sql.my";
    var uri = new Uri(baseUrl.TrimEnd('/') + path);
    var bodyBytes = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
    var signed = AwsV4Signer.SignedHeaders(method, uri, bodyBytes, json is null ? null : "application/json", ak, sk, region, "execute-api");
    using var request = new HttpRequestMessage(new HttpMethod(method), uri);
    foreach (var (k, v) in signed) request.Headers.TryAddWithoutValidation(k, v);
    if (json is not null) { request.Content = new ByteArrayContent(bodyBytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
    using var response = await http.SendAsync(request);
    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
}

static string Short(string b) => b.Length <= 300 ? b : b[..300] + "...";

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "sub2subtest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
