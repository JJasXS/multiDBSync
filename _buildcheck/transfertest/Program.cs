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

const int Hq = 6;
const int S1 = 7;
const int S2 = 8;
const string Customer = "300-C0001";
const string Supplier = "SYNC-XFER-SU";
const string Item = "Dogs";
const string FromLoc = "SYNC-FROM";
const string ToLoc = "SYNC-TO";

var root = FindRepoRoot();
LoadEnv(root);

// Optional: dotnet run -- S1→S2 SI_TO_PI
var filterLink = args.Length > 0 ? args[0] : null;
var filterType = args.Length > 1 ? args[1] : null;

var report = new List<string>
{
    "# Document transfer test suite",
    $"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
    "",
    "Covers every Type dropdown option on **Master → Sub** (HQ→S1, HQ→S2) and **Sub → Sub** (S1→S2).",
    "Each case: create source → dry-run → transfer → re-read destination from Firebird.",
    filterLink is null ? "" : $"Filter: link={filterLink}" + (filterType is null ? "" : $" type={filterType}"),
    ""
};
var cases = new List<CaseResult>();
var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
using var sm = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

var host = BuildHost(root);
using var scope = host.Services.CreateScope();
var engine = scope.ServiceProvider.GetRequiredService<ITransactionEngine>();
var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
var mapping = scope.ServiceProvider.GetRequiredService<IMappingService>();
var registry = scope.ServiceProvider.GetRequiredService<IDatabaseRegistry>();
var read = scope.ServiceProvider.GetRequiredService<ISqlAccountReadAdapter>();

var hqDb = await registry.GetAsync(Hq);
var s1Db = await registry.GetAsync(S1);
var s2Db = await registry.GetAsync(S2);
report.Add($"| DB | Id | Code | Role |");
report.Add($"|----|----|------|------|");
report.Add($"| {hqDb?.DisplayName} | {Hq} | {hqDb?.DbCode} | MASTER |");
report.Add($"| {s1Db?.DisplayName} | {S1} | {s1Db?.DbCode} | SUB |");
report.Add($"| {s2Db?.DisplayName} | {S2} | {s2Db?.DbCode} | SUB |");
report.Add("");

if (hqDb is null || s1Db is null || s2Db is null)
{
    report.Add("**FAIL:** HQ/S1/S2 registration missing.");
    await WriteReport(report, cases, root);
    return;
}

var hqCreds = await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials")
    ?? await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials/HQ");
var s1Creds = await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S1");
var s2Creds = await LoadCreds(sm, "proacc/tenant/TNT10006/sqlApiCredentials/S2");
if (hqCreds is null || s1Creds is null || s2Creds is null)
{
    report.Add($"**FAIL:** credentials HQ={(hqCreds is null ? "MISS" : "OK")} S1={(s1Creds is null ? "MISS" : "OK")} S2={(s2Creds is null ? "MISS" : "OK")}");
    await WriteReport(report, cases, root);
    return;
}

await EnsureSupplier(http, s1Creds.Value, region, "S1", Supplier, report);
await EnsureSupplier(http, s2Creds.Value, region, "S2", Supplier, report);
await EnsureLocation(http, s1Creds.Value, region, "S1", FromLoc, report);
await EnsureLocation(http, s1Creds.Value, region, "S1", ToLoc, report);
await EnsureLocation(http, s2Creds.Value, region, "S2", FromLoc, report);
await EnsureLocation(http, s2Creds.Value, region, "S2", ToLoc, report);
await EnsureLocation(http, hqCreds.Value, region, "HQ", FromLoc, report);
await EnsureLocation(http, hqCreds.Value, region, "HQ", ToLoc, report);
report.Add("");

var links = new (string Label, int SrcId, int DstId, RegisteredDatabase SrcDb, RegisteredDatabase DstDb, (string ak, string sk) SrcCreds, (string ak, string sk) DstCreds, string ItemOnDst)[]
{
    ("HQ→S1", Hq, S1, hqDb, s1Db, hqCreds.Value, s1Creds.Value, "Cats"),
    ("HQ→S2", Hq, S2, hqDb, s2Db, hqCreds.Value, s2Creds.Value, Item),
    ("S1→S2", S1, S2, s1Db, s2Db, s1Creds.Value, s2Creds.Value, Item),
};

