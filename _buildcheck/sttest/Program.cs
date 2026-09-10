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
const string txnType = "ST_TO_ST";
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

var results = new List<string> { "# Stock transfer S1 → S2", $"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", "" };
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

var srcCreds = await LoadCredsAsync(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S1");
var dstCreds = await LoadCredsAsync(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S2");
if (srcCreds is null || dstCreds is null)
{
    results.Add("FAIL: Could not load S1/S2 SQL API credentials.");
    await WriteAsync(results, root);
    return;
}
var (ak, sk) = srcCreds.Value;
var (ak2, sk2) = dstCreds.Value;

const string fromLoc = "SYNC-FROM";
const string toLoc = "SYNC-TO";
await EnsureLocationAsync(http, ak, sk, region, "S1", fromLoc, results);
await EnsureLocationAsync(http, ak, sk, region, "S1", toLoc, results);
await EnsureLocationAsync(http, ak2, sk2, region, "S2", fromLoc, results);
await EnsureLocationAsync(http, ak2, sk2, region, "S2", toLoc, results);

var srcLocs = await read.GetWarehousesAsync(srcDb);
var dstLocs = await read.GetWarehousesAsync(dstDb);
results.Add($"S1 locations: {string.Join(", ", srcLocs.Select(l => l.Code).Take(10))}");
results.Add($"S2 locations: {string.Join(", ", dstLocs.Select(l => l.Code).Take(10))}");

if (!srcLocs.Any(l => string.Equals(l.Code, fromLoc, StringComparison.OrdinalIgnoreCase))
    || !srcLocs.Any(l => string.Equals(l.Code, toLoc, StringComparison.OrdinalIgnoreCase)))
{
    results.Add("FAIL: Could not create/read SYNC-FROM and SYNC-TO on Sub 1.");
    await WriteAsync(results, root);
    return;
}
results.Add($"Using locations: {fromLoc} → {toLoc}");

await EnsureWarehouseMappingAsync(mapping, s1, s2, fromLoc, fromLoc, results);
await EnsureWarehouseMappingAsync(mapping, s1, s2, toLoc, toLoc, results);
await EnsureItemMappingAsync(mapping, s1, s2, item, itemOnS2, results);
results.Add("");

var stNo = await EnsureStockTransferAsync(http, ak, sk, region, fromLoc, toLoc, item, results);
if (stNo is null)
{
    await WriteAsync(results, root);
    return;
}

results.Add($"## Dry run {stNo}");
var dry = await engine.DryRunAsync(s1, s2, txnType, stNo);
foreach (var c in dry.Checks)
    results.Add($"- {(c.Ok ? "OK" : "FAIL")} {c.Name}: {c.Detail}");
results.Add($"Summary: {dry.Summary}");
results.Add("");

if (!dry.WouldSucceed)
{
    results.Add("Dry run failed — transfer not attempted.");
    await WriteAsync(results, root);
    return;
}

var srcDoc = await read.GetDocumentAsync(srcDb, "ST", stNo);
var qtyBySeq = new Dictionary<int, decimal>();
if (srcDoc is not null)
{
    foreach (var line in srcDoc.Lines)
    {
        var q = line.Qty > 1 ? Math.Round(line.Qty / 2m, 2, MidpointRounding.AwayFromZero) : line.Qty;
        if (q <= 0) q = line.Qty;
        qtyBySeq[line.Seq] = q;
        results.Add($"- Qty override seq {line.Seq}: {line.Qty} → {q}");
    }
}

results.Add("## Transfer");
var enq = await engine.EnqueueAsync(s1, s2, txnType, stNo, dryRun: false, "sttest@local", confirmed: true, qtyBySeq);
results.Add($"- Enqueue: txnPk={enq.TxnPk} txnId={enq.TxnId} — {enq.Message}");
if (enq.TxnPk <= 0)
{
    await WriteAsync(results, root);
    return;
}

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

await WriteAsync(results, root);

static async Task EnsureLocationAsync(
    HttpClient http, string ak, string sk, string region, string label, string code, List<string> results)
{
    var get = await SendAsync(http, ak, sk, region, "GET", $"/location?code={Uri.EscapeDataString(code)}");
    if (get.Status == 200 && get.Body.Contains(code, StringComparison.OrdinalIgnoreCase))
    {
        results.Add($"- Location on {label} ({code}): exists");
        return;
    }

    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code,
        ["description"] = $"Sync test location {code}",
        ["cancelled"] = false,
        ["changed"] = true
    });
    var post = await SendAsync(http, ak, sk, region, "POST", "/location", payload);
    results.Add($"- Location on {label} ({code}): POST => {post.Status}");
    if (post.Status is < 200 or >= 300)
        results.Add($"  {Short(post.Body)}");
}

static string? PickLocation(IReadOnlyList<MasterRecord> locs, int index)
{
    var usable = locs
        .Select(l => (l.Code ?? "").Trim())
        .Where(c => c.Length > 0 && c != "----")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (usable.Count == 0)
        return null;
    if (usable.Count == 1)
        return index == 0 ? usable[0] : null;
    return usable[Math.Min(index, usable.Count - 1)];
}

static async Task EnsureWarehouseMappingAsync(
    IMappingService mapping, int src, int dst, string master, string sub, List<string> results)
{
    var existing = await mapping.FindAsync(src, dst, EntityCodes.Warehouse, master);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase))
    {
        results.Add($"- Warehouse map {master}→{sub}: already OK");
        return;
    }
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Warehouse;
    row.DstEntityCode = EntityCodes.Warehouse;
    row.EntityCode = EntityCodes.Warehouse;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "sttest@local");
    results.Add($"- Warehouse map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
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
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "sttest@local");
    results.Add($"- Item map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task<string?> EnsureStockTransferAsync(
    HttpClient http, string ak, string sk, string region, string fromLoc, string toLoc, string itemCode, List<string> results)
{
    var list = await SendAsync(http, ak, sk, region, "GET", "/stocktransfer?offset=0&limit=20");
    results.Add($"- GET /stocktransfer => {list.Status}");
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
                        results.Add($"- Using existing ST on S1: {docno}");
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

    var tag = $"SYNC-ST-{DateTime.UtcNow:HHmmss}";
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
                ["itemcode"] = itemCode,
                ["fromlocation"] = fromLoc,
                ["tolocation"] = toLoc,
                ["project"] = "----",
                ["description"] = tag,
                ["uom"] = "UNIT",
                ["qty"] = "2",
                ["sqty"] = "2",
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

    var post = await SendAsync(http, ak, sk, region, "POST", "/stocktransfer", payload);
    results.Add($"- POST /stocktransfer ({fromLoc}→{toLoc}/{itemCode}) => {post.Status}");
    if (post.Status is < 200 or >= 300)
    {
        results.Add($"  {Short(post.Body)}");
        return null;
    }

    try
    {
        using var created = JsonDocument.Parse(post.Body);
        var no = created.RootElement.TryGetProperty("docno", out var dn) ? dn.GetString()?.Trim() : null;
        results.Add($"- Created ST: {no}");
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
    var outPath = Path.Combine(root, "_buildcheck", "sttest-results.md");
    await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, results));
    Console.WriteLine(string.Join(Environment.NewLine, results));
    Console.WriteLine($"Wrote {outPath}");
}

static string Short(string b) => b.Length <= 400 ? b : b[..400] + "...";

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "sttest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
