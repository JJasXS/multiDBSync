using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// CLI: Update+Delete leftover SDK POC stock groups on MIRACLE/ONYAT.
/// Usage: dotnet run -- --sdk-stockgroup-poc --db=MIRACLE --code=G2611030941236330
/// </summary>
public static class SdkStockGroupPocHost
{
    private static readonly Dictionary<string, (string Fdb, string Code)> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIRACLE"] = ("ACC-MIRACLE.FDB", "G2611030941236330"),
        ["ONYAT"] = ("ACC-ONYAT.FDB", "G2611030942352342")
    };

    public static async Task RunAsync(string[] args)
    {
        LoadLocalEnv();
        SqlAccSdkPasswordStatus.TryPromoteEnvPasswordToProcess("env:SQLACC_SDK_PASSWORD", out _);

        var dbCode = (ReadArg(args, "--db") ?? "MIRACLE").Trim();
        if (!Defaults.TryGetValue(dbCode, out var def))
        {
            Console.Error.WriteLine("Refused: --db must be MIRACLE or ONYAT.");
            Environment.ExitCode = 2;
            return;
        }

        var fdb = def.Fdb;
        var code = (ReadArg(args, "--code") ?? def.Code).Trim();
        var other = fdb.Equals("ACC-MIRACLE.FDB", StringComparison.OrdinalIgnoreCase)
            ? "ACC-ONYAT.FDB"
            : "ACC-MIRACLE.FDB";

        using var host = BuildHost();
        var sdk = host.Services.GetRequiredService<IOptions<SqlAccSdkOptions>>().Value;
        var writeOpts = host.Services.GetRequiredService<IOptions<SqlAccountingWriteOptions>>().Value;
        Console.WriteLine(
            $"StockGroup POC Update/Delete. Provider={writeOpts.Provider}; BlockRestHttp={writeOpts.BlockRestHttp}; " +
            $"db={dbCode}; code={code}; isolateVs={other}");

        if (!writeOpts.ShouldRefuseRestHttp() || !SqlAccountingWriteProviders.IsSdk(writeOpts.Provider))
        {
            Console.Error.WriteLine("Refused: require Provider=Sdk and REST blocked (BlockRestHttp or Sdk).");
            Environment.ExitCode = 2;
            return;
        }

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

        var probe = new SqlAccSdkStockGroupPocProbe(
            host.Services.GetRequiredService<ISqlAccComAppFactory>());

        var result = await probe.RunAsync(new SqlAccStockGroupPocRequest(
            DcfPath: sdk.DefaultDcfPath ?? "",
            DatabaseFileName: fdb,
            UserName: sdk.UserName ?? "",
            Password: password.Trim(),
            GroupCode: code,
            OtherDatabaseFileName: other,
            AuthorizeLanRead: sdk.AuthorizeLanRead,
            AuthorizedExactTarget: sdk.AuthorizedExactTarget,
            SupervisorManagedBackupConfirm: confirm)).ConfigureAwait(false);

        Console.WriteLine($"Success={result.Success} SafetyBlocked={result.SafetyBlocked}");
        Console.WriteLine(result.Message);
        foreach (var s in result.Steps)
        {
            Console.WriteLine(
                $"[{(s.Ok ? "OK" : "FAIL")}]{(s.Blocked ? "[BLOCKED]" : "")} {s.Operation}: {s.Detail}");
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
        builder.Services.Configure<SqlAccountingWriteOptions>(
            builder.Configuration.GetSection(SqlAccountingWriteOptions.SectionName));
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