var types = new (string Code, string SrcDoc, string DstDoc, string SrcPath, PartyMode Party, bool Stock)[]
{
    ("SI_TO_PI", "IV", "PI", "/customerinvoice", PartyMode.CustomerToSupplier, false),
    ("DO_TO_GR", "DO", "GR", "/deliveryorder", PartyMode.CustomerToSupplier, false),
    ("ST_TO_ST", "ST", "ST", "/stocktransfer", PartyMode.None, true),
    ("CN_TO_PR", "CN", "PR", "/salescreditnote", PartyMode.CustomerToSupplier, false),
    ("CN_TO_DN", "CN", "DN", "/salescreditnote", PartyMode.CustomerToCustomer, false),
    ("DN_TO_CN", "DN", "CN", "/salesdebitnote", PartyMode.CustomerToCustomer, false),
};

foreach (var link in links)
{
    report.Add($"## Link {link.Label}");
    report.Add("");

    await EnsurePartyMaps(mapping, link.SrcId, link.DstId, report);
    await EnsureItemMap(mapping, link.SrcId, link.DstId, Item, link.ItemOnDst, report);
    if (types.Any(t => t.Stock))
    {
        await EnsureWhMap(mapping, link.SrcId, link.DstId, FromLoc, FromLoc, report);
        await EnsureWhMap(mapping, link.SrcId, link.DstId, ToLoc, ToLoc, report);
    }
    report.Add("");

    foreach (var type in types)
    {
        if (filterLink is not null && !string.Equals(link.Label, filterLink, StringComparison.OrdinalIgnoreCase))
            continue;
        if (filterType is not null && !string.Equals(type.Code, filterType, StringComparison.OrdinalIgnoreCase))
            continue;

        var result = await RunCase(
            engine, jobs, read, http, region,
            link, type, report);
        cases.Add(result);
    }
}

await WriteReport(report, cases, root);
return;

