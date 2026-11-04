using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// CLI: Login → SELECT COUNT(*) FROM RDB$DATABASE → Logout for MIRACLE/ONYAT only.
/// Usage: dotnet run -- --sdk-read-probe --db=MIRACLE
/// No CUD, no workers, no shared job queue.
/// </summary>
public static class SdkReadProbeHost
{
    private static readonly HashSet<string> AllowedDbCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIRACLE",
        "ONYAT"
    };

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
        if (!AllowedDbCodes.Contains(dbCode))
        {
            Console.Error.WriteLine(
                "SDK read probe refused: only MIRACLE or ONYAT are authorized. Got db=" + dbCode);
            Environment.ExitCode = 2;
            return;
        }

        using var host = BuildHost();
        var sdk = host.Services.GetRequiredService<IOptions<SqlAccSdkOptions>>().Value;
        var password = Environment.GetEnvironmentVariable("SQLACC_SDK_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("SDK read probe refused: SQLACC_SDK_PASSWORD is not set in process env.");
            Environment.ExitCode = 2;
            return;
        }

        var fdb = DbCodeToFdb[dbCode];
        var dcf = (sdk.DefaultDcfPath ?? "").Trim();
        if (dcf.Length == 0)
        {
            Console.Error.WriteLine("SDK read probe refused: SqlAccSdk:DefaultDcfPath is empty.");
            Environment.ExitCode = 2;
            return;
        }

        var probe = new SqlAccSdkReadProbe(
            host.Services.GetRequiredService<ISqlAccComAppFactory>());

        Console.WriteLine($"SDK read probe db={dbCode} fdb={fdb} (SELECT-only, no CUD).");
        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: dcf,
            DatabaseFileName: fdb,
            UserName: sdk.UserName ?? "",
            Password: password.Trim(),
            SelectSql: SqlAccReadPocSafety.MinimalMetadataSql,
            AuthorizeLanRead: sdk.AuthorizeLanRead,
            AuthorizedExactTarget: sdk.AuthorizedExactTarget)).ConfigureAwait(false);

        Console.WriteLine(
            $"Success={result.Success} SafetyBlocked={result.SafetyBlocked} LoginOk={result.LoginOk} " +
            $"ReadOk={result.ReadOk} LogoutOk={result.LogoutOk} Count={result.RecordCount} Wrote=False");
        Console.WriteLine(result.Message);
        if (!result.Success)
            Environment.ExitCode = 1;
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.Configure<SqlAccSdkOptions>(
            builder.Configuration.GetSection(SqlAccSdkOptions.SectionName));
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
            if (!File.Exists(envPath))
                continue;
            DotNetEnv.Env.Load(
                envPath,
                new DotNetEnv.LoadOptions(setEnvVars: true, clobberExistingVars: false));
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
