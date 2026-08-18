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
using SqlAccountSync.Services.Connections;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;
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

builder.Services.Configure<CentralDbOptions>(builder.Configuration.GetSection(CentralDbOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.SectionName));
builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName));

var awsRegion = builder.Configuration["AWS:Region"] ?? "ap-southeast-1";
try
{
    builder.Services.AddSingleton<IAmazonSecretsManager>(_ =>
        new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(awsRegion)));
}
catch
{
    // AWS client optional
}

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<SqlAccountApiWriteAdapter>();
builder.Services.AddSingleton<ISecretResolver, SecretResolver>();
builder.Services.AddSingleton<CentralDb>();
builder.Services.AddSingleton<CompanyConnectionFactory>();
builder.Services.AddSingleton<SchemaBootstrap>();
builder.Services.AddScoped<IDatabaseRegistry, DatabaseRegistry>();
builder.Services.AddScoped<IUserDirectory, UserDirectory>();
builder.Services.AddScoped<OtpAuthService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<IConnectionTestService, ConnectionTestService>();
builder.Services.AddScoped<ISqlAccountReadAdapter, FirebirdReadAdapter>();
builder.Services.AddScoped<ISqlAccountWriteAdapter>(sp => sp.GetRequiredService<SqlAccountApiWriteAdapter>());
builder.Services.AddScoped<IMappingService, MappingService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<IJobQueue, JobQueue>();
builder.Services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
builder.Services.AddScoped<ITransactionEngine, TransactionEngine>();
builder.Services.AddHostedService<SyncWorker>();

var sessionHours = builder.Configuration.GetValue("Sync:SessionHours", 8);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(sessionHours, 1, 24));
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.Name = "SqlAccountSync.Auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PolicyNames.Admin, p => p.RequireRole(AppRoles.Admin));
    options.AddPolicy(PolicyNames.Manager, p => p.RequireRole(AppRoles.Admin, AppRoles.Manager));
    options.AddPolicy(PolicyNames.Operator, p => p.RequireRole(AppRoles.Admin, AppRoles.Manager, AppRoles.Operator));
    options.AddPolicy(PolicyNames.Viewer, p => p.RequireRole(AppRoles.Admin, AppRoles.Manager, AppRoles.Operator, AppRoles.Viewer));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/Users", PolicyNames.Admin);
    options.Conventions.AuthorizeFolder("/Settings", PolicyNames.Admin);
});

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
