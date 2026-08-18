using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages.Errors;

public class IndexModel : SyncPageModel
{
    private readonly IErrorLogService _errors;

    public IndexModel(IErrorLogService errors) => _errors = errors;

    public List<ErrorEntry> Items { get; set; } = [];

    public async Task OnGetAsync() => Items = await _errors.ListAsync();

    public async Task<IActionResult> OnPostResolveAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return RedirectToPage();
    }
}
