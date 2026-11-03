using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

/*
 * Isolated SDK Read POC (no web host / workers).
 * Password: env SQLACC_SDK_PASSWORD or interactive secure prompt — never --password.
 * Project companies: ACC-MIRACLE.FDB / ACC-ONYAT.FDB (ACC-UAMAT.FDB refused).
 */

static int Usage()
{
    Console.WriteLine("SqlAccSdkReadPoc — SDK Login + SELECT-only metadata + Logout");
    Console.WriteLine("Required for live: --live --dcf <path> --fdb <file.FDB> --user <name>");
    Console.WriteLine("Or: --live --company miracle|onyat --user <name>  (uses shared project DCF)");
    Console.WriteLine("Company switch: --live --switch --user <name>  (Miracle → Onyat → Miracle)");
    Console.WriteLine("BizObject inspect (CHM application_objects4, metadata only):");
    Console.WriteLine("  --live --inspect-bizobjects --company miracle --user <name> --authorize-lan ...");
    Console.WriteLine("  --live --inspect-bizobjects --inspect-targets-only ...  (compact REST-blocker report)");
    Console.WriteLine("  --export-field-map <path.md>  (full Fields + XMLFieldDefs; implies inspect; no CUD)");
    Console.WriteLine("Password: env SQLACC_SDK_PASSWORD, or interactive prompt (never --password).");
    Console.WriteLine("LAN/UNC: --authorize-lan --authorized-target \"<dcf>|<fdb>[;...]\"");
    return 2;
}

static string? Opt(List<string> a, string name)
{
    var i = a.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= a.Count) return null;
    return a[i + 1];
}

static string SecureStringToString(SecureString secure)
{
    var ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
    try { return Marshal.PtrToStringUni(ptr) ?? ""; }
    finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
}

static string ReadPasswordInteractive()
{
    Console.Write("SQL Acc password (not echoed): ");
    var secure = new SecureString();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (secure.Length > 0) secure.RemoveAt(secure.Length - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar))
            secure.AppendChar(key.KeyChar);
    }
    secure.MakeReadOnly();
    try { return SecureStringToString(secure); }
    finally { secure.Dispose(); }
}

