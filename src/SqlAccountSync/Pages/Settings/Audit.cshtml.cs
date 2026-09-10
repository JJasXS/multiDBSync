using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlAccountSync.Pages.Settings;

/// <summary>Legacy Settings route; audit logs now live on /logs.</summary>
public class AuditModel : PageModel
{
    public IActionResult OnGet() => Redirect("/logs#audit-logs");
}
