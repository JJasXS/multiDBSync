using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

/*
 * Isolated SDK Write POC / Phase-2 live CUD verification.
 * Backup evidence (one required):
 *   --backup-path <file.fbk|zip|7z|bak>
 *   OR --confirm-supervisor-backup "SUPERVISOR-MANAGED-RESTORABLE-BACKUP:<FDB>"
 *      (env SQLACC_SDK_CONFIRM_SUPERVISOR_BACKUP). MIRACLE/ONYAT only.
 * Password: env SQLACC_SDK_PASSWORD or interactive prompt (never --password).
 * Project companies: ACC-MIRACLE.FDB / ACC-ONYAT.FDB only (ACC-UAMAT.FDB refused).
 * Does NOT modify website appsettings (AllowLiveCom only in this process).
 */

static string? Opt(List<string> a, string name)
{
    var i = a.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return i < 0 || i + 1 >= a.Count ? null : a[i + 1];
}

static string SecureStringToString(SecureString secure)
{
    var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
    try { return Marshal.PtrToStringUni(ptr) ?? ""; }
    finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
}

static string ResolvePassword(List<string> argsList)
{
    if (argsList.Any(a => string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Refusing --password. Use SQLACC_SDK_PASSWORD or interactive prompt.");

    var fromEnv = Environment.GetEnvironmentVariable("SQLACC_SDK_PASSWORD");
    if (!string.IsNullOrEmpty(fromEnv))
    {
        Console.WriteLine("Password source: SQLACC_SDK_PASSWORD (not printed).");
        return fromEnv;
    }

    if (Console.IsInputRedirected)
        throw new InvalidOperationException("No SQLACC_SDK_PASSWORD and input is redirected.");

    Console.Write("SQL Acc password (not echoed): ");
    var secure = new SecureString();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace) { if (secure.Length > 0) secure.RemoveAt(secure.Length - 1); continue; }
        if (!char.IsControl(key.KeyChar)) secure.AppendChar(key.KeyChar);
    }
    secure.MakeReadOnly();
    try { return SecureStringToString(secure); }
    finally { secure.Dispose(); }
}

static SqlAccLiveCudSuite ParseSuite(string? raw)
{
    var s = (raw ?? "all").Trim().ToLowerInvariant();
    return s switch
    {
        "gl" or "gl_acc" or "account" => SqlAccLiveCudSuite.GlAccount,
        "supplier" or "ap_supplier" => SqlAccLiveCudSuite.Supplier,
        "fixes" or "gl-supplier" or "gl_supplier" => SqlAccLiveCudSuite.GlAccount | SqlAccLiveCudSuite.Supplier,
        "group" or "st_group" or "stockgroup" => SqlAccLiveCudSuite.StockGroup,
        "gr" or "ph_gr" or "goodsreceived" => SqlAccLiveCudSuite.GoodsReceived,
        "uom" or "item" or "st_item" => SqlAccLiveCudSuite.ItemUom,
        "agent" or "website" or "masters" => SqlAccLiveCudSuite.None,
        _ => SqlAccLiveCudSuite.All
    };
}

var argsList = args.ToList();
var live = argsList.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
var dcf = Opt(argsList, "--dcf") ?? "";
var fdb = Opt(argsList, "--fdb") ?? "";
var company = Opt(argsList, "--company") ?? "miracle";
var verifyOther = Opt(argsList, "--verify-other-fdb") ?? "";
var user = Opt(argsList, "--user") ?? "ADMIN";
var backup = Opt(argsList, "--backup-path")
             ?? Environment.GetEnvironmentVariable("SQLACC_SDK_BACKUP_PATH")
             ?? "";
var supervisorConfirm = Opt(argsList, "--confirm-supervisor-backup")
                        ?? Environment.GetEnvironmentVariable("SQLACC_SDK_CONFIRM_SUPERVISOR_BACKUP")
                        ?? "";
