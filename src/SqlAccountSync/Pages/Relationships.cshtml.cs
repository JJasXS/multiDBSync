using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Databases;

namespace SqlAccountSync.Pages;

public class RelationshipsModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;

    public RelationshipsModel(IDatabaseRegistry registry, IAuditService audit)
    {
        _registry = registry;
        _audit = audit;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
    [BindProperty] public int MasterDbId { get; set; }
    [BindProperty] public int SubDbId { get; set; }
    public string Message { get; set; } = "";
    public bool Error { get; set; }

    public async Task OnGetAsync()
    {
        Databases = await _registry.ListAsync();
        Links = await _registry.ListLinksAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        try
        {
            await _registry.AddLinkAsync(MasterDbId, SubDbId, CurrentEmail);
            await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED DBLINK", Result = "SUCCESS" });
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            Error = true;
            Message = ex.Message;
            await OnGetAsync();
            return Page();
        }
    }
}
