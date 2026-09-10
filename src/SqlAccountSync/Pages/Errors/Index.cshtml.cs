using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlAccountSync.Pages.Errors;

public class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/logs#error-logs");
}
