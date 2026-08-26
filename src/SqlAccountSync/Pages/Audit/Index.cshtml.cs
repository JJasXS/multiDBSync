using Microsoft.AspNetCore.Mvc;

namespace SqlAccountSync.Pages.Audit;

public class IndexModel : SyncPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Settings/Audit");
}