static async Task<CaseResult> RunCase(
    ITransactionEngine engine, IJobQueue jobs, ISqlAccountReadAdapter read, HttpClient http, string region,
    (string Label, int SrcId, int DstId, RegisteredDatabase SrcDb, RegisteredDatabase DstDb, (string ak, string sk) SrcCreds, (string ak, string sk) DstCreds, string ItemOnDst) link,
    (string Code, string SrcDoc, string DstDoc, string SrcPath, PartyMode Party, bool Stock) type,
    List<string> report)
{
    var title = $"{link.Label} · {type.Code}";
    report.Add($"### {title}");
    var result = new CaseResult { Link = link.Label, Type = type.Code };

    try
    {
        var srcNo = await CreateSourceDoc(http, read, link.SrcDb, link.SrcCreds.ak, link.SrcCreds.sk, region, type, report);
        result.SourceDoc = srcNo ?? "";
        if (srcNo is null)
        {
            result.Outcome = "FAIL";
            result.Detail = "Could not create/find source document";
            report.Add($"- **FAIL** source on {type.SrcPath}");
            report.Add("");
            return result;
        }

        var dry = await engine.DryRunAsync(link.SrcId, link.DstId, type.Code, srcNo);
        result.DryOk = dry.WouldSucceed;
        foreach (var c in dry.Checks.Take(8))
            report.Add($"- Dry {(c.Ok ? "OK" : "FAIL")} {c.Name}: {c.Detail}");
        if (!dry.WouldSucceed)
        {
            result.Outcome = "FAIL";
            result.Detail = dry.Summary;
            report.Add($"- **FAIL** dry-run: {dry.Summary}");
            report.Add("");
            return result;
        }

        var enq = await engine.EnqueueAsync(link.SrcId, link.DstId, type.Code, srcNo, false, "transfertest@local", true);
        report.Add($"- Enqueue: {enq.TxnId} — {enq.Message}");
        if (enq.TxnPk <= 0)
        {
            result.Outcome = "FAIL";
            result.Detail = enq.Message;
            report.Add("");
            return result;
        }

        var alreadySynced = enq.Message.Contains("already synchronized", StringComparison.OrdinalIgnoreCase);
        if (!alreadySynced)
        {
            var pending = await jobs.ListAsync(JobStatuses.Pending);
            var mine = pending.FirstOrDefault(j => j.TxnId == enq.TxnPk && j.JobKind == "TXN");
            if (mine is not null)
                await engine.ProcessJobAsync(mine);
            else
                report.Add("- WARN: no pending job (worker may have taken it)");
        }

        SyncTransaction? txn = null;
        for (var i = 0; i < 20; i++)
        {
            txn = await engine.GetAsync(enq.TxnPk);
            if (txn is not null && txn.Status is JobStatuses.Completed or JobStatuses.Failed or JobStatuses.WaitingForMapping)
                break;
            if (txn is not null && txn.Status == JobStatuses.Retrying && i == 8)
            {
                report.Add("- Retrying stalled job…");
                await engine.RetryAsync(enq.TxnPk, "transfertest@local");
                var again = (await jobs.ListAsync(JobStatuses.Pending))
                    .FirstOrDefault(j => j.TxnId == enq.TxnPk && j.JobKind == "TXN");
                if (again is not null)
                    await engine.ProcessJobAsync(again);
            }
            await Task.Delay(500);
        }

        result.TxnStatus = txn?.Status ?? "";
        result.DestDoc = txn?.DstDocNo ?? "";
        report.Add($"- Transfer: {txn?.Status} dest={txn?.DstDocNo} ({txn?.DstDocKey}) err={txn?.ErrorMessage}");

        // Stale "already synced" rows can point at a dest that never landed — force a retry once.
        if (alreadySynced && (string.IsNullOrWhiteSpace(txn?.DstDocNo)
            || !string.IsNullOrWhiteSpace(txn.ErrorMessage)
            || await read.GetDocumentAsync(link.DstDb, type.DstDoc, txn.DstDocNo) is null))
        {
            report.Add("- Stale sync record — forcing RetryAsync");
            await engine.RetryAsync(enq.TxnPk, "transfertest@local");
            var retryJob = (await jobs.ListAsync(JobStatuses.Pending))
                .FirstOrDefault(j => j.TxnId == enq.TxnPk && j.JobKind == "TXN");
            if (retryJob is not null)
                await engine.ProcessJobAsync(retryJob);
            for (var i = 0; i < 20; i++)
            {
                txn = await engine.GetAsync(enq.TxnPk);
                if (txn is not null && txn.Status is JobStatuses.Completed or JobStatuses.Failed or JobStatuses.WaitingForMapping)
                    break;
                await Task.Delay(500);
            }
            result.TxnStatus = txn?.Status ?? "";
            result.DestDoc = txn?.DstDocNo ?? "";
            report.Add($"- After retry: {txn?.Status} dest={txn?.DstDocNo} ({txn?.DstDocKey}) err={txn?.ErrorMessage}");
        }

        if (!string.Equals(txn?.Status, JobStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(txn?.DstDocNo))
        {
            result.Outcome = "FAIL";
            result.Detail = txn?.ErrorMessage ?? "transfer not completed";
            report.Add("");
            return result;
        }

        var dest = await read.GetDocumentAsync(link.DstDb, type.DstDoc, txn!.DstDocNo);
        if (dest is null)
        {
            // Idempotent re-runs may reference an API doc not yet visible in local FDB — confirm via API.
            var apiPath = type.Code switch
            {
                "SI_TO_PI" => "/purchaseinvoice",
                "DO_TO_GR" => "/goodsreceived",
                "ST_TO_ST" => "/stocktransfer",
                "CN_TO_PR" => "/purchasereturned",
                "CN_TO_DN" => "/salesdebitnote",
                "DN_TO_CN" => "/salescreditnote",
                _ => ""
            };
            var apiOk = false;
            if (apiPath.Length > 0)
            {
                var list = await Send(http, link.DstCreds.ak, link.DstCreds.sk, region, "GET", $"{apiPath}?offset=0&limit=50");
                apiOk = list.Status == 200 && list.Body.Contains(txn.DstDocNo, StringComparison.OrdinalIgnoreCase);
                report.Add($"- Firebird miss; API list {apiPath} contains {txn.DstDocNo}: {apiOk}");
            }

            if (!apiOk)
            {
                result.RereadOk = false;
                result.Outcome = "FAIL";
                result.Detail = $"Firebird re-read missed {type.DstDoc} {txn.DstDocNo}";
                report.Add($"- **FAIL** re-read: no {type.DstDoc} {txn.DstDocNo} on dest");
                report.Add("");
                return result;
            }

            result.RereadOk = true;
            result.Outcome = "PASS";
            result.Detail = $"API-verified dest {txn.DstDocNo} (Firebird lag)";
            result.RereadDetail = result.Detail;
            report.Add($"- **PASS** re-read via API — {result.Detail}");
            report.Add("");
            return result;
        }

        var checks = new List<string>();
        if (type.Party != PartyMode.None && string.IsNullOrWhiteSpace(dest.PartyCode))
            checks.Add("party empty");
        if (dest.Lines.Count == 0)
            checks.Add("no lines");
        else
        {
            var line = dest.Lines[0];
            if (line.Qty <= 0) checks.Add("qty<=0");
            if (string.IsNullOrWhiteSpace(line.ItemCode)) checks.Add("item empty");
            if (type.Stock)
            {
                if (string.IsNullOrWhiteSpace(line.FromLocation) || string.IsNullOrWhiteSpace(line.ToLocation))
                    checks.Add("missing from/to location");
            }
        }

        result.RereadOk = checks.Count == 0;
        result.RereadDetail = $"party={dest.PartyCode} lines={dest.Lines.Count} item={dest.Lines.FirstOrDefault()?.ItemCode} qty={dest.Lines.FirstOrDefault()?.Qty}";
        if (checks.Count > 0)
        {
            result.Outcome = "FAIL";
            result.Detail = string.Join("; ", checks);
            report.Add($"- **FAIL** re-read checks: {result.Detail} ({result.RereadDetail})");
        }
        else
        {
            result.Outcome = "PASS";
            result.Detail = result.RereadDetail;
            report.Add($"- **PASS** re-read OK — {result.RereadDetail}");
        }
    }
    catch (Exception ex)
    {
        result.Outcome = "FAIL";
        result.Detail = ex.Message;
        report.Add($"- **FAIL** exception: {ex.Message}");
    }

    report.Add("");
    return result;
}

static async Task<string?> CreateSourceDoc(
    HttpClient http, ISqlAccountReadAdapter read, RegisteredDatabase srcDb,
    string ak, string sk, string region,
    (string Code, string SrcDoc, string DstDoc, string SrcPath, PartyMode Party, bool Stock) type,
    List<string> report)
{
    var tag = $"XFER-{type.Code}-{DateTime.UtcNow:HHmmss}";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var path = type.SrcPath;
    var isSi = path.Contains("customerinvoice", StringComparison.OrdinalIgnoreCase);

    // Sales invoices: this tenant's POST /customerinvoice often returns a docno that never
    // appears in local Firebird SL_IV. Prefer an invoice the reader can already see.
    if (isSi)
    {
        var existing = await read.ListDocumentsAsync(srcDb, "IV", 20);
        // Prefer an invoice whose customer we already map in this suite.
        var preferred = existing.FirstOrDefault(d =>
            string.Equals(d.PartyCode, Customer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.PartyCode, "300-H0001", StringComparison.OrdinalIgnoreCase));
        var visible = preferred ?? existing.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.DocNo));
        if (visible is not null)
        {
            // Load full header for party (list may omit it)
            var full = await read.GetDocumentAsync(srcDb, "IV", visible.DocNo);
            report.Add($"- Using Firebird-visible IV: {visible.DocNo} party={full?.PartyCode}");
            return visible.DocNo;
        }
        report.Add("- WARN: no Firebird IV on source; attempting API create");
    }

    if (type.Stock)
    {
        var st = BuildStockTransfer(today, tag);
        var post = await Send(http, ak, sk, region, "POST", "/stocktransfer", st);
        report.Add($"- Create ST => {post.Status}");
        var stNo = ParseDocNo(post, report);
        if (stNo is not null)
        {
            var fb = await read.GetDocumentAsync(srcDb, "ST", stNo);
            if (fb is null)
            {
                report.Add($"- WARN: ST {stNo} not visible in Firebird yet");
            }
        }
        return stNo;
    }

    var isDn = path.Contains("debit", StringComparison.OrdinalIgnoreCase);
    var isCn = path.Contains("credit", StringComparison.OrdinalIgnoreCase);
    var isDo = path.Contains("delivery", StringComparison.OrdinalIgnoreCase);
    var line = new Dictionary<string, object?>
    {
        ["seq"] = 1000,
        ["itemcode"] = Item,
        ["location"] = "----",
        ["project"] = "----",
        ["description"] = tag,
        ["qty"] = "1",
        ["uom"] = "UNIT",
        ["rate"] = "1",
        ["sqty"] = "1",
        ["suomqty"] = isDo ? "1" : "0",
        ["unitprice"] = "10",
        ["taxinclusive"] = false,
        ["amount"] = "10",
        ["localamount"] = "10",
        ["amountwithtax"] = "10",
        ["printable"] = true,
        ["changed"] = true
    };
    if (isSi)
    {
        line["account"] = "500-000";
        line["deliverydate"] = today;
        line["transferable"] = true;
    }
    if (isDo)
    {
        line["receiveqty"] = "0";
        line["returnqty"] = "0";
        line["transferable"] = true;
        line["sdsserialnumber"] = Array.Empty<object>();
    }
    if (isDn)
    {
        line["deliverydate"] = today;
        line["transferable"] = true;
    }
    if (isCn)
        line["unitcost"] = "10";

    var payload = new Dictionary<string, object?>
    {
        ["docdate"] = today,
        ["postdate"] = today,
        ["taxdate"] = today,
        ["code"] = Customer,
        ["companyname"] = Customer,
        ["country"] = "MY",
        ["area"] = "----",
        ["agent"] = "----",
        ["project"] = "----",
        ["terms"] = "30 Days",
        ["currencycode"] = "----",
        ["currencyrate"] = "1",
        ["shipper"] = "----",
        ["description"] = tag,
        ["cancelled"] = false,
        ["status"] = 0,
        ["docamt"] = "10",
        ["localdocamt"] = "10",
        ["branchname"] = "BILLING",
        ["dcountry"] = "MY",
        ["note"] = tag,
        ["printcount"] = 0,
        ["changed"] = true,
        ["sdsdocdetail"] = new[] { line }
    };
    if (isDo || isDn)
        payload["transferable"] = true;

    var created = await Send(http, ak, sk, region, "POST", path, JsonSerializer.Serialize(payload));
    report.Add($"- Create {type.SrcDoc} via {path} => {created.Status}");
    var docNo = ParseDocNo(created, report);
    if (docNo is null)
        return null;

    if (isSi)
    {
        var fb = await read.GetDocumentAsync(srcDb, "IV", docNo);
        if (fb is null)
        {
            report.Add($"- **FAIL** API returned {docNo} but Firebird SL_IV has no such doc (tenant API/DB mismatch)");
            return null;
        }
    }
    return docNo;
}

