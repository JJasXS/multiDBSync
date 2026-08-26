using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;

namespace SqlAccountSync.Pages.Settings;

public class ErrorsModel : SyncPageModel
{
    private readonly IErrorLogService _errors;

    public ErrorsModel(IErrorLogService errors) => _errors = errors;

    public List<ErrorEntry> Errors { get; set; } = [];

    public async Task OnGetAsync() => Errors = await _errors.ListAsync();

    public async Task<IActionResult> OnPostResolveAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return RedirectToPage();
    }
}