var authorizeLan = argsList.Any(a => string.Equals(a, "--authorize-lan", StringComparison.OrdinalIgnoreCase));
var authorizedTarget = Opt(argsList, "--authorized-target") ?? "";
var suiteRaw = Opt(argsList, "--suite") ?? "all";
var suite = ParseSuite(suiteRaw);
var agentOnly = string.Equals(suiteRaw, "agent", StringComparison.OrdinalIgnoreCase);
var websiteSuite = string.Equals(suiteRaw, "website", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(suiteRaw, "masters", StringComparison.OrdinalIgnoreCase);

if (!SqlAccProjectTargets.TryResolveCompanyAlias(company, out var resolvedFdb, out var dbCode))
{
    Console.WriteLine($"Unknown --company '{company}'. Use miracle or onyat.");
    return 2;
}

if (string.IsNullOrWhiteSpace(dcf))
    dcf = SqlAccProjectTargets.SharedDcf;
if (string.IsNullOrWhiteSpace(fdb))
    fdb = resolvedFdb;

if (string.Equals(verifyOther, "none", StringComparison.OrdinalIgnoreCase)
    || verifyOther == "-")
{
    verifyOther = "";
}
else if (string.IsNullOrWhiteSpace(verifyOther))
{
    verifyOther = string.Equals(fdb, SqlAccProjectTargets.MiracleFdb, StringComparison.OrdinalIgnoreCase)
        ? SqlAccProjectTargets.OnyatFdb
        : SqlAccProjectTargets.MiracleFdb;
}

if (string.IsNullOrWhiteSpace(authorizedTarget) && authorizeLan)
    authorizedTarget = SqlAccProjectTargets.AuthorizedBothCompanies;

Console.WriteLine("=== SDK Write / Live CUD POC preflight ===");
Console.WriteLine($"CompanyAlias={company} DBCODE={dbCode}");
Console.WriteLine($"DCF={dcf}");
Console.WriteLine($"FDB={fdb}");
Console.WriteLine($"VerifyOtherFdb={verifyOther}");
Console.WriteLine($"User={user}");
Console.WriteLine($"Suite={suiteRaw} => {suite}");
Console.WriteLine($"BackupPath={(string.IsNullOrWhiteSpace(backup) ? "(missing)" : backup)}");
Console.WriteLine($"BackupExists={(!string.IsNullOrWhiteSpace(backup) && File.Exists(backup))}");
Console.WriteLine($"SupervisorBackupConfirm={(string.IsNullOrWhiteSpace(supervisorConfirm) ? "(missing)" : supervisorConfirm)}");
Console.WriteLine($"AuthorizeLan={authorizeLan}");
Console.WriteLine($"AuthorizedTarget={authorizedTarget}");
Console.WriteLine($"Live={live}");
Console.WriteLine("Note: website appsettings Provider/AllowLiveCom are NOT modified by this tool.");

if (!live)
{
    Console.WriteLine("STOP: --live not specified. No COM write performed.");
    return 0;
}

try
{
    SqlAccWritePocSafety.EnsureRestorableBackup(fdb, backup, supervisorConfirm);
    SqlAccReadPocSafety.EnsureLiveReadAuthorized(dcf, fdb, authorizeLan, authorizedTarget);
    if (!string.IsNullOrWhiteSpace(verifyOther))
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(dcf, verifyOther, authorizeLan, authorizedTarget);
    Console.WriteLine("Safety gates passed (backup evidence + write target" +
                      (string.IsNullOrWhiteSpace(verifyOther) ? "; isolation skipped" : " + verify-other target") + ").");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine("SAFETY GATE BLOCKED WRITE:");
    Console.WriteLine(ex.Message);
    return 3;
}

string password;
try { password = ResolvePassword(argsList); }
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    return 4;
}

var options = Options.Create(new SqlAccSdkOptions { AllowLiveCom = true });
var factory = new SqlAccComAppFactory(options);
using var sta = new StaWorkQueue();

