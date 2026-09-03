using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken(Order = 1001)] // handlers validate token manually for JSON fetch
public class LoginModel : PageModel
{
    private readonly OtpAuthService _auth;
    private readonly BootstrapOptions _bootstrap;
    private readonly SyncOptions _sync;
    private readonly IAntiforgery _antiforgery;

    public LoginModel(
        OtpAuthService auth,
        IOptions<BootstrapOptions> bootstrap,
        IOptions<SyncOptions> sync,
        IAntiforgery antiforgery)
    {
        _auth = auth;
        _bootstrap = bootstrap.Value;
        _sync = sync.Value;
        _antiforgery = antiforgery;
    }

    public string SuggestedEmail =>
        string.IsNullOrWhiteSpace(_bootstrap.AdminEmail) ? "" : _bootstrap.AdminEmail.Trim();

    public int OtpSeconds => Math.Clamp(_sync.OtpMinutes, 2, 30) * 60;

    public void OnGet() { }

    public async Task<IActionResult> OnPostSendAsync([FromForm] string email)
    {
        if (!await ValidateAntiForgeryAsync().ConfigureAwait(false))
            return new JsonResult(new { success = false, message = "Invalid session. Refresh and try again." });

        var (ok, msg, debugCode) = await _auth.SendOtpAsync(email ?? "");
        return new JsonResult(new
        {
            success = ok,
            message = msg,
            email = (email ?? "").Trim(),
            expiresInSeconds = OtpSeconds,
            resendCooldownSeconds = 30,
            debugOtp = debugCode
        });
    }

    public async Task<IActionResult> OnPostVerifyAsync([FromForm] string email, [FromForm] string code)
    {
        if (!await ValidateAntiForgeryAsync().ConfigureAwait(false))
            return new JsonResult(new { success = false, message = "Invalid session. Refresh and try again." });

        var (ok, user, msg) = await _auth.VerifyAsync(email ?? "", code ?? "");
        if (!ok || user is null)
            return new JsonResult(new { success = false, message = msg });

        await OtpAuthService.SignInAsync(HttpContext, user);
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
