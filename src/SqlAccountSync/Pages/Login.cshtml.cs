using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SqlAccountSync.Data;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly OtpAuthService _auth;
    private readonly CentralDb _db;
    private readonly IHostEnvironment _env;

    public LoginModel(OtpAuthService auth, CentralDb db, IHostEnvironment env)
    {
        _auth = auth;
        _db = db;
        _env = env;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Sent { get; set; }
    public bool ShowLocalDevHint => _env.IsDevelopment() && !_db.IsConfigured;

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
