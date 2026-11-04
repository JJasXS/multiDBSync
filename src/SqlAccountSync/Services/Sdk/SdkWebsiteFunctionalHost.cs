using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// CLI: Website-path SDK CUD lab on MIRACLE/ONYAT only (unique SDKPOC* codes).
/// Usage: dotnet run -- --sdk-website-functional --db=MIRACLE
/// Requires SQLACC_SDK_PASSWORD and supervisor backup confirm (or env SQLACC_SDK_CONFIRM_SUPERVISOR_BACKUP).
/// </summary>
public static class SdkWebsiteFunctionalHost
{
    private static readonly Dictionary<string, string> DbCodeToFdb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIRACLE"] = "ACC-MIRACLE.FDB",
        ["ONYAT"] = "ACC-ONYAT.FDB"
    };

    public static async Task RunAsync(string[] args)
    {
        LoadLocalEnv();
        SqlAccSdkPasswordStatus.TryPromoteEnvPasswordToProcess("env:SQLACC_SDK_PASSWORD", out _);

        var dbCode = (ReadArg(args, "--db") ?? "MIRACLE").Trim();
        if (!DbCodeToFdb.TryGetValue(dbCode, out var fdb))
        {
            Console.Error.WriteLine("Refused: --db must be MIRACLE or ONYAT.");
            Environment.ExitCode = 2;
            return;
        }

        var other = fdb.Equals("ACC-MIRACLE.FDB", StringComparison.OrdinalIgnoreCase)
            ? "ACC-ONYAT.FDB"
            : "ACC-MIRACLE.FDB";

        using var host = BuildHost();
        var sdk = host.Services.GetRequiredService<IOptions<SqlAccSdkOptions>>().Value;
        var password = Environment.GetEnvironmentVariable("SQLACC_SDK_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("SQLACC_SDK_PASSWORD is not set.");
            Environment.ExitCode = 2;
            return;
        }

        var confirm = Environment.GetEnvironmentVariable("SQLACC_SDK_CONFIRM_SUPERVISOR_BACKUP");
        if (string.IsNullOrWhiteSpace(confirm))
            confirm = SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(fdb);

        var probe = new SqlAccSdkWebsiteFunctionalProbe(
            host.Services.GetRequiredService<ISqlAccComAppFactory>());

        Console.WriteLine($"Website functional CUD probe db={dbCode} fdb={fdb} isolateVs={other}");
        // Default: master CUD only (no Document Transfer / POC docs on authorized DBs).
        var includeDocs = args.Any(a =>
            string.Equals(a, "--include-documents", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--include-documents=true", StringComparison.OrdinalIgnoreCase));

        var result = await probe.RunAsync(new SqlAccWebsiteFunctionalRequest(
            DcfPath: sdk.DefaultDcfPath ?? "",
            DatabaseFileName: fdb,
            UserName: sdk.UserName ?? "",
            Password: password.Trim(),
            BackupPath: "",
            AuthorizeLanRead: sdk.AuthorizeLanRead,
            AuthorizedExactTarget: sdk.AuthorizedExactTarget,
            VerifyOtherDatabaseFileName: other,
            SupervisorManagedBackupConfirm: confirm,
            SkipDocuments: !includeDocs)).ConfigureAwait(false);

        Console.WriteLine($"Success={result.Success} SafetyBlocked={result.SafetyBlocked} LoginOk={result.LoginOk} IsolationOk={result.IsolationOk}");
        Console.WriteLine(result.Message);
        if (result.Steps is not null)
        {
            foreach (var s in result.Steps)
            {
                Console.WriteLine(
                    $"[{(s.Ok ? "OK" : "FAIL")}]{(s.Blocked ? "[BLOCKED]" : "")} {s.Feature}/{s.Operation}: {s.Detail}" +
                    (string.IsNullOrWhiteSpace(s.Code) ? "" : $" code={s.Code}") +
                    (s.OrphanPossible ? " ORPHAN?" : ""));
            }
        }

        if (!result.Success)
            Environment.ExitCode = result.SafetyBlocked ? 2 : 1;
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.Configure<SqlAccSdkOptions>(builder.Configuration.GetSection(SqlAccSdkOptions.SectionName));
        builder.Services.AddSingleton<ISqlAccComAppFactory, SqlAccComAppFactory>();
        return builder.Build();
    }

    private static void LoadLocalEnv()
    {
        foreach (var envPath in new[]
                 {
                     Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                     Path.Combine(AppContext.BaseDirectory, ".env"),
                 })
        {
            if (!File.Exists(envPath)) continue;
            DotNetEnv.Env.Load(envPath, new DotNetEnv.LoadOptions(setEnvVars: true, clobberExistingVars: false));
            break;
        }
    }

    private static string? ReadArg(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..];
        }
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
