using Amazon;
using Amazon.SecretsManager;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.SqlAccount.Sdk;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Tenants;
using SqlAccountSync.Services.Transactions;
using SqlAccountSync.Services.Validation;

// Load local .env without clobbering Process/User vars already set (e.g. SQLACC_SDK_PASSWORD).
foreach (var envPath in new[]
         {
             Path.Combine(Directory.GetCurrentDirectory(), ".env"),
             Path.Combine(AppContext.BaseDirectory, ".env"),
         })
{
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(
            envPath,
            new DotNetEnv.LoadOptions(setEnvVars: true, clobberExistingVars: false));
        break;
    }
}

// If SQLACC_SDK_PASSWORD (or configured PasswordSecretRef) exists only on User/Machine, promote to Process.
{
    var sdkPwdRef = Environment.GetEnvironmentVariable("SqlAccSdk__PasswordSecretRef")
                    ?? "env:SQLACC_SDK_PASSWORD";
    SqlAccountSync.Services.SqlAccount.Sdk.SqlAccSdkPasswordStatus.TryPromoteEnvPasswordToProcess(
        sdkPwdRef, out _);
}

// Optional master-branch SDK CLI hosts (discover / smoke). Website DI still uses Rest|Sdk provider selection.
if (args.Any(a => string.Equals(a, "--sdk-discover", StringComparison.OrdinalIgnoreCase)))
{
    await SdkDiscoverHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-write-smoke", StringComparison.OrdinalIgnoreCase)))
{
    await SdkWriteSmokeHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-smoke", StringComparison.OrdinalIgnoreCase)))
{
    await SdkSmokeHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-read-probe", StringComparison.OrdinalIgnoreCase)))
{
    await SdkReadProbeHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-website-session-probe", StringComparison.OrdinalIgnoreCase)))
{
    await SdkWebsiteSessionProbeHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-website-functional", StringComparison.OrdinalIgnoreCase)))
{
    await SdkWebsiteFunctionalHost.RunAsync(args).ConfigureAwait(false);
    return;
}

if (args.Any(a => string.Equals(a, "--sdk-live-cud", StringComparison.OrdinalIgnoreCase)))
{
    await SdkLiveCudHost.RunAsync(args).ConfigureAwait(false);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// Persist antiforgery/auth keys outside publish so redeploys do not invalidate browser cookies.
var dpKeysDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "SqlAccountSync", "dp-keys");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("SqlAccountSync");

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

var sdkSecretFromEnv = (Environment.GetEnvironmentVariable("SDK_SECRET_ID")
    ?? builder.Configuration["SDK_SECRET_ID"])?.Trim();
if (!string.IsNullOrEmpty(sdkSecretFromEnv))
    envOverrides["Sync:SdkSecretId"] = sdkSecretFromEnv;

var appModeFromEnv = (Environment.GetEnvironmentVariable("APP_MODE")
    ?? Environment.GetEnvironmentVariable("Sync__AppMode")
    ?? builder.Configuration["APP_MODE"]
    ?? builder.Configuration["Sync:AppMode"])?.Trim();
if (!string.IsNullOrEmpty(appModeFromEnv))
    envOverrides["Sync:AppMode"] = appModeFromEnv;

if (envOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(envOverrides);

builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
builder.Services.Configure<LoginDbOptions>(builder.Configuration.GetSection(LoginDbOptions.SectionName));
builder.Services.Configure<CentralRegistryOptions>(builder.Configuration.GetSection(CentralRegistryOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.SectionName));
builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));
builder.Services.Configure<FeatureOptions>(builder.Configuration.GetSection(FeatureOptions.SectionName));
builder.Services.Configure<SqlAccountingWriteOptions>(builder.Configuration.GetSection(SqlAccountingWriteOptions.SectionName));
builder.Services.Configure<SqlAccSdkOptions>(builder.Configuration.GetSection(SqlAccSdkOptions.SectionName));
// SDK-only labs: BlockRestHttp forces Provider=Sdk so DI never selects the REST write adapter.
builder.Services.PostConfigure<SqlAccountingWriteOptions>(opts =>
{
    if (opts.BlockRestHttp)
        opts.Provider = SqlAccountingWriteProviders.Sdk;
});

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
// Default in-process distributed cache; replace with Redis in DI for multi-instance rate limits.
builder.Services.AddDistributedMemoryCache();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeadersSetup.Configure(options, builder.Configuration));
var writeHttpTimeoutSeconds = Math.Clamp(
    builder.Configuration.GetValue("Sync:WriteHttpTimeoutSeconds", 30), 5, 300);
var sqlAccWriteSection = builder.Configuration.GetSection(SqlAccountingWriteOptions.SectionName);
var blockSqlAccRestHttp = sqlAccWriteSection.GetValue("BlockRestHttp", false)
    || SqlAccountingWriteProviders.IsSdk(sqlAccWriteSection["Provider"]);
HttpMessageHandler CreateSqlAccRestHandler() =>
    blockSqlAccRestHttp
        ? new BlockSqlAccountingRestHandler(new HttpClientHandler { UseCookies = false })
        : new HttpClientHandler { UseCookies = false };
builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(writeHttpTimeoutSeconds);
    // SQL Accounting IntraWeb BFF rejects clients without a modern browser User-Agent (HTTP 500 HTML).
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(CreateSqlAccRestHandler);
builder.Services.AddHttpClient<ISqlAccountApiClient, SqlAccountApiClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(CreateSqlAccRestHandler);
builder.Services.AddHttpClient<TenantEmailBootstrap>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<ITenantEmailBootstrap>(sp => sp.GetRequiredService<TenantEmailBootstrap>());
builder.Services.AddTransient<ITenantDatabaseBootstrap>(sp => sp.GetRequiredService<TenantEmailBootstrap>());
builder.Services.AddSingleton<TenantMenuFlags>();
builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
builder.Services.AddSingleton<OtpHashPepper>();
builder.Services.AddSingleton<ISqlApiCredentialBundle, SqlApiCredentialBundle>();
builder.Services.AddSingleton<ISqlApiSecretProvisioner, SqlApiSecretProvisioner>();
// Master SDK connection/session helpers (CLI smoke /health/detail). Website writes use SqlAccountingWrite:Provider.
builder.Services.AddSingleton<ISdkConnectionBundle, SdkConnectionBundle>();
builder.Services.AddSingleton<ISdkSessionManager, SdkSessionManager>();builder.Services.AddSingleton<CentralDb>();
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
builder.Services.AddSingleton<ISqlAccComAppFactory, SqlAccComAppFactory>();
builder.Services.AddSingleton<ISqlAccSessionManager, SqlAccSessionManager>();
builder.Services.AddScoped<ISqlAccSessionDiagnostics, SqlAccSessionDiagnostics>();
builder.Services.AddScoped<SqlAccountSdkWriteAdapter>();
builder.Services.AddScoped<ISqlAccountWriteAdapter>(sp =>
{
    var provider = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlAccountingWriteOptions>>()
        .Value.Provider?.Trim();
    if (string.Equals(provider, SqlAccountingWriteProviders.Sdk, StringComparison.OrdinalIgnoreCase))
        return sp.GetRequiredService<SqlAccountSdkWriteAdapter>();
    return sp.GetRequiredService<SqlAccountApiWriteAdapter>();
});
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<IJobQueue, JobQueue>();
builder.Services.AddScoped<IDevSingleJobProcessor, DevSingleJobProcessor>();
builder.Services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
builder.Services.AddScoped<ITransferMasterEnsure, TransferMasterEnsure>();
builder.Services.AddScoped<ITransactionEngine, TransactionEngine>();
builder.Services.AddScoped<IAccountAutoPushService, AccountAutoPushService>();
builder.Services.AddScoped<IAutoPushSettings, AutoPushSettings>();
builder.Services.AddSingleton<IAutoPushSchedule, AutoPushSchedule>();
builder.Services.AddScoped<IMasterMissingAutoPushService, MasterMissingAutoPushService>();
builder.Services.AddHostedService<TenantConfigRefreshWorker>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<AccountAutoPushWorker>();

var sessionHours = builder.Configuration.GetValue("Sync:SessionHours", 8);
var cookieSecure = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
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
        // Production: HTTPS-only auth cookie (requires TLS or a trusted proxy setting X-Forwarded-Proto).
        options.Cookie.SecurePolicy = cookieSecure;
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
HostHardening.EnsureProductionAllowedHosts(app.Environment, app.Configuration, app.Logger);

app.UseForwardedHeaders();

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

if (app.Environment.IsDevelopment())
{
    var features = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeatureOptions>>().Value;
    if (features.ForceShowDocumentTransferUi)
    {
        var menu = app.Services.GetRequiredService<TenantMenuFlags>();
        menu.ApplyTenantPlan(menu.ShowSynchronization, menu.ShowDocumentTransfer, forceShowDocumentTransfer: true);
        app.Logger.LogInformation(
            "Development: Document Transfer menu forced on (Features:ForceShowDocumentTransferUi).");
    }
}
// Public liveness only — no queue/SDK internals (master security hardening).
app.MapGet("/health", () => Results.Json(new { status = "ok" })).AllowAnonymous();
// Detailed health for authenticated admins.
app.MapGet("/health/detail", async (HttpContext http, IJobQueue queue, ISdkSessionManager sdk, IConfiguration config) =>
{
    var snap = await queue.GetHealthAsync();
    var lease = config.GetValue("Sync:ProcessingLeaseSeconds", 900);
    var sdkHealth = sdk.GetHealth();
    var status = "ok";
    if (!sdkHealth.ProgIdRegistered)
        status = "sdk-offline";
    else if (snap.OldestProcessingAgeSec is double age && age > Math.Max(lease, 60) * 2)
        status = "degraded";
    double? oldestAge = snap.OldestProcessingAgeSec is null
        ? null
        : Math.Round(snap.OldestProcessingAgeSec.Value, 1);
    return Results.Json(new
    {
        status,
        requestId = RequestId.Current(http),
        sdk = new
        {
            progIdRegistered = sdkHealth.ProgIdRegistered,
            loggedIn = sdkHealth.LoggedIn,
            currentCompany = sdkHealth.CurrentCompany
        },
        queue = new
        {
            pending = snap.Pending,
            processing = snap.Processing,
            failed = snap.Failed,
            oldestProcessingAgeSec = oldestAge
        }
    });
}).RequireAuthorization(PolicyNames.Admin);
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

try
{
    using var scope = app.Services.CreateScope();
    var diag = scope.ServiceProvider.GetRequiredService<ISqlAccSessionDiagnostics>();
    var status = diag.GetProviderStatus();
    var writeOpts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlAccountingWriteOptions>>().Value;
    app.Logger.LogInformation(
        "Write provider effective={Provider}; SdkSelected={Sdk}; BlockRestHttp={BlockRest}; AllowLiveCom={Live}; PasswordConfigured={Pwd}; BackgroundWriteWorkers={Workers}; AllowDevSingleJobProcess={DevJob}; {Message}",
        status.WriteProvider,
        status.SdkSelected,
        writeOpts.BlockRestHttp,
        status.AllowLiveCom,
        status.PasswordConfigured,
        status.BackgroundWriteWorkersEnabled,
        app.Configuration.GetValue("Sync:AllowDevSingleJobProcess", false),
        status.Message);
    if (writeOpts.ShouldRefuseRestHttp())
    {
        app.Logger.LogInformation(
            "SDK-only SQL Accounting mode: REST adapter refused and HttpClient handler blocks api.sql.my.");
    }
}
catch (Exception ex)
{
    app.Logger.LogDebug(ex, "SDK provider status log skipped.");
}

try
{
    var syncOpts = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncOptions>>().Value;
    var schedule = app.Services.GetRequiredService<IAutoPushSchedule>();
    var seconds = Math.Clamp(syncOpts.AccountAutoPushSeconds <= 0 ? 60 : syncOpts.AccountAutoPushSeconds, 15, 3600);
    schedule.SetIntervalSeconds(seconds);
    schedule.ArmNextFromNow();
}
catch (Exception ex)
{
    app.Logger.LogDebug(ex, "Auto-push schedule arm skipped.");
}

app.Run();

public partial class Program { }
