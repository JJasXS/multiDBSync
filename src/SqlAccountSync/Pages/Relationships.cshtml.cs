using Microsoft.AspNetCore.Mvc;

namespace SqlAccountSync.Pages;

public class RelationshipsModel : SyncPageModel
{
    public IActionResult OnGet() => RedirectToPage("/Databases/Index", new { tab = "relationships" });
}