if (websiteSuite)
{
    Console.WriteLine($"Running Website-path SDK functional verification on {fdb} (password not logged)...");
    var websiteProbe = new SqlAccSdkWebsiteFunctionalProbe(factory, sta);
    var websiteResult = await websiteProbe.RunAsync(new SqlAccWebsiteFunctionalRequest(
        DcfPath: dcf,
        DatabaseFileName: fdb,
        UserName: user,
        Password: password,
        BackupPath: backup,
        AuthorizeLanRead: authorizeLan,
        AuthorizedExactTarget: authorizedTarget,
        VerifyOtherDatabaseFileName: verifyOther,
        SupervisorManagedBackupConfirm: supervisorConfirm));

    Console.WriteLine($"Database={websiteResult.DatabaseFileName}");
    Console.WriteLine($"LoginOk={websiteResult.LoginOk} IsolationOk={websiteResult.IsolationOk}");
    Console.WriteLine($"Success={websiteResult.Success} SafetyBlocked={websiteResult.SafetyBlocked}");
    Console.WriteLine(websiteResult.Message);
    if (websiteResult.Steps is not null)
    {
        Console.WriteLine("--- Steps ---");
        foreach (var step in websiteResult.Steps)
        {
            Console.WriteLine(
                $"[{(step.Ok ? "OK" : "FAIL")}{(step.Blocked ? " BLOCKED" : "")}] {step.Feature}.{step.Operation}" +
                (string.IsNullOrWhiteSpace(step.Code) ? "" : $" code={step.Code}") +
                (string.IsNullOrWhiteSpace(step.DocNo) ? "" : $" docno={step.DocNo}") +
                $" | {step.Detail}" +
                (step.OrphanPossible ? " | ORPHAN?" : ""));
        }
    }

    return websiteResult.Success ? 0 : 1;
}

if (agentOnly)
{
    Console.WriteLine($"Running Agent CRUD POC on {fdb} (password not logged)...");
    var probe = new SqlAccSdkAgentWriteProbe(factory, sta);
    var result = await probe.RunAsync(new SqlAccAgentWriteRequest(
        DcfPath: dcf,
        DatabaseFileName: fdb,
        UserName: user,
        Password: password,
        BackupPath: backup,
        AuthorizeLanRead: authorizeLan,
        AuthorizedExactTarget: authorizedTarget,
        SupervisorManagedBackupConfirm: supervisorConfirm));

    Console.WriteLine($"AgentCode={result.AgentCode}");
    Console.WriteLine($"LoginOk={result.LoginOk} CreateOk={result.CreateOk} UpdateOk={result.UpdateOk} DeleteOk={result.DeleteOk}");
    Console.WriteLine($"LogoutOk={result.LogoutOk} ComCleanupOk={result.ComCleanupOk} OrphanPossible={result.OrphanPossible}");
    Console.WriteLine($"Success={result.Success} SafetyBlocked={result.SafetyBlocked}");
    Console.WriteLine(result.Message);
    return result.Success ? 0 : 1;
}

Console.WriteLine($"Running Live CUD verification on {fdb} suite={suite} (password not logged)...");
var liveProbe = new SqlAccSdkLiveCudVerificationProbe(factory, sta);
var liveResult = await liveProbe.RunAsync(new SqlAccLiveCudRequest(
    DcfPath: dcf,
    DatabaseFileName: fdb,
    UserName: user,
    Password: password,
    BackupPath: backup,
    AuthorizeLanRead: authorizeLan,
    AuthorizedExactTarget: authorizedTarget,
    Suite: suite,
    VerifyOtherDatabaseFileName: verifyOther,
    SupervisorManagedBackupConfirm: supervisorConfirm));

Console.WriteLine($"Database={liveResult.DatabaseFileName}");
Console.WriteLine($"LoginOk={liveResult.LoginOk} LogoutOk={liveResult.LogoutOk} ComCleanupOk={liveResult.ComCleanupOk} IsolationOk={liveResult.IsolationOk}");
Console.WriteLine($"Success={liveResult.Success} SafetyBlocked={liveResult.SafetyBlocked}");
Console.WriteLine(liveResult.Message);
if (liveResult.Steps is not null)
{
    Console.WriteLine("--- Steps ---");
    foreach (var step in liveResult.Steps)
    {
        Console.WriteLine(
            $"[{(step.Ok ? "OK" : "FAIL")}{(step.Blocked ? " BLOCKED" : "")}] {step.Operation}" +
            (string.IsNullOrWhiteSpace(step.Code) ? "" : $" code={step.Code}") +
            (string.IsNullOrWhiteSpace(step.DocNo) ? "" : $" docno={step.DocNo}") +
            (string.IsNullOrWhiteSpace(step.DocKey) ? "" : $" dockey={step.DocKey}") +
            $" | {step.Detail}" +
            (step.OrphanPossible ? " | ORPHAN?" : ""));
    }
}

return liveResult.Success ? 0 : 1;
