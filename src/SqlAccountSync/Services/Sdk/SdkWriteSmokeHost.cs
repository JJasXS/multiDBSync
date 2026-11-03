using Amazon;
using Amazon.SecretsManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Sdk;

/// <summary>CLI: create+delete a throwaway customer via SDK. Usage: --sdk-write-smoke --db=MIRACLE</summary>
public static class SdkWriteSmokeHost
{
    public static async Task RunAsync(string[] args)
    {
        var dbCode = ReadArg(args, "--db") ?? "MIRACLE";
        var code = "ZZ-SDK-" + DateTime.Now.ToString("HHmmss");
        Console.WriteLine($"SDK write smoke on {dbCode}: create customer {code}");

        using var host = BuildHost();
        var session = host.Services.GetRequiredService<ISdkSessionManager>();
        var write = host.Services.GetRequiredService<ISqlAccountWriteAdapter>();

        try
        {
            // 1) Raw COM path (wiki sample).
            await session.UseCompanyAsync(dbCode, biz =>
            {
                dynamic? obj = null;
                try
                {
                    obj = biz.BizObjects.Find("AR_CUSTOMER");
                    if (obj is null)
                        throw new InvalidOperationException("AR_CUSTOMER BizObject not found.");

                    obj.New();
                    var main = obj.DataSets.Find("MainDataSet");
                    main.FindField("CODE").AsString = code;
                    main.FindField("COMPANYNAME").AsString = "SDK Smoke " + code;

                    dynamic? branch = null;
                    try
                    {
                        branch = obj.DataSets.Find("cdsBranch");
                        if (branch is not null)
                        {
                            try { branch.Edit(); }
                            catch { branch.Append(); }
                            try { branch.FindField("BRANCHNAME").AsString = "BILLING"; }
                            catch { branch.FindField("BranchName").AsString = "BILLING"; }
                            try { branch.Post(); } catch { /* ignore */ }
                        }
                    }
                    finally
                    {
                        ComHelper.SafeRelease(branch);
                    }

                    Console.WriteLine("Calling Save()...");
                    obj.Save();
                    Console.WriteLine("Save OK.");
                    try { obj.Close(); } catch { /* ignore */ }
                }
                finally
                {
                    ComHelper.SafeRelease(obj);
                }

                dynamic? del = null;
                try
                {
                    del = biz.BizObjects.Find("AR_CUSTOMER");
                    object? key = null;
                    try { key = del.FindKeyByRef("CODE", code); }
                    catch { key = null; }
                    if (key is null)
                    {
                        Console.WriteLine("Created but FindKeyByRef could not find it for delete.");
                        return;
                    }
                    try { del.Params.Find("Code").Value = key; }
                    catch { del.Params.Find("CODE").Value = key; }
                    del.Open();
                    del.Delete();
                    del.Save();
                    try { del.Close(); } catch { /* ignore */ }
                    Console.WriteLine("Raw COM delete OK.");
                }
                finally
                {
                    ComHelper.SafeRelease(del);
                }
            }).ConfigureAwait(false);

            // 2) Adapter path used by master→sub push (full partner payload).
            var adapterCode = "ZZ-ADP-" + DateTime.Now.ToString("HHmmss");
            var db = new Models.RegisteredDatabase { DbCode = dbCode };
            var record = new Models.MasterRecord
            {
                Code = adapterCode,
                Description = "Adapter Smoke " + adapterCode,
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["companyname"] = "Adapter Smoke " + adapterCode,
                    ["controlaccount"] = "300-000"
                }
            };
            Console.WriteLine("Adapter CreateMaster " + adapterCode + "...");
            var created = await write.CreateMasterAsync(
                db, Models.EntityCodes.Customer, record, "SMOKE|" + adapterCode,
                sourceDb: db, sourceEntityCode: Models.EntityCodes.Customer).ConfigureAwait(false);
            Console.WriteLine("Adapter create: " + created.Success + " " + created.Message);
            if (!created.Success)
                throw new InvalidOperationException("Adapter create failed: " + created.Message);

            var deleted = await write.DeleteMasterAsync(db, Models.EntityCodes.Customer, adapterCode)
                .ConfigureAwait(false);
            Console.WriteLine("Adapter delete: " + deleted.Success + " " + deleted.Message);
            Console.WriteLine("Adapter smoke passed.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WRITE SMOKE FAILED: " + ComHelper.FormatException(ex));
            Environment.ExitCode = 1;
        }
        finally
        {
            try { await session.DisconnectAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        ApplyEnv(builder);
        builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
        builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
        builder.Services.AddMemoryCache();
        var region = builder.Configuration["AWS:Region"]
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? "ap-southeast-1";
        builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
            new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region)));
        builder.Services.AddSingleton<ISdkConnectionBundle, SdkConnectionBundle>();
        builder.Services.AddSingleton<ISdkSessionManager, SdkSessionManager>();
        builder.Services.AddSingleton<ISqlAccountWriteAdapter, SdkWriteAdapter>();
        builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        return builder.Build();
    }

    private static void ApplyEnv(HostApplicationBuilder builder)
    {
        foreach (var envPath in new[]
                 {
                     Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                     Path.Combine(AppContext.BaseDirectory, ".env"),
                     @"C:\Apps\SqlAccountSync\publish\.env",
                 })
        {
            if (!File.Exists(envPath)) continue;
            DotNetEnv.Env.Load(envPath);
            break;
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
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..];
        }
        return null;
    }
}
