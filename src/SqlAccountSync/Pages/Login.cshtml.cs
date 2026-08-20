using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly OtpAuthService _auth;
    private readonly IHostEnvironment _env;
    private readonly SyncOptions _sync;
    private readonly BootstrapOptions _bootstrap;

    public LoginModel(
        OtpAuthService auth,
        IHostEnvironment env,
        IOptions<SyncOptions> sync,
        IOptions<BootstrapOptions> bootstrap)
    {
        _auth = auth;
        _env = env;
        _sync = sync.Value;
        _bootstrap = bootstrap.Value;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Sent { get; set; }
    public string DevAdminEmail => string.IsNullOrWhiteSpace(_bootstrap.AdminEmail) ? "admin@localhost" : _bootstrap.AdminEmail.Trim();
    public string DevMockCode => (_sync.MockSignInCode ?? "").Trim();
    public bool ShowLocalDevHint =>
        _env.IsDevelopment() && _sync.DebugOtpToConsole && !string.IsNullOrWhiteSpace(DevMockCode);

    public void OnGet() { }

    public async Task<IActionResult> OnPostSendAsync()
    {
        var (ok, msg) = await _auth.SendOtpAsync(Email);
        Message = msg;
        Sent = ok;
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        var (ok, user, msg) = await _auth.VerifyAsync(Email, Code);
        Message = msg;
        if (!ok || user is null)
            return Page();
        await OtpAuthService.SignInAsync(HttpContext, user);
        return RedirectToPage("/Index");
    }
}