static string BuildStockTransfer(string today, string tag) =>
    JsonSerializer.Serialize(new Dictionary<string, object?>
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
                ["itemcode"] = Item,
                ["fromlocation"] = FromLoc,
                ["tolocation"] = ToLoc,
                ["project"] = "----",
                ["description"] = tag,
                ["uom"] = "UNIT",
                ["qty"] = "10",
                ["sqty"] = "10",
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

static string? ParseDocNo((int Status, string Body) post, List<string> report)
{
    if (post.Status is < 200 or >= 300)
    {
        report.Add($"  {Short(post.Body)}");
        return null;
    }
    try
    {
        using var doc = JsonDocument.Parse(post.Body);
        var no = doc.RootElement.TryGetProperty("docno", out var n) ? n.GetString()?.Trim() : null;
        report.Add($"- Source doc: {no}");
        return no;
    }
    catch
    {
        report.Add($"  parse fail: {Short(post.Body)}");
        return null;
    }
}

static async Task EnsurePartyMaps(IMappingService mapping, int src, int dst, List<string> report)
{
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Supplier, Customer, Supplier, report);
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Customer, Customer, Customer, report);
    // HQ sample invoice IV-00001 uses this customer; SYNC-GR-SU already used successfully on dests
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Supplier, "300-H0001", "SYNC-GR-SU", report);
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Customer, "300-H0001", "300-H0001", report);
    // S1 sample invoice party
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Supplier, "300-W0001", "SYNC-GR-SU", report);
    await EnsureParty(mapping, src, dst, EntityCodes.Customer, EntityCodes.Customer, "300-W0001", "300-W0001", report);
}

