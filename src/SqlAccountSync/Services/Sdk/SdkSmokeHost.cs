using Amazon;
using Amazon.SecretsManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// CLI smoke test: connect, create/update/delete a marked test customer on UAMAT.
/// Usage: dotnet run -- --sdk-smoke --db=UAMAT
/// </summary>
public static class SdkSmokeHost
{
    public static async Task RunAsync(string[] args)
    {
        var dbCode = ReadArg(args, "--db") ?? "UAMAT";
        var code = "SYNC-SDK-SMOKE";
        Console.WriteLine("SDK smoke test on " + dbCode + " using code " + code);

        using var host = BuildHost();
        var write = host.Services.GetRequiredService<SdkWriteAdapter>();
        var session = host.Services.GetRequiredService<ISdkSessionManager>();
        var db = new RegisteredDatabase { DbCode = dbCode, DisplayName = dbCode };

        try
        {
            var count = await session.UseCompanyAsync(dbCode, biz =>
            {
                object? ds = null;
                try
                {
                    ds = biz.DBManager.NewDataSet("SELECT COUNT(*) AS C FROM AR_CUSTOMER");
                    dynamic data = ds;
                    data.First();
                    return Convert.ToString(data.FindField("C").Value) ?? "?";
                }
                finally
                {
                    ComHelper.SafeRelease(ds);
                }
            }).ConfigureAwait(false);
            Console.WriteLine("Customer count before: " + count);

            var record = new MasterRecord
            {
                Code = code,
                Description = "SqlAccountSync SDK smoke test — safe to delete",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["companyname"] = "SqlAccountSync SDK smoke test — safe to delete",
                    ["creditterm"] = "30 Days",
                    ["status"] = "A"
                }
            };

            var create = await write.CreateMasterAsync(db, EntityCodes.Customer, record, "SMOKE|" + DateTime.UtcNow.ToString("o"))
                .ConfigureAwait(false);
            Console.WriteLine("Create: " + create.Success + " " + create.Message);

            record.Description = "SqlAccountSync SDK smoke test UPDATED";
            record.Fields["companyname"] = record.Description;
            var update = await write.UpdateMasterAsync(db, EntityCodes.Customer, record, "SMOKE-UPD", 0, 0)
                .ConfigureAwait(false);
            Console.WriteLine("Update: " + update.Success + " " + update.Message);

            var delete = await write.DeleteMasterAsync(db, EntityCodes.Customer, code).ConfigureAwait(false);
            Console.WriteLine("Delete: " + delete.Success + " " + delete.Message);

            var health = session.GetHealth();
            Console.WriteLine("Health: loggedIn=" + health.LoggedIn + " company=" + health.CurrentCompany);
            Console.WriteLine(create.Success && update.Success && delete.Success
                ? "SMOKE OK"
                : "SMOKE FAILED");
            if (!(create.Success && update.Success && delete.Success))
                Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Smoke failed: " + ComHelper.FormatException(ex));
            Environment.ExitCode = 1;
        }
        finally
        {
            await session.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
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

        builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
        builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
        builder.Services.AddMemoryCache();
        var awsRegion = builder.Configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
        builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
            new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(awsRegion)));
        builder.Services.AddSingleton<ISdkConnectionBundle, SdkConnectionBundle>();
        builder.Services.AddSingleton<ISdkSessionManager, SdkSessionManager>();
        builder.Services.AddSingleton<SdkWriteAdapter>();
        return builder.Build();
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
