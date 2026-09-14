using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages.Users;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
    public string Message { get; set; } = "";
    public bool Error { get; set; }
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Role { get; set; } = AppRoles.Viewer;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await _users.SaveAsync(new AppUser { Email = Email, DisplayName = DisplayName, Role = Role });
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED USER", RecordId = Email, Result = "SUCCESS" });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync(int id)
    {
        try
        {
            await _users.DeactivateAsync(id);
            await _audit.WriteAsync(new AuditEntry
            {
                UserEmail = CurrentEmail,
                Action = "USER DISABLED USER",
                RecordId = id.ToString(),
                Result = "SUCCESS"
            });
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            Error = true;
            Message = ex.Message;
            await LoadAsync();
            return Page();
        }
    }

    private async Task LoadAsync() => Users = await _users.ListAsync();
}
