using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages.Users;

[Authorize(Policy = PolicyNames.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IndexModel : SyncPageModel
{
    private readonly ILoginUserStore _users;
    private readonly IAuditService _audit;

    public IndexModel(ILoginUserStore users, IAuditService audit)
    {
        _users = users;
        _audit = audit;
    }

    public List<AppUser> Users { get; set; } = [];
    public string Message { get; set; } = "";
    public bool Error { get; set; }
    public int CurrentUserId => CurrentUser.UserId(User);
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Mobile { get; set; } = "";
    [BindProperty] public string Role { get; set; } = AppRoles.Viewer;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin)
            return Forbid();

        try
        {
            await _users.AddAsync(Email, DisplayName, AppRoles.Normalize(Role), Mobile);
            await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED USER", RecordId = Email, Result = "SUCCESS" });
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

    public async Task<IActionResult> OnPostSetActiveAsync(int id, bool active)
    {
        if (!IsAdmin)
            return Forbid();

        try
        {
            await SetActiveAsync(id, active);
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

    private async Task SetActiveAsync(int id, bool active)
    {
        var target = await _users.GetByIdAsync(id) ?? throw new InvalidOperationException("User not found.");
        if (!active && IsCurrentUser(target))
            throw new InvalidOperationException("You cannot turn off your own account.");

        if (active)
            await _users.SetActiveAsync(id, true);
        else
            await _users.SetActiveAsync(id, false);

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = active ? "USER ENABLED USER" : "USER DISABLED USER",
            RecordId = target.Email,
            Result = "SUCCESS"
        });
    }

    private bool IsCurrentUser(AppUser target) =>
        target.UserId == CurrentUserId
        || string.Equals(target.Email.Trim(), CurrentEmail.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync() => Users = await _users.ListAsync();
}