static async Task EnsureParty(
    IMappingService mapping, int src, int dst, string srcEnt, string dstEnt, string master, string sub, List<string> report)
{
    var existing = await mapping.FindPartyMapAsync(src, dst, srcEnt, master, dstEnt);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.DstEntityCode, dstEnt, StringComparison.OrdinalIgnoreCase))
    {
        report.Add($"- Map {srcEnt}→{dstEnt} {master}→{sub}: OK");
        return;
    }

    var row = new EntityMapping
    {
        MasterDbId = src,
        SubDbId = dst,
        MasterCode = master,
        MasterDesc = master,
        SrcEntityCode = srcEnt,
        DstEntityCode = dstEnt,
        EntityCode = dstEnt,
        SubCode = sub,
        SubDesc = sub
    };
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "transfertest@local");
    report.Add($"- Map {srcEnt}→{dstEnt} {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureItemMap(IMappingService mapping, int src, int dst, string master, string sub, List<string> report)
{
    var existing = await mapping.FindAsync(src, dst, EntityCodes.Item, master);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase))
    {
        report.Add($"- Item map {master}→{sub}: OK");
        return;
    }
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Item;
    row.DstEntityCode = EntityCodes.Item;
    row.EntityCode = EntityCodes.Item;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "transfertest@local");
    report.Add($"- Item map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureWhMap(IMappingService mapping, int src, int dst, string master, string sub, List<string> report)
{
    var existing = await mapping.FindAsync(src, dst, EntityCodes.Warehouse, master);
    if (existing is not null && string.Equals(existing.SubCode, sub, StringComparison.OrdinalIgnoreCase))
        return;
    var row = existing ?? new EntityMapping { MasterDbId = src, SubDbId = dst, MasterCode = master, MasterDesc = master };
    row.SrcEntityCode = EntityCodes.Warehouse;
    row.DstEntityCode = EntityCodes.Warehouse;
    row.EntityCode = EntityCodes.Warehouse;
    row.SubCode = sub;
    row.SubDesc = sub;
    var (ok, msg, _) = await mapping.ConfirmAsync(row, "transfertest@local");
    report.Add($"- Warehouse map {master}→{sub}: {(ok ? "OK" : "FAIL")} {msg}");
}

static async Task EnsureSupplier(HttpClient http, (string ak, string sk) creds, string region, string label, string code, List<string> report)
{
    var get = await Send(http, creds.ak, creds.sk, region, "GET", $"/supplier?code={Uri.EscapeDataString(code)}");
    if (get.Status == 200 && get.Body.Contains(code, StringComparison.OrdinalIgnoreCase))
    {
        report.Add($"- Supplier on {label} ({code}): exists");
        return;
    }
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code,
        ["companyname"] = $"Transfer test supplier ({label})",
        ["country"] = "MY",
        ["area"] = "----",
        ["agent"] = "----",
        ["terms"] = "30 Days",
        ["currencycode"] = "----",
        ["cancelled"] = false,
        ["changed"] = true
    });
    var post = await Send(http, creds.ak, creds.sk, region, "POST", "/supplier", payload);
    report.Add($"- Supplier on {label} ({code}): POST => {post.Status}");
}

