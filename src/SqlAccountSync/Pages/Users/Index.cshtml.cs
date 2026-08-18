using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages.Users;

public class IndexModel : SyncPageModel
{
    private readonly IUserDirectory _users;
    private readonly IAuditService _audit;

    public IndexModel(IUserDirectory users, IAuditService audit)
    {
        _users = users;
        _audit = audit;
    }

    public List<AppUser> Users { get; set; } = [];
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Role { get; set; } = AppRoles.Viewer;

    public async Task OnGetAsync() => Users = await _users.ListAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        await _users.SaveAsync(new AppUser { Email = Email, DisplayName = DisplayName, Role = Role });
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED USER", RecordId = Email, Result = "SUCCESS" });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync(int id)
    {
        if (!IsAdmin) return Forbid();
        await _users.DeactivateAsync(id);
        return RedirectToPage();
    }
}
