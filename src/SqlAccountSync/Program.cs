using Amazon;
using Amazon.SecretsManager;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using SqlAccountSync.Authorization;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Connections;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Tenants;
using SqlAccountSync.Services.Transactions;
using SqlAccountSync.Services.Validation;

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

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// Standard ProAcc .env keys (same names as ApprovalPO / eQuotation).
var envOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var tenantFromEnv = (Environment.GetEnvironmentVariable("TENANT_CODE") ?? builder.Configuration["TENANT_CODE"])?.Trim();
if (!string.IsNullOrEmpty(tenantFromEnv))
{
    envOverrides["Sync:TenantCode"] = tenantFromEnv;
    envOverrides["TenantBootstrap:TenantCode"] = tenantFromEnv;
}

var regionFromEnv = (Environment.GetEnvironmentVariable("AWS_REGION")
    ?? Environment.GetEnvironmentVariable("AWS__Region")
    ?? builder.Configuration["AWS_REGION"])?.Trim();
if (!string.IsNullOrEmpty(regionFromEnv))
    envOverrides["AWS:Region"] = regionFromEnv;

var bootstrapUrlFromEnv = (Environment.GetEnvironmentVariable("TENANT_BOOTSTRAP_API_URL")
    ?? Environment.GetEnvironmentVariable("TenantBootstrap__AwsApiBaseUrl"))?.Trim();
if (!string.IsNullOrEmpty(bootstrapUrlFromEnv))
    envOverrides["TenantBootstrap:AwsApiBaseUrl"] = bootstrapUrlFromEnv;

if (envOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(envOverrides);

builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
builder.Services.Configure<CentralRegistryOptions>(builder.Configuration.GetSection(CentralRegistryOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.SectionName));
builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
builder.Services.Configure<FeatureOptions>(builder.Configuration.GetSection(FeatureOptions.SectionName));

var awsRegion = builder.Configuration["AWS:Region"]
    ?? Environment.GetEnvironmentVariable("AWS_REGION")
    ?? "ap-southeast-1";
try
{
    // Default credential chain picks up AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY from .env after DotNetEnv.Load.
    builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
        new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(awsRegion)));
}
catch
{
    // AWS client optional
}

builder.Services.AddMemoryCache();
var writeHttpTimeoutSeconds = Math.Clamp(
    builder.Configuration.GetValue("Sync:WriteHttpTimeoutSeconds", 30), 5, 300);
builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(writeHttpTimeoutSeconds);
    // SQL Accounting IntraWeb BFF rejects clients without a modern browser User-Agent (HTTP 500 HTML).
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
builder.Services.AddHttpClient<TenantEmailBootstrap>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<ITenantEmailBootstrap>(sp => sp.GetRequiredService<TenantEmailBootstrap>());
builder.Services.AddTransient<ITenantDatabaseBootstrap>(sp => sp.GetRequiredService<TenantEmailBootstrap>());
builder.Services.AddSingleton<TenantMenuFlags>();
builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
builder.Services.AddSingleton<ISqlApiCredentialBundle, SqlApiCredentialBundle>();
builder.Services.AddSingleton<ISqlApiSecretProvisioner, SqlApiSecretProvisioner>();
builder.Services.AddSingleton<CentralDb>();
builder.Services.AddSingleton<IRegistryReadGateway, RegistryReadGateway>();
builder.Services.AddSingleton<CompanyConnectionFactory>();
builder.Services.AddSingleton<SchemaBootstrap>();
builder.Services.AddScoped<IDatabaseRegistry, DatabaseRegistry>();
builder.Services.AddScoped<IUserDirectory, UserDirectory>();
builder.Services.AddScoped<ILoginDatabaseSource, LoginDatabaseSource>();
builder.Services.AddScoped<ILoginUserStore, LoginUserStore>();
builder.Services.AddScoped<ILoginUserResolver, LoginUserResolver>();
builder.Services.AddScoped<OtpAuthService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<IConnectionTestService, ConnectionTestService>();
builder.Services.AddScoped<ISqlAccountReadAdapter, FirebirdReadAdapter>();
builder.Services.AddScoped<IItemPrerequisiteService, ItemPrerequisiteService>();
builder.Services.AddScoped<IFirebirdPartyInsert, FirebirdPartyInsert>();
builder.Services.AddScoped<IFirebirdAccountInsert, FirebirdAccountInsert>();
builder.Services.AddScoped<IFirebirdPartyMutate, FirebirdPartyMutate>();
builder.Services.AddScoped<IFirebirdAccountMutate, FirebirdAccountMutate>();
builder.Services.AddScoped<ISqlAccountWriteAdapter>(sp => sp.GetRequiredService<SqlAccountApiWriteAdapter>());
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<IJobQueue, JobQueue>();
builder.Services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
builder.Services.AddScoped<ITransferMasterEnsure, TransferMasterEnsure>();
builder.Services.AddScoped<ITransactionEngine, TransactionEngine>();
builder.Services.AddScoped<IAccountAutoPushService, AccountAutoPushService>();
builder.Services.AddScoped<IAutoPushSettings, AutoPushSettings>();
builder.Services.AddScoped<IMasterMissingAutoPushService, MasterMissingAutoPushService>();
builder.Services.AddHostedService<TenantConfigRefreshWorker>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<AccountAutoPushWorker>();

var sessionHours = builder.Configuration.GetValue("Sync:SessionHours", 8);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        // Do not send forbidden users to Login — that looks like an instant logout.
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(sessionHours, 1, 24));
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.Name = "SqlAccountSync.Auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnValidatePrincipal = AuthCookieEvents.ValidatePrincipalAsync;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PolicyNames.Admin, p => p.RequireRole(AppRoles.Admin));
    options.AddPolicy(PolicyNames.Manager, p => p.RequireRole(AppRoles.Admin));
    options.AddPolicy(PolicyNames.Operator, p => p.RequireRole(AppRoles.Admin));
    options.AddPolicy(PolicyNames.Viewer, p => p.RequireRole(AppRoles.Admin, AppRoles.Viewer));
});

builder.Services.AddControllers();
var razorPages = builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");
});
if (builder.Environment.IsDevelopment())
    razorPages.AddRazorRuntimeCompilation();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers[RequestId.Header].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(id))
        id = Guid.NewGuid().ToString("N")[..12];
    ctx.Items[RequestId.ItemKey] = id;
    ctx.Response.Headers[RequestId.Header] = id;
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMenuMiddleware>();
app.MapGet("/health", async (IJobQueue queue) =>
{
    var snap = await queue.GetHealthAsync();
    var lease = builder.Configuration.GetValue("Sync:ProcessingLeaseSeconds", 900);
    var status = "ok";
    if (snap.OldestProcessingAgeSec is double age && age > Math.Max(lease, 60) * 2)
        status = "degraded";
    double? oldestAge = snap.OldestProcessingAgeSec is null
        ? null
        : Math.Round(snap.OldestProcessingAgeSec.Value, 1);
    return Results.Json(new
    {
        status,
        queue = new
        {
            pending = snap.Pending,
            processing = snap.Processing,
            failed = snap.Failed,
            oldestProcessingAgeSec = oldestAge
        }
    });
}).AllowAnonymous();
app.MapControllers();
app.MapRazorPages();

try
{
    using var scope = app.Services.CreateScope();
    var bootstrap = scope.ServiceProvider.GetRequiredService<SchemaBootstrap>();
    await bootstrap.EnsureAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Central database bootstrap skipped or failed. Configure CentralDb and restart.");
}

app.Run();

public partial class Program { }
