using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SqlAccountSync.Authorization;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages;

public class WriteProviderModel : PageModel
{
    private readonly IWriteProviderSettings _settings;

    public WriteProviderModel(IWriteProviderSettings settings) => _settings = settings;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync(string provider, string? returnUrl)
    {
        if (!CurrentUser.IsAdmin(User))
            return Forbid();

        var next = SqlAccountingWriteProviders.IsSdk(provider)
            ? SqlAccountingWriteProviders.Sdk
            : SqlAccountingWriteProviders.Rest;
        await _settings.SetAsync(next).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
