using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Options;

namespace SqlAccountSync.Pages;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly ILogger<LogoutModel> _logger;
    private readonly SyncOptions _sync;

    public LogoutModel(ILogger<LogoutModel> logger, IOptions<SyncOptions> sync)
    {
        _logger = logger;
        _sync = sync.Value;
    }

    public async Task<IActionResult> OnGet()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var poolsCleared = FirebirdPoolReleaser.ClearAllPools(_logger);

        if (_sync.IsDevAppMode)
        {
            TempData["LoginNotice"] = poolsCleared
                ? "Dev: signed out and Firebird connection pools cleared."
                : "Dev: signed out, but clearing Firebird pools failed. Check server logs.";
            TempData["LoginNoticeOk"] = poolsCleared ? "1" : "0";
        }

        return RedirectToPage("/Login");
    }
}
