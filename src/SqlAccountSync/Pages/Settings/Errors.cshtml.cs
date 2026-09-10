using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlAccountSync.Pages.Settings;

/// <summary>Legacy Settings route; error logs now live on /logs.</summary>
public class ErrorsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/logs#error-logs");

    public IActionResult OnPost() => OnGet();
}
