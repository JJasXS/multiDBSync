using System.Globalization;
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

const int s1 = 7;
const int s2 = 8;
const string item = "Dogs";
const string itemOnS2 = "Dogs";

var root = FindRepoRoot();
foreach (var envPath in new[]
{
    Path.Combine(root, ".env"),
    Path.Combine(root, "src", "SqlAccountSync", ".env"),
    Path.Combine(root, "_buildcheck", ".env")
})
{
    if (File.Exists(envPath))
    {
        Env.Load(envPath);
        Console.WriteLine($"Loaded env: {envPath}");
        break;
    }
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
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

var results = new List<string> { "# CN ↔ DN transfer S1 → S2", $"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", "" };
var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

var host = BuildHost(root);
using var scope = host.Services.CreateScope();
var engine = scope.ServiceProvider.GetRequiredService<ITransactionEngine>();
var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
var mapping = scope.ServiceProvider.GetRequiredService<IMappingService>();
var registry = scope.ServiceProvider.GetRequiredService<IDatabaseRegistry>();
var read = scope.ServiceProvider.GetRequiredService<ISqlAccountReadAdapter>();

var srcDb = await registry.GetAsync(s1);
var dstDb = await registry.GetAsync(s2);
results.Add($"Source: {srcDb?.DisplayName} (id={s1}, code={srcDb?.DbCode})");
results.Add($"Dest:   {dstDb?.DisplayName} (id={s2}, code={dstDb?.DbCode})");
results.Add("");

if (srcDb is null || dstDb is null)
{
    results.Add("FAIL: Sub 1 or Sub 2 database registration missing.");
    await WriteAsync(results, root);
    return;
}

var types = await engine.ListTypesAsync();
foreach (var code in new[] { "CN_TO_DN", "DN_TO_CN" })
{
    var t = types.FirstOrDefault(x => x.Code == code);
    results.Add($"- Type {code}: srcDoc={t.Src} dstDoc={t.Dst} dstPath={t.DstPath} active={t.Code is not null}");
}
results.Add("");

var srcCreds = await LoadCredsAsync(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S1");
var dstCreds = await LoadCredsAsync(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S2");
if (srcCreds is null || dstCreds is null)
{
    results.Add("FAIL: Could not load S1/S2 SQL API credentials.");
    await WriteAsync(results, root);
    return;
}
var (ak, sk) = srcCreds.Value;

var customers = await read.GetCustomersAsync(srcDb);
var customer = customers
    .Select(c => (c.Code ?? "").Trim())
    .FirstOrDefault(c => c.Length > 0 && c != "----");
if (string.IsNullOrEmpty(customer))
{
    results.Add("FAIL: No customer on Sub 1 to use for CN/DN.");
    await WriteAsync(results, root);
    return;
}
results.Add($"Using customer: {customer}");

await EnsureCustomerMappingAsync(mapping, s1, s2, customer, customer, results);
await EnsureItemMappingAsync(mapping, s1, s2, item, itemOnS2, results);
results.Add("");

var cnNo = await EnsureSalesNoteAsync(http, ak, sk, region, "/salescreditnote", customer, item, results);
var dnNo = await EnsureSalesNoteAsync(http, ak, sk, region, "/salesdebitnote", customer, item, results);
if (cnNo is null || dnNo is null)
{
    await WriteAsync(results, root);
    return;
}

await RunTransferAsync(engine, jobs, read, srcDb, s1, s2, "CN_TO_DN", "CN", cnNo, results);
results.Add("");
await RunTransferAsync(engine, jobs, read, srcDb, s1, s2, "DN_TO_CN", "DN", dnNo, results);

await WriteAsync(results, root);

static async Task RunTransferAsync(
    ITransactionEngine engine, IJobQueue jobs, ISqlAccountReadAdapter read, RegisteredDatabase srcDb,
    int srcId, int dstId, string txnType, string srcDocType, string docNo, List<string> results)
{
    results.Add($"## {txnType} dry run {docNo}");
    var dry = await engine.DryRunAsync(srcId, dstId, txnType, docNo);
    foreach (var c in dry.Checks)
        results.Add($"- {(c.Ok ? "OK" : "FAIL")} {c.Name}: {c.Detail}");
    results.Add($"Summary: {dry.Summary}");

    if (!dry.WouldSucceed)
    {
        results.Add("Dry run failed — transfer not attempted.");
        return;
    }

    // Confirm Firebird can read the source
    var srcDoc = await read.GetDocumentAsync(srcDb, srcDocType, docNo);
    results.Add($"- Firebird read {srcDocType} {docNo}: {(srcDoc is null ? "MISS" : $"OK party={srcDoc.PartyCode} lines={srcDoc.Lines.Count}")}");

    results.Add($"## {txnType} transfer");
    var enq = await engine.EnqueueAsync(srcId, dstId, txnType, docNo, dryRun: false, "cndntest@local", confirmed: true);
    results.Add($"- Enqueue: txnPk={enq.TxnPk} txnId={enq.TxnId} — {enq.Message}");
    if (enq.TxnPk <= 0)
        return;

    var pending = await jobs.ListAsync(JobStatuses.Pending);
    var mine = pending.FirstOrDefault(j => j.TxnId == enq.TxnPk && j.JobKind == "TXN");
    if (mine is not null)
        await engine.ProcessJobAsync(mine);
    else
        results.Add("- WARN: no pending TXN job found to process");

    var txn = await engine.GetAsync(enq.TxnPk);
    results.Add($"- Status: {txn?.Status}");
    results.Add($"- Dest doc: {txn?.DstDocNo} ({txn?.DstDocKey})");
    results.Add($"- Error: {txn?.ErrorMessage}");
    if (txn is not null)
    {
        var events = await engine.TimelineAsync(enq.TxnPk);
        foreach (var ev in events.TakeLast(8))
            results.Add($"  · {ev.Step}: {ev.Result} {ev.Detail}");
    }
}

static async Task EnsureCustomerMappingAsync(
    IMappingService mapping, int src, int dst, string master, string sub, List<string> results)
{
    var existing = await mapping.FindPartyMapAsync(src, dst, EntityCodes.Customer, master, EntityCodes.Customer);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(existing.DstEntityCode)
            || string.Equals(existing.DstEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)))
    {
        results.Add($"- Customer map {master}→{sub}: already OK");
        return;
    }

    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Customer;
    row.DstEntityCode = EntityCodes.Customer;
    row.EntityCode = EntityCodes.Customer;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "cndntest@local");
    results.Add($"- Customer map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureItemMappingAsync(
    IMappingService mapping, int src, int dst, string master, string sub, List<string> results)
{
    var existing = await mapping.FindAsync(src, dst, EntityCodes.Item, master);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase))
    {
        results.Add($"- Item map {master}→{sub}: already OK");
        return;
    }
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Item;
    row.DstEntityCode = EntityCodes.Item;
    row.EntityCode = EntityCodes.Item;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "cndntest@local");
    results.Add($"- Item map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task<string?> EnsureSalesNoteAsync(
    HttpClient http, string ak, string sk, string region, string path, string customer, string itemCode, List<string> results)
{
    var list = await SendAsync(http, ak, sk, region, "GET", $"{path}?offset=0&limit=20");
    results.Add($"- GET {path} => {list.Status}");
    if (list.Status == 200)
    {
        try
        {
            using var doc = JsonDocument.Parse(list.Body);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var row in data.EnumerateArray())
                {
                    var docno = row.TryGetProperty("docno", out var n) ? n.GetString()?.Trim() : null;
                    if (!string.IsNullOrEmpty(docno))
                    {
                        results.Add($"- Using existing on S1: {docno}");
                        return docno;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            results.Add($"- WARN parse list: {ex.Message}");
        }
    }
    else
    {
        results.Add($"  {Short(list.Body)}");
    }

    var isDebit = path.Contains("debit", StringComparison.OrdinalIgnoreCase);
    var tag = $"SYNC-{(isDebit ? "DN" : "CN")}-{DateTime.UtcNow:HHmmss}";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var line = new Dictionary<string, object?>
    {
        ["seq"] = 1000,
        ["itemcode"] = itemCode,
        ["location"] = "----",
        ["project"] = "----",
        ["description"] = tag,
        ["qty"] = "1",
        ["uom"] = "UNIT",
        ["rate"] = "1",
        ["sqty"] = "1",
        ["suomqty"] = "0",
        ["unitprice"] = "10",
        ["taxinclusive"] = false,
        ["amount"] = "10",
        ["localamount"] = "10",
        ["amountwithtax"] = "10",
        ["printable"] = true,
        ["changed"] = true
    };
    if (isDebit)
    {
        line["deliverydate"] = today;
        line["transferable"] = true;
    }
    else
    {
        line["unitcost"] = "10";
    }

    var payload = new Dictionary<string, object?>
    {
        ["docdate"] = today,
        ["postdate"] = today,
        ["taxdate"] = today,
        ["code"] = customer,
        ["currencycode"] = "----",
        ["currencyrate"] = "1",
        ["description"] = tag,
        ["area"] = "----",
        ["agent"] = "----",
        ["project"] = "----",
        ["terms"] = "30 Days",
        ["docamt"] = "10",
        ["localdocamt"] = "10",
        ["cancelled"] = false,
        ["status"] = 0,
        ["note"] = tag,
        ["printcount"] = 0,
        ["changed"] = true,
        ["sdsdocdetail"] = new[] { line }
    };
    if (isDebit)
        payload["transferable"] = true;

    var json = JsonSerializer.Serialize(payload);
    var post = await SendAsync(http, ak, sk, region, "POST", path, json);
    results.Add($"- POST {path} ({customer}/{itemCode}) => {post.Status}");
    if (post.Status is < 200 or >= 300)
    {
        results.Add($"  {Short(post.Body)}");
        return null;
    }

    try
    {
        using var created = JsonDocument.Parse(post.Body);
        var no = created.RootElement.TryGetProperty("docno", out var dn) ? dn.GetString()?.Trim() : null;
        results.Add($"- Created: {no}");
        return no;
    }
    catch
    {
        results.Add($"- Created but could not parse docno: {Short(post.Body)}");
        return null;
    }
}

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

static async Task<(string, string)?> LoadCredsAsync(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        return (doc.RootElement.GetProperty("accessKey").GetString()!, doc.RootElement.GetProperty("secretKey").GetString()!);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Secret load failed: {ex.Message}");
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

static async Task WriteAsync(List<string> results, string root)
{
    var outPath = Path.Combine(root, "_buildcheck", "cndntest-results.md");
    await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, results));
    Console.WriteLine(string.Join(Environment.NewLine, results));
    Console.WriteLine($"Wrote {outPath}");
}

static string Short(string b) => b.Length <= 400 ? b : b[..400] + "...";

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "cndntest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
