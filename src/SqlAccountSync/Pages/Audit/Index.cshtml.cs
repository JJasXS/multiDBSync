using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlAccountSync.Pages.Audit;

public class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/logs#audit-logs");
}
