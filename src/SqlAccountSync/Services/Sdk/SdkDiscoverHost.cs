using System.Text;
using Amazon;
using Amazon.SecretsManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Sdk;

/// <summary>CLI: dotnet run -- --sdk-discover --db=UAMAT</summary>
public static class SdkDiscoverHost
{
    public static async Task RunAsync(string[] args)
    {
        var dbCode = ReadArg(args, "--db") ?? "UAMAT";
        Console.WriteLine("SDK discover for company: " + dbCode);

        using var host = BuildHost();
        var session = host.Services.GetRequiredService<ISdkSessionManager>();
        var report = new StringBuilder();

        try
        {
            await session.UseCompanyAsync(dbCode, biz =>
            {
                report.AppendLine("IsLogin=" + (bool)biz.IsLogin);
                DiscoverMasters(biz, report);
                DiscoverDocuments(biz, report);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Discover failed: " + ComHelper.FormatException(ex));
            Environment.ExitCode = 1;
            return;
        }

        var outPath = Path.Combine(AppContext.BaseDirectory, $"sdk-discover-{dbCode}.txt");
        await File.WriteAllTextAsync(outPath, report.ToString()).ConfigureAwait(false);
        Console.WriteLine(report.ToString());
        Console.WriteLine("Wrote " + outPath);
    }

    private static void DiscoverMasters(dynamic biz, StringBuilder report)
    {
        foreach (var name in new[] { "AR_CUSTOMER", "AP_SUPPLIER", "GL_ACC", "ST_ITEM", "ST_GROUP", "ST_UOM" })
        {
            report.AppendLine();
            report.AppendLine("=== " + name + " ===");
            dynamic? obj = null;
            try
            {
                obj = biz.BizObjects.Find(name);
                if (obj is null)
                {
                    report.AppendLine("NOT FOUND");
                    continue;
                }

                try
                {
                    obj.New();
                    var main = obj.DataSets.Find(SdkObjectMap.MainDataSet);
                    var fields = ComHelper.TryEnumerateFields(main);
                    report.AppendLine("MainDataSet fields (" + fields.Length + "): " + string.Join(", ", fields.Take(80)));
                    foreach (var dsName in new[] { "cdsBranch", "BranchDataSet", "cdsItemUom", "ItemUomDataSet", "sdsbranch", "sdsitemuom" })
                    {
                        try
                        {
                            var ds = obj.DataSets.Find(dsName);
                            if (ds is null) continue;
                            var df = ComHelper.TryEnumerateFields(ds);
                            report.AppendLine("  Detail " + dsName + ": " + string.Join(", ", df.Take(40)));
                            ComHelper.SafeRelease(ds);
                        }
                        catch { /* skip */ }
                    }
                    try { obj.Cancel(); } catch { try { obj.Close(); } catch { } }
                }
                catch (Exception ex)
                {
                    report.AppendLine("Open/New failed: " + ComHelper.FormatException(ex));
                }
            }
            finally
            {
                ComHelper.SafeRelease(obj);
            }
        }
    }

    private static void DiscoverDocuments(dynamic biz, StringBuilder report)
    {
        foreach (var name in new[] { "PH_PI", "PH_GR", "PH_CN", "SL_IV", "SL_CN", "SL_DN", "SL_DO", "ST_XF" })
        {
            report.AppendLine();
            report.AppendLine("=== DOC " + name + " ===");
            dynamic? obj = null;
            try
            {
                obj = biz.BizObjects.Find(name);
                if (obj is null)
                {
                    report.AppendLine("NOT FOUND");
                    continue;
                }
                obj.New();
                var main = obj.DataSets.Find(SdkObjectMap.MainDataSet);
                report.AppendLine("Main: " + string.Join(", ", ComHelper.TryEnumerateFields(main).Take(60)));
                foreach (var dsName in ComHelper.UnverifiedDetailDataSetNames)
                {
                    try
                    {
                        var ds = obj.DataSets.Find(dsName);
                        if (ds is null) continue;
                        report.AppendLine("  Detail " + dsName + ": " + string.Join(", ", ComHelper.TryEnumerateFields(ds).Take(40)));
                        ComHelper.SafeRelease(ds);
                    }
                    catch { /* skip */ }
                }
                try { obj.Cancel(); } catch { try { obj.Close(); } catch { } }
            }
            catch (Exception ex)
            {
                report.AppendLine("Failed: " + ComHelper.FormatException(ex));
            }
            finally
            {
                ComHelper.SafeRelease(obj);
            }
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        ApplyEnv(builder);
        builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
        builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
        builder.Services.AddMemoryCache();
        var region = builder.Configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
        builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
            new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region)));
        builder.Services.AddSingleton<ISdkConnectionBundle, SdkConnectionBundle>();
        builder.Services.AddSingleton<ISdkSessionManager, SdkSessionManager>();
        return builder.Build();
    }

    private static void ApplyEnv(HostApplicationBuilder builder)
    {
        foreach (var envPath in new[]
                 {
                     Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                     Path.Combine(AppContext.BaseDirectory, ".env"),
                 })
        {
            if (File.Exists(envPath))
            {
                DotNetEnv.Env.Load(envPath);
                break;
            }
        }

        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var tenant = Environment.GetEnvironmentVariable("TENANT_CODE");
        if (!string.IsNullOrWhiteSpace(tenant))
            map["Sync:TenantCode"] = tenant;
        var region = Environment.GetEnvironmentVariable("AWS_REGION");
        if (!string.IsNullOrWhiteSpace(region))
            map["AWS:Region"] = region;
        var secret = Environment.GetEnvironmentVariable("SDK_SECRET_ID");
        if (!string.IsNullOrWhiteSpace(secret))
            map["Sync:SdkSecretId"] = secret;
        if (map.Count > 0)
            builder.Configuration.AddInMemoryCollection(map);
    }

    private static string? ReadArg(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim();
        }
        return null;
    }
}
