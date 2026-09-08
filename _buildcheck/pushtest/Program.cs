using Amazon;
using Amazon.SecretsManager;
using DotNetEnv;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Validation;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
foreach (var envPath in new[] { Path.Combine(root, ".env"), Path.Combine(root, "..", "src", "SqlAccountSync", ".env") })
{
    if (File.Exists(envPath))
    {
        Env.Load(envPath);
        break;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Environment.EnvironmentName = "Development";
builder.Configuration["CentralDb:Path"] = builder.Configuration["CentralDb:Path"] ?? @"C:\SQLData\SqlAccountSync\SYNC.FDB";
builder.Configuration["CentralDb:Host"] = builder.Configuration["CentralDb:Host"] ?? "localhost";
builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
builder.Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"));
builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(c =>
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"));
try
{
    var region = builder.Configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "ap-southeast-1";
    builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
        new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region)));
}
catch { /* optional */ }

builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
builder.Services.AddSingleton<CentralDb>();
builder.Services.AddSingleton<CompanyConnectionFactory>();
builder.Services.AddScoped<IDatabaseRegistry, DatabaseRegistry>();
builder.Services.AddScoped<ISqlAccountReadAdapter, FirebirdReadAdapter>();
builder.Services.AddScoped<IItemPrerequisiteService, ItemPrerequisiteService>();
builder.Services.AddScoped<ISqlAccountWriteAdapter>(sp => sp.GetRequiredService<SqlAccountApiWriteAdapter>());
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<IJobQueue, JobQueue>();
builder.Services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var sync = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
var mapping = scope.ServiceProvider.GetRequiredService<IMappingService>();

const int hq = 6;
const int s2 = 8;
var tests = new[] { ("Duckies", "API create (new on S2)"), ("Turtle", "API create (new on S2)") };
var queuedIds = new List<int>();

foreach (var (code, note) in tests)
{
    var (id, msg) = await sync.EnqueueMasterAsync(hq, s2, EntityCodes.Item, code, dryRun: false, "pushtest@local");
    Console.WriteLine($"Enqueue {code} ({note}): job #{id} - {msg}");
    if (id > 0)
    {
        queuedIds.Add(id);
        var job = await jobs.GetAsync(id);
        if (job is not null)
            await sync.ProcessJobAsync(job);
    }
}

if (queuedIds.Count == 0)
{
    Console.WriteLine("No jobs queued.");
    return;
}

foreach (var id in queuedIds)
{
    var job = await jobs.GetAsync(id);
    if (job is null) continue;
    Console.WriteLine($"Job #{id}: {job.Status} - {job.ErrorMessage}");
}

Console.WriteLine();
Console.WriteLine("HQ->S2 mappings:");
var maps = await mapping.ListAsync(hq, s2, EntityCodes.Item);
foreach (var m in maps.OrderBy(x => x.MasterCode))
    Console.WriteLine($"  {m.MasterCode} -> {m.SubCode} ({m.Status})");

sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "pushtest";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
