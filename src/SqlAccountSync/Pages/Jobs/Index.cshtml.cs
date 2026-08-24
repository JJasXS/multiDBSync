using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages.Jobs;

public class IndexModel : SyncPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Push/Index", new { tab = "jobs" });
}
