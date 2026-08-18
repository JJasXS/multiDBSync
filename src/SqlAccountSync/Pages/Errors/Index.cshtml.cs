using Microsoft.AspNetCore.Mvc;

namespace SqlAccountSync.Pages.Errors;

public class IndexModel : SyncPageModel
{
    public IActionResult OnGet() => Redirect("/logs");
}
