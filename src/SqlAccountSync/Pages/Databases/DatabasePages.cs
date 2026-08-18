using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Connections;
using SqlAccountSync.Services.Databases;

namespace SqlAccountSync.Pages.Databases;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;

    public IndexModel(IDatabaseRegistry registry) => _registry = registry;

    public List<RegisteredDatabase> Databases { get; set; } = [];

    public async Task OnGetAsync() => Databases = await _registry.ListAsync(includeInactive: IsAdmin);
}

public class EditModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;

    public EditModel(IDatabaseRegistry registry, IAuditService audit)
    {
        _registry = registry;
        _audit = audit;
    }

    [BindProperty] public RegisteredDatabase Db { get; set; } = new();
    public string Message { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!IsAdmin) return Forbid();
        if (id is > 0)
        {
            var existing = await _registry.GetAsync(id.Value);
            if (existing is null) return RedirectToPage("Index");
            Db = existing;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        var id = await _registry.SaveAsync(Db, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = Db.DbId == 0 ? "USER CREATED DATABASE" : "USER UPDATED DATABASE",
            DbId = id,
            Entity = "DATABASE",
            RecordId = Db.DbCode,
            Result = "SUCCESS",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            RequestId = RequestId.Current(HttpContext)
        });
        return RedirectToPage("Details", new { id });
    }
}

public class DetailsModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IConnectionTestService _test;
    private readonly IAuditService _audit;

    public DetailsModel(IDatabaseRegistry registry, IConnectionTestService test, IAuditService audit)
    {
        _registry = registry;
        _test = test;
        _audit = audit;
    }

    public RegisteredDatabase? Db { get; set; }
    public List<DatabaseLink> Links { get; set; } = [];
    public string Message { get; set; } = "";
    public ConnectionTestResult? LastTest { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Db = await _registry.GetAsync(id);
        if (Db is null) return RedirectToPage("Index");
        Links = (await _registry.ListLinksAsync()).Where(l => l.MasterDbId == id || l.SubDbId == id).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync(int id)
    {
        if (!CanOperate) return Forbid();
        Db = await _registry.GetAsync(id);
        if (Db is null) return RedirectToPage("Index");
        LastTest = await _test.TestAsync(Db);
        await _registry.UpdateStatusAsync(id, LastTest.Status, LastTest.Success);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = "USER TESTED CONNECTION",
            DbId = id,
            Result = LastTest.Success ? "SUCCESS" : "FAIL",
            RequestId = RequestId.Current(HttpContext)
        });
        Message = LastTest.Message;
        Links = (await _registry.ListLinksAsync()).Where(l => l.MasterDbId == id || l.SubDbId == id).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(int id)
    {
        if (!IsAdmin) return Forbid();
        await _registry.DeactivateAsync(id, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER DISABLED DATABASE", DbId = id, Result = "SUCCESS" });
        return RedirectToPage("Index");
    }
}
