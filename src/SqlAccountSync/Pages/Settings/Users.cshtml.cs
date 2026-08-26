using Microsoft.AspNetCore.Mvc;

namespace SqlAccountSync.Pages.Settings;

public class UsersModel : SyncPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Users/Index");
}