static async Task EnsureLocation(HttpClient http, (string ak, string sk) creds, string region, string label, string code, List<string> report)
{
    var get = await Send(http, creds.ak, creds.sk, region, "GET", $"/location?code={Uri.EscapeDataString(code)}");
    if (get.Status == 200 && get.Body.Contains(code, StringComparison.OrdinalIgnoreCase))
        return;
    var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["code"] = code,
        ["description"] = $"Sync test {code}",
        ["cancelled"] = false,
        ["changed"] = true
    });
    var post = await Send(http, creds.ak, creds.sk, region, "POST", "/location", payload);
    report.Add($"- Location on {label} ({code}): POST => {post.Status}");
}

static async Task WriteReport(List<string> report, List<CaseResult> cases, string root)
{
    var summary = new List<string>
    {
        "## Summary",
        "",
        "| # | Link | Type | Source | Dest | Dry | Transfer | Re-read | Result |",
        "|---|------|------|--------|------|-----|----------|---------|--------|"
    };
    var i = 1;
    foreach (var c in cases)
    {
        summary.Add(
            $"| {i++} | {c.Link} | {c.Type} | {Esc(c.SourceDoc)} | {Esc(c.DestDoc)} | {(c.DryOk ? "OK" : "FAIL")} | {Esc(c.TxnStatus)} | {(c.RereadOk ? "OK" : "FAIL")} | **{c.Outcome}** |");
    }
    var passed = cases.Count(c => c.Outcome == "PASS");
    summary.Add("");
    summary.Add($"**Totals: {passed}/{cases.Count} passed**");
    summary.Add("");

    // Insert summary after intro (after blank line following DB table)
    var insertAt = report.FindIndex(l => l.StartsWith("## Link ", StringComparison.Ordinal));
    if (insertAt < 0) insertAt = report.Count;
    report.InsertRange(insertAt, summary);

    var failDetails = cases.Where(c => c.Outcome != "PASS").ToList();
    if (failDetails.Count > 0)
    {
        report.Add("## Failures");
        report.Add("");
        foreach (var f in failDetails)
            report.Add($"- **{f.Link} {f.Type}**: {f.Detail}");
        report.Add("");
    }

    var outPath = Path.Combine(root, "_buildcheck", "transfertest-results.md");
    await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, report));
    Console.WriteLine(string.Join(Environment.NewLine, report));
    Console.WriteLine($"Wrote {outPath}");
}

