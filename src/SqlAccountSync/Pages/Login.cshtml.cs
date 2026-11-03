using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken(Order = 1001)] // handlers validate token manually for JSON fetch
public class LoginModel : PageModel
{
    private readonly OtpAuthService _auth;
    private readonly BootstrapOptions _bootstrap;
    private readonly SyncOptions _sync;
    private readonly IAntiforgery _antiforgery;
    private readonly ITenantDatabaseBootstrap _tenantDb;
    private readonly IConfiguration _config;

    public LoginModel(
        OtpAuthService auth,
        IOptions<BootstrapOptions> bootstrap,
        IOptions<SyncOptions> sync,
        IAntiforgery antiforgery,
        ITenantDatabaseBootstrap tenantDb,
        IConfiguration config)
    {
        _auth = auth;
        _bootstrap = bootstrap.Value;
        _sync = sync.Value;
        _antiforgery = antiforgery;
        _tenantDb = tenantDb;
        _config = config;
    }

    public string SuggestedEmail =>
        string.IsNullOrWhiteSpace(_bootstrap.AdminEmail) ? "" : _bootstrap.AdminEmail.Trim();

    public int OtpSeconds => Math.Clamp(_sync.OtpMinutes, 2, 30) * 60;

    public int? MaxDatabases { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var tenant = (_config["TenantBootstrap:TenantCode"]
                      ?? _config["Sync:TenantCode"]
                      ?? Environment.GetEnvironmentVariable("TENANT_CODE")
                      ?? "").Trim();
        if (tenant.Length == 0)
            return;

        MaxDatabases = await _tenantDb.GetMaxDatabasesAsync(tenant, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostSendAsync([FromForm] string email)
    {
        if (!await ValidateAntiForgeryAsync().ConfigureAwait(false))
            return new JsonResult(new { success = false, message = "Invalid session. Refresh and try again." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (ok, msg, debugCode) = await _auth.SendOtpAsync(email ?? "", ip).ConfigureAwait(false);
        // Defense in depth: never put OTP on the wire unless the TCP peer is loopback.
        var reveal = debugCode is not null && HostHardening.IsLoopbackIp(ip) ? debugCode : null;
        return new JsonResult(new
        {
            success = ok,
            message = msg,
            email = (email ?? "").Trim(),
            expiresInSeconds = OtpSeconds,
            resendCooldownSeconds = OtpAuthService.SendCooldownSeconds,
            debugOtp = reveal
        });
    }

    public async Task<IActionResult> OnPostVerifyAsync([FromForm] string email, [FromForm] string code)
    {
        if (!await ValidateAntiForgeryAsync().ConfigureAwait(false))
            return new JsonResult(new { success = false, message = "Invalid session. Refresh and try again." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (ok, user, msg) = await _auth.VerifyAsync(email ?? "", code ?? "", ip).ConfigureAwait(false);
        if (!ok || user is null)
            return new JsonResult(new { success = false, message = msg });

        await OtpAuthService.SignInAsync(HttpContext, user).ConfigureAwait(false);
        return new JsonResult(new { success = true, message = msg, redirect = Url.Page("/Index") });
    }

    private async Task<bool> ValidateAntiForgeryAsync()
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
