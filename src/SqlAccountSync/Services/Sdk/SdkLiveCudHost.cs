using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// CLI: LiveCud master suites on MIRACLE/ONYAT only (no GoodsReceived by default).
/// Usage: dotnet run -- --sdk-live-cud --db=MIRACLE --suite=GlAccount,Supplier,StockGroup,ItemUom
/// </summary>
public static class SdkLiveCudHost
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

        var suite = ParseSuite(ReadArg(args, "--suite")
            ?? "GlAccount,Supplier,StockGroup,ItemUom");

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

        var probe = new SqlAccSdkLiveCudVerificationProbe(
            host.Services.GetRequiredService<ISqlAccComAppFactory>());

        Console.WriteLine($"LiveCud probe db={dbCode} fdb={fdb} suite={suite} isolateVs={other}");
        var result = await probe.RunAsync(new SqlAccLiveCudRequest(
            DcfPath: sdk.DefaultDcfPath ?? "",
            DatabaseFileName: fdb,
            UserName: sdk.UserName ?? "",
            Password: password.Trim(),
            BackupPath: "",
            AuthorizeLanRead: sdk.AuthorizeLanRead,
            AuthorizedExactTarget: sdk.AuthorizedExactTarget,
            Suite: suite,
            VerifyOtherDatabaseFileName: other,
            SupervisorManagedBackupConfirm: confirm)).ConfigureAwait(false);

        Console.WriteLine($"Success={result.Success} SafetyBlocked={result.SafetyBlocked} LoginOk={result.LoginOk} IsolationOk={result.IsolationOk}");
        Console.WriteLine(result.Message);
        if (result.Steps is not null)
        {
            foreach (var s in result.Steps)
            {
                Console.WriteLine(
                    $"[{(s.Ok ? "OK" : "FAIL")}]{(s.Blocked ? "[BLOCKED]" : "")} {s.Operation}: {s.Detail}" +
                    (string.IsNullOrWhiteSpace(s.Code) ? "" : $" code={s.Code}") +
                    (s.OrphanPossible ? " ORPHAN?" : ""));
            }
        }

        if (!result.Success)
            Environment.ExitCode = result.SafetyBlocked ? 2 : 1;
    }

    private static SqlAccLiveCudSuite ParseSuite(string raw)
    {
        var suite = SqlAccLiveCudSuite.None;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<SqlAccLiveCudSuite>(part, ignoreCase: true, out var flag))
                suite |= flag;
            else
                Console.Error.WriteLine("Unknown suite flag ignored: " + part);
        }

        if (suite == SqlAccLiveCudSuite.None)
            suite = SqlAccLiveCudSuite.GlAccount | SqlAccLiveCudSuite.Supplier
                    | SqlAccLiveCudSuite.StockGroup | SqlAccLiveCudSuite.ItemUom;
        return suite;
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
