using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Connections;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Pages.Databases;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;

    public IndexModel(IDatabaseRegistry registry, IAuditService audit)
    {
        _registry = registry;
        _audit = audit;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
    public string Tab { get; set; } = "databases";
    [BindProperty] public int MasterDbId { get; set; }
    [BindProperty] public int SubDbId { get; set; }
    public string Message { get; set; } = "";
    public bool Error { get; set; }
    public bool ShowAddForm { get; set; }

    public async Task OnGetAsync(string? tab)
    {
        Tab = string.Equals(tab, "relationships", StringComparison.OrdinalIgnoreCase) ? "relationships" : "databases";
        Databases = await _registry.ListAsync(includeInactive: IsAdmin);
        Links = await _registry.ListLinksAsync();
    }

    public async Task<IActionResult> OnPostAddLinkAsync()
    {
        if (!IsAdmin) return Forbid();
        Tab = "relationships";
        try
        {
            await _registry.AddLinkAsync(MasterDbId, SubDbId, CurrentEmail);
            await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED DBLINK", Result = "SUCCESS" });
            return RedirectToPage(new { tab = "relationships" });
        }
        catch (InvalidOperationException ex)
        {
            Error = true;
            ShowAddForm = true;
            Message = ex.Message;
            Databases = await _registry.ListAsync(includeInactive: IsAdmin);
            Links = await _registry.ListLinksAsync();
            return Page();
        }
    }
}

public class EditModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;
    private readonly ISqlApiSecretProvisioner _secrets;
    private readonly SyncOptions _sync;

    public EditModel(
        IDatabaseRegistry registry,
        IAuditService audit,
        ISqlApiSecretProvisioner secrets,
        IOptions<SyncOptions> sync)
    {
        _registry = registry;
        _audit = audit;
        _secrets = secrets;
        _sync = sync.Value;
    }

    [BindProperty] public RegisteredDatabase Db { get; set; } = new();
    public string Message { get; set; } = "";
    public string TenantCode => (_sync.TenantCode ?? "").Trim();
    public bool AutoSqlApiCredRef => _sync.AutoAssignSqlApiCredRefByDbId && TenantCode.Length > 0;
    public string DefaultApiBaseUrl => "https://api.sql.my";
    public string SqlApiCredRefPlaceholder
    {
        get
        {
            var tenant = TenantCode.Length > 0 ? TenantCode : "{TENANT_CODE}";
            var code = SqlApiCredRefConvention.SanitizeDbCode(Db.DbCode);
            var dbPart = code.Length > 0 ? code : "{CODE}";
            return $"proacc/tenant/{tenant}/sqlApiCredentials/{dbPart}";
        }
    }
    public string SqlApiCredRefHint =>
        AutoSqlApiCredRef
            ? $"Secrets Manager name. Follows the Code field: proacc/tenant/{TenantCode}/sqlApiCredentials/{{CODE}}"
            : "Secrets Manager name, for example proacc/tenant/{TENANT_CODE}/sqlApiCredentials/{CODE}";

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!IsAdmin) return Forbid();
        if (id is > 0)
        {
            var existing = await _registry.GetAsync(id.Value);
            if (existing is null) return RedirectToPage("Index");
            Db = existing;
        }
        else
        {
            Db.DbRole = DatabaseRoles.Master;
            if (string.IsNullOrWhiteSpace(Db.ApiCredRef))
                Db.ApiCredRef = SqlApiCredRefPlaceholder;
            if (string.IsNullOrWhiteSpace(Db.FbHost))
                Db.FbHost = "localhost";
            if (string.IsNullOrWhiteSpace(Db.FbCredRef))
                Db.FbCredRef = "masterkey";
        }
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForDisplay(Db.ApiCredRef);
        return Page();
    }

    public IActionResult OnGetFind(string? name)
    {
        if (!IsAdmin) return Forbid();
        return new JsonResult(new { paths = DatabaseFileBrowser.FindByName(name) });
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        Db.CompanyName = (Db.DisplayName ?? "").Trim();
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForStorage(Db.ApiCredRef);
        var wasNew = Db.DbId == 0;
        var id = await _registry.SaveAsync(Db, CurrentEmail);
        var saved = await _registry.GetAsync(id);
        if (saved is not null
            && !string.IsNullOrWhiteSpace(_sync.TenantCode)
            && !string.IsNullOrWhiteSpace(saved.DbCode)
            && !string.IsNullOrWhiteSpace(saved.ApiCredRef)
            && saved.ApiCredRef.StartsWith("sm:", StringComparison.OrdinalIgnoreCase))
        {
            await _secrets.EnsurePlaceholderAsync(_sync.TenantCode, saved.DbCode);
        }

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = wasNew ? "USER CREATED DATABASE" : "USER UPDATED DATABASE",
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