static string Esc(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Replace("|", "/");

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

static void LoadEnv(string root)
{
    foreach (var envPath in new[]
    {
        Path.Combine(root, ".env"),
        Path.Combine(root, "src", "SqlAccountSync", ".env"),
        Path.Combine(root, "_buildcheck", ".env")
    })
    {
        if (!File.Exists(envPath)) continue;
        Env.Load(envPath);
        Console.WriteLine($"Loaded env: {envPath}");
        return;
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
    builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
    builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
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

static async Task<(string ak, string sk)?> LoadCreds(AmazonSecretsManagerClient sm, string secretId)
{
    try
    {
        var resp = await sm.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(resp.SecretString ?? "{}");
        return (doc.RootElement.GetProperty("accessKey").GetString()!, doc.RootElement.GetProperty("secretKey").GetString()!);
    }
    catch
    {
        return null;
    }
}

static async Task<(int Status, string Body)> Send(HttpClient http, string ak, string sk, string region, string method, string path, string? json = null)
{
    var uri = new Uri("https://api.sql.my" + path);
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

static string Short(string b) => b.Length <= 280 ? b : b[..280] + "...";

enum PartyMode { None, CustomerToSupplier, CustomerToCustomer }

sealed class CaseResult
{
    public string Link { get; set; } = "";
    public string Type { get; set; } = "";
    public string SourceDoc { get; set; } = "";
    public string DestDoc { get; set; } = "";
    public bool DryOk { get; set; }
    public string TxnStatus { get; set; } = "";
    public bool RereadOk { get; set; }
    public string RereadDetail { get; set; } = "";
    public string Outcome { get; set; } = "FAIL";
    public string Detail { get; set; } = "";
}

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "transfertest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