static string ResolvePassword(List<string> argsList)
{
    if (argsList.Any(a => string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Refusing --password. Use env SQLACC_SDK_PASSWORD or interactive prompt.");

    var fromEnv = Environment.GetEnvironmentVariable("SQLACC_SDK_PASSWORD");
    if (!string.IsNullOrEmpty(fromEnv))
    {
        Console.WriteLine("Password source: environment SQLACC_SDK_PASSWORD (value not printed).");
        return fromEnv;
    }

    if (Console.IsInputRedirected)
        throw new InvalidOperationException("No SQLACC_SDK_PASSWORD and console input is redirected.");

    Console.WriteLine("Password source: interactive secure prompt.");
    return ReadPasswordInteractive();
}

var argsList = args.ToList();
if (argsList.Contains("-h") || argsList.Contains("--help"))
    return Usage();

var live = argsList.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
var switchCompanies = argsList.Any(a => string.Equals(a, "--switch", StringComparison.OrdinalIgnoreCase));
var inspectTargetsOnly = argsList.Any(a => string.Equals(a, "--inspect-targets-only", StringComparison.OrdinalIgnoreCase));
var exportFieldMap = Opt(argsList, "--export-field-map") ?? "";
var inspectBizObjects = inspectTargetsOnly
    || !string.IsNullOrWhiteSpace(exportFieldMap)
    || argsList.Any(a => string.Equals(a, "--inspect-bizobjects", StringComparison.OrdinalIgnoreCase));
var dcf = Opt(argsList, "--dcf") ?? "";
var fdb = Opt(argsList, "--fdb") ?? "";
var company = Opt(argsList, "--company") ?? "";
var user = Opt(argsList, "--user") ?? "";
var sql = Opt(argsList, "--sql") ?? SqlAccReadPocSafety.MinimalMetadataSql;
var authorizeLan = argsList.Any(a => string.Equals(a, "--authorize-lan", StringComparison.OrdinalIgnoreCase));
var authorizedTarget = Opt(argsList, "--authorized-target") ?? "";

if (!string.IsNullOrWhiteSpace(company))
{
    if (!SqlAccProjectTargets.TryResolveCompanyAlias(company, out var resolvedFdb, out var dbCode))
    {
        Console.WriteLine($"Unknown --company '{company}'. Use miracle or onyat.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(dcf))
        dcf = SqlAccProjectTargets.SharedDcf;
    if (string.IsNullOrWhiteSpace(fdb))
        fdb = resolvedFdb;
    Console.WriteLine($"Resolved company alias '{company}' => DBCODE={dbCode} FDB={fdb}");
}

if (string.IsNullOrWhiteSpace(authorizedTarget) && authorizeLan)
    authorizedTarget = SqlAccProjectTargets.AuthorizedBothCompanies;

Console.WriteLine("=== SDK Read POC safety preflight ===");
Console.WriteLine($"Explicit DCF: {dcf}");
Console.WriteLine($"Explicit FDB: {fdb}");
Console.WriteLine($"SwitchMode: {switchCompanies}");
Console.WriteLine($"InspectBizObjects: {inspectBizObjects}");
Console.WriteLine($"InspectTargetsOnly: {inspectTargetsOnly}");
Console.WriteLine($"User: {user}");
Console.WriteLine($"LAN/UNC DCF: {(string.IsNullOrWhiteSpace(dcf) ? "(n/a)" : SqlAccReadPocSafety.IsLanOrUncTarget(dcf).ToString())}");
Console.WriteLine($"AuthorizeLanRead: {authorizeLan}");
Console.WriteLine($"AuthorizedExactTarget: {authorizedTarget}");
Console.WriteLine($"Requested key: {(!string.IsNullOrWhiteSpace(dcf) && !string.IsNullOrWhiteSpace(fdb) ? SqlAccReadPocSafety.NormalizeTarget(dcf, fdb) : "(incomplete)")}");
Console.WriteLine($"Live requested: {live}");
Console.WriteLine("SQL: " + (inspectBizObjects ? "(n/a — BizObject metadata inspect)" : sql));

if (!live)
{
    Console.WriteLine("STOP: --live not specified. No COM performed.");
    return 0;
}

if (string.IsNullOrWhiteSpace(user))
{
    Console.WriteLine("Missing --user.");
    return 2;
}

if (switchCompanies)
{
    if (string.IsNullOrWhiteSpace(dcf))
        dcf = SqlAccProjectTargets.SharedDcf;
    try
    {
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(dcf, SqlAccProjectTargets.MiracleFdb, authorizeLan, authorizedTarget);
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(dcf, SqlAccProjectTargets.OnyatFdb, authorizeLan, authorizedTarget);
        Console.WriteLine("Safety gate: both company targets authorized.");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine("SAFETY GATE BLOCKED LIVE CONNECT:");
        Console.WriteLine(ex.Message);
        return 3;
    }
}
else
{
    try
    {
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(dcf, fdb, authorizeLan, authorizedTarget);
        Console.WriteLine("Safety gate: authorized target match OK.");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine("SAFETY GATE BLOCKED LIVE CONNECT:");
        Console.WriteLine(ex.Message);
        return 3;
    }
}

string password;
try { password = ResolvePassword(argsList); }
catch (Exception ex)
{
    Console.WriteLine("Password resolution failed: " + ex.Message);
    return 4;
}

if (string.IsNullOrEmpty(password))
{
    Console.WriteLine("Password is empty.");
    return 4;
}

var options = Options.Create(new SqlAccSdkOptions { AllowLiveCom = true, ProgId = "SQLAcc.BizApp" });
var factory = new SqlAccComAppFactory(options);
using var sta = new StaWorkQueue();

if (switchCompanies)
{
    var switchProbe = new SqlAccSdkCompanySwitchProbe(factory, sta);
    Console.WriteLine();
    Console.WriteLine("Attempting SDK company switch (Miracle → Onyat → Miracle), password not logged...");
    var sw = await switchProbe.RunAsync(new SqlAccCompanySwitchRequest(
        DcfPath: dcf,
        FirstDatabaseFileName: SqlAccProjectTargets.MiracleFdb,
        SecondDatabaseFileName: SqlAccProjectTargets.OnyatFdb,
        UserName: user,
        Password: password,
        AuthorizeLanRead: authorizeLan,
        AuthorizedExactTarget: authorizedTarget,
        ThirdDatabaseFileName: SqlAccProjectTargets.MiracleFdb));
    Console.WriteLine($"Hop1 FDB={sw.FirstFdb} Login={sw.FirstLoginOk} Read={sw.FirstReadOk} Count={sw.FirstRecordCount} Logout={sw.FirstLogoutOk}");
    Console.WriteLine($"Hop2 FDB={sw.SecondFdb} Login={sw.SecondLoginOk} Read={sw.SecondReadOk} Count={sw.SecondRecordCount} Logout={sw.SecondLogoutOk}");
    Console.WriteLine($"Hop3 FDB={sw.ThirdFdb} Login={sw.ThirdLoginOk} Read={sw.ThirdReadOk} Count={sw.ThirdRecordCount} Logout={sw.ThirdLogoutOk}");
    Console.WriteLine($"ComCleanupOk={sw.ComCleanupOk} Success={sw.Success} SafetyBlocked={sw.SafetyBlocked}");
    Console.WriteLine(sw.Message);
    return sw.Success ? 0 : 1;
}

if (inspectBizObjects)
{
    var inspectProbe = new SqlAccSdkBizObjectInspectProbe(factory, sta);
    Console.WriteLine();
    Console.WriteLine(inspectTargetsOnly
        ? "Attempting TARGETED BizObject metadata inspect (no New/Edit/Save/Delete/Post), password not logged..."
        : "Attempting SDK BizObject metadata inspect (no New/Edit/Save/Delete/Post), password not logged...");
    var ir = await inspectProbe.RunAsync(new SqlAccBizObjectInspectRequest(
        DcfPath: dcf,
        DatabaseFileName: fdb,
        UserName: user,
        Password: password,
        AuthorizeLanRead: authorizeLan,
        AuthorizedExactTarget: authorizedTarget,
        TargetsOnly: inspectTargetsOnly));

    if (!string.IsNullOrWhiteSpace(exportFieldMap))
    {
        var mapText = SqlAccBizObjectInspectTargets.FormatFullFieldMap(ir);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exportFieldMap));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(exportFieldMap, mapText);
        Console.WriteLine($"Wrote full field map (CUD unverified): {Path.GetFullPath(exportFieldMap)}");
    }

    if (inspectTargetsOnly)
    {
        Console.Write(SqlAccBizObjectInspectTargets.FormatCompactReport(ir));
        return ir.Success ? 0 : 1;
    }

    Console.WriteLine($"LoginOk={ir.LoginOk} IsLoginOk={ir.IsLoginOk} LogoutOk={ir.LogoutOk} ComCleanupOk={ir.ComCleanupOk}");
    Console.WriteLine($"Success={ir.Success} SafetyBlocked={ir.SafetyBlocked}");
    Console.WriteLine(ir.Message);
    Console.WriteLine();
    Console.WriteLine($"=== All BizObjects ({ir.AllBizObjectNames?.Count ?? 0}) ===");
    foreach (var n in ir.AllBizObjectNames ?? [])
        Console.WriteLine("  " + n);
    Console.WriteLine();
    Console.WriteLine($"=== Related filter ({ir.RelatedNames?.Count ?? 0}) ===");
    foreach (var n in ir.RelatedNames ?? [])
        Console.WriteLine("  " + n);
    Console.WriteLine();
    Console.WriteLine("=== Inspected (metadata only; CUD NOT verified) ===");
    foreach (var m in ir.Inspected ?? [])
    {
        Console.WriteLine($"--- {m.Name} | Found={m.Found} | Error={m.FindError ?? "(none)"} ---");
        foreach (var ds in m.DataSets)
        {
            Console.WriteLine($"  DataSet: {ds.Name}  fields={ds.Fields.Count}");
            foreach (var f in ds.Fields.Take(40))
                Console.WriteLine($"    {f.FieldName}  type={f.DataType}  size={f.DataSize}");
            if (ds.Fields.Count > 40)
                Console.WriteLine($"    … +{ds.Fields.Count - 40} more fields");
        }
        if (!string.IsNullOrWhiteSpace(m.XmlFieldDefs))
        {
            var xml = m.XmlFieldDefs.Length <= 500 ? m.XmlFieldDefs : m.XmlFieldDefs[..500] + "…";
            Console.WriteLine("  XMLFieldDefs: " + xml.Replace('\r', ' ').Replace('\n', ' '));
        }
    }
    return ir.Success ? 0 : 1;
}

var probe = new SqlAccSdkReadProbe(factory, sta);
Console.WriteLine();
Console.WriteLine("Attempting SDK Login + metadata SELECT + Logout (password not logged)...");
var result = await probe.RunAsync(new SqlAccReadProbeRequest(
    DcfPath: dcf,
    DatabaseFileName: fdb,
    UserName: user,
    Password: password,
    SelectSql: sql,
    AuthorizeLanRead: authorizeLan,
    AuthorizedExactTarget: authorizedTarget));

Console.WriteLine($"LoginOk={result.LoginOk}");
Console.WriteLine($"IsLoginOk={result.IsLoginOk}");
Console.WriteLine($"ReadOk={result.ReadOk} RecordCount={result.RecordCount}");
Console.WriteLine($"LogoutOk={result.LogoutOk}");
Console.WriteLine($"ComCleanupOk={result.ComCleanupOk}");
Console.WriteLine($"Success={result.Success} SafetyBlocked={result.SafetyBlocked}");
Console.WriteLine(result.Message);
return result.Success ? 0 : 1;
