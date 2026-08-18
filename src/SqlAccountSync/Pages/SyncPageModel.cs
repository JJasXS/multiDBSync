using Microsoft.AspNetCore.Mvc.RazorPages;
using SqlAccountSync.Services.Auth;

namespace SqlAccountSync.Pages;

public abstract class SyncPageModel : PageModel
{
    public string CurrentEmail => CurrentUser.Email(User);
    public string CurrentRole => CurrentUser.Role(User);
    public bool IsAdmin => CurrentUser.IsAdmin(User);
    public bool CanManage => CurrentUser.CanManage(User);
    public bool CanOperate => CurrentUser.CanOperate(User);
}
