using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlAccountSync.Pages.Partners;

/// <summary>Legacy URL; redirects into Master data Partners tab.</summary>
public class CreateModel : PageModel
{
    [BindProperty(SupportsGet = true)] public int DbId { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = "CUSTOMER";

    public IActionResult OnGet() =>
        RedirectToPage("/Accounts/Index", new { tab = "partners", entityCode = EntityCode, dbId = DbId });

    public IActionResult OnPost() => OnGet();
}
