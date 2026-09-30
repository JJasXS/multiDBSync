using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;

namespace SqlAccountSync.Pages.Settings;

[Authorize(Policy = PolicyNames.Admin)]
public class UsersModel : SyncPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Users/Index");
}
