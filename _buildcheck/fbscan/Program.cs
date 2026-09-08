using DotNetEnv;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlAccountSync.Data;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Secrets;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
foreach (var envPath in new[]
         {
             Path.Combine(root, ".env"),
             Path.Combine(root, "src", "SqlAccountSync", ".env"),
             @"C:\Users\sqlsupport\SqlAccountSync\.env"
         })
{
    if (!File.Exists(envPath)) continue;
    Env.Load(envPath);
    break;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Environment.EnvironmentName = "Development";
builder.Configuration["CentralDb:Path"] = builder.Configuration["CentralDb:Path"] ?? @"C:\SQLData\SqlAccountSync\SYNC.FDB";
builder.Configuration["CentralDb:Host"] = builder.Configuration["CentralDb:Host"] ?? "localhost";
builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
builder.Services.AddSingleton<IWebHostEnvironment>(new ScanHostEnvironment(root));
builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
builder.Services.AddSingleton<CentralDb>();
builder.Services.AddSingleton<CompanyConnectionFactory>();
builder.Services.AddScoped<IDatabaseRegistry, DatabaseRegistry>();

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var registry = scope.ServiceProvider.GetRequiredService<IDatabaseRegistry>();
var factory = scope.ServiceProvider.GetRequiredService<CompanyConnectionFactory>();

var hq = await registry.GetAsync(6);
var s2 = await registry.GetAsync(8);
if (hq is null || s2 is null)
{
    Console.WriteLine("HQ or S2 not found.");
    return;
}

foreach (var db in new[] { hq, s2 })
{
    Console.WriteLine($"=== {db.DbCode} {db.FbPath} ===");
    await using var conn = await factory.OpenAsync(db);
    foreach (var table in new[] { "ST_UOM", "UOM", "ST_ITEM_UOM", "ST_ITEMUOM", "ST_GROUP" })
    {
        if (!await TableExistsAsync(conn, table)) continue;
        var count = await ScalarAsync(conn, $"SELECT COUNT(*) FROM {table}");
        Console.WriteLine($"{table}: {count} rows");
        if (table.StartsWith("ST_ITEM", StringComparison.OrdinalIgnoreCase))
        {
            await using var cmd = new FbCommand($"SELECT FIRST 20 CODE, UOM FROM {table} ORDER BY CODE", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                Console.WriteLine($"  {r.GetString(0)} / {r.GetValue(1)}");
        }
        else if (table is "ST_UOM" or "UOM")
        {
            await using var cmd = new FbCommand($"SELECT FIRST 20 CODE, DESCRIPTION FROM {table}", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                Console.WriteLine($"  {r.GetValue(0)} / {r.GetValue(1)}");
        }
        else if (table == "ST_GROUP")
        {
            await using var cmd = new FbCommand("SELECT CODE FROM ST_GROUP", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                Console.WriteLine($"  group: {r.GetValue(0)}");
        }
    }

    foreach (var item in new[] { "Duckies", "Turtle", "Dogs" })
    {
        var exists = await ScalarAsync(conn, "SELECT COUNT(*) FROM ST_ITEM WHERE CODE = @c",
            new FbParameter("@c", item)) > 0;
        Console.WriteLine($"ST_ITEM {item}: {(exists ? "exists" : "missing")}");
    }
    Console.WriteLine();
}

static async Task<bool> TableExistsAsync(FbConnection conn, string table)
{
    await using var cmd = new FbCommand("SELECT COUNT(*) FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = @t", conn);
    cmd.Parameters.AddWithValue("@t", table.ToUpperInvariant());
    return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
}

static async Task<long> ScalarAsync(FbConnection conn, string sql, FbParameter? p = null)
{
    await using var cmd = new FbCommand(sql, conn);
    if (p is not null) cmd.Parameters.Add(p);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

sealed class ScanHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "fbscan";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
