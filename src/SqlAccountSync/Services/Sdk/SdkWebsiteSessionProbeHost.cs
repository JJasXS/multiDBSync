using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// Development CLI that exercises the same ISqlAccSessionManager path as the Website Sdk write adapter.
/// Usage: dotnet run -- --sdk-website-session-probe
/// Sequence: MIRACLE → ONYAT → MIRACLE read-only SELECT. No CUD, no queue, no api.sql.my.
/// </summary>
public static class SdkWebsiteSessionProbeHost
{
    private static readonly Dictionary<string, string> FdbByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIRACLE"] = "ACC-MIRACLE.FDB",
        ["ONYAT"] = "ACC-ONYAT.FDB"
    };

    public static async Task RunAsync(string[] args)
    {
        LoadLocalEnv();
        SqlAccSdkPasswordStatus.TryPromoteEnvPasswordToProcess("env:SQLACC_SDK_PASSWORD", out _);

        var sequence = ParseSequence(args);
        using var host = BuildHost();
        var sessions = host.Services.GetRequiredService<ISqlAccSessionManager>();
        var writeOpts = host.Services.GetRequiredService<IOptions<SqlAccountingWriteOptions>>().Value;
        var sdkOpts = host.Services.GetRequiredService<IOptions<SqlAccSdkOptions>>().Value;

        Console.WriteLine(
            $"Website-equivalent session probe. Provider={writeOpts.Provider}; BlockRestHttp={writeOpts.BlockRestHttp}; " +
            $"AllowLiveCom={sdkOpts.AllowLiveCom}; manager={sessions.GetType().FullName}; sequence={string.Join("->", sequence)}");

        if (!writeOpts.ShouldRefuseRestHttp() || !SqlAccountingWriteProviders.IsSdk(writeOpts.Provider))
        {
            Console.Error.WriteLine("Refusing: expected Provider=Sdk and REST blocked for this probe.");
            Environment.ExitCode = 2;
            return;
        }

        var instances = new List<int>();
        var comInstances = new List<string>();
        var step = 0;
        foreach (var code in sequence)
        {
            step++;
            var db = new RegisteredDatabase
            {
                DbCode = code,
                DisplayName = code,
                FbPath = @"\\192.168.100.201\Share\" + FdbByCode[code]
            };

            Console.WriteLine($"--- step {step}: {code} ---");
            try
            {
                var result = await sessions.RunAsync(db, app =>
                {
                    if (!app.IsLogin)
                        throw new InvalidOperationException("IsLogin=false inside RunAsync.");
                    var instanceId = app is SqlAccComApp com
                        ? RuntimeHelpers.GetHashCode(com)
                        : RuntimeHelpers.GetHashCode(app);
                    var count = app.QueryRecordCount(SqlAccReadPocSafety.MinimalMetadataSql);
                    var diag = app.LastComDiagnostics;
                    return (instanceId, count, diag, app.GetType().FullName ?? "");
                }).ConfigureAwait(false);

                instances.Add(result.instanceId);
                var comInst = ExtractComInstance(result.diag);
                if (comInst is not null)
                    comInstances.Add(comInst);
                Console.WriteLine(
                    $"OK db={code} count={result.count} clrInstance={result.instanceId} comInstance={comInst} type={result.Item4}");
                Console.WriteLine("comDiag={" + result.diag + "}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL db={code}: {ex.GetType().Name}: {ex.Message}");
                Environment.ExitCode = 1;
                return;
            }
        }

        var reusedClr = instances.Count >= 2 && instances.Distinct().Count() == 1;
        var reusedCom = comInstances.Count >= 2 && comInstances.Distinct(StringComparer.Ordinal).Count() == 1;
        Console.WriteLine(
            $"CLR wrapper ids: {string.Join(" -> ", instances)}; sameWrapper={reusedClr}");
        Console.WriteLine(
            $"COM RCW instance tokens: {string.Join(" -> ", comInstances)}; sameComServerObject={reusedCom}");

        // Dispose manager to release COM at end of CLI (Website keeps it for process lifetime).
        if (sessions is IAsyncDisposable asyncDisp)
            await asyncDisp.DisposeAsync().ConfigureAwait(false);
        else if (sessions is IDisposable disp)
            disp.Dispose();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.Configure<SqlAccountingWriteOptions>(
            builder.Configuration.GetSection(SqlAccountingWriteOptions.SectionName));
        builder.Services.Configure<SqlAccSdkOptions>(
            builder.Configuration.GetSection(SqlAccSdkOptions.SectionName));
        builder.Services.PostConfigure<SqlAccountingWriteOptions>(opts =>
        {
            if (opts.BlockRestHttp)
                opts.Provider = SqlAccountingWriteProviders.Sdk;
        });
        builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddMemoryCache();
        builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
        builder.Services.AddSingleton<ISecretResolver, EnvOnlySecretResolver>();
        builder.Services.AddSingleton<ISdkConnectionBundle, SdkConnectionBundle>();
        builder.Services.AddSingleton<ISqlAccComAppFactory, SqlAccComAppFactory>();
        builder.Services.AddSingleton<ISqlAccSessionManager, SqlAccSessionManager>();
        return builder.Build();
    }

    private static string[] ParseSequence(string[] args)
    {
        var raw = ReadArg(args, "--sequence") ?? "MIRACLE,ONYAT,MIRACLE";
        var parts = raw.Split([',', ';', '>', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            parts = ["MIRACLE", "ONYAT", "MIRACLE"];
        foreach (var p in parts)
        {
            if (!FdbByCode.ContainsKey(p))
                throw new InvalidOperationException("Unauthorized or unknown db in sequence: " + p);
        }
        return parts;
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

    private static string? ExtractComInstance(string? diag)
    {
        if (string.IsNullOrWhiteSpace(diag))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(diag, @"instance=(\d+)");
        return m.Success ? m.Groups[1].Value : null;
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

    /// <summary>Resolves env: refs only — enough for SQLACC_SDK_PASSWORD in local labs.</summary>
    private sealed class EnvOnlySecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default)
        {
            var r = (secretRef ?? "").Trim();
            if (r.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                var name = r[4..].Trim();
                return Task.FromResult<string?>(Environment.GetEnvironmentVariable(name));
            }

            return Task.FromResult<string?>(null);
        }
    }
}
