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
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Pages.Databases;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;

    public IndexModel(IDatabaseRegistry registry)
    {
        _registry = registry;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];

    public async Task OnGetAsync()
    {
        Databases = await _registry.ListAsync(includeInactive: IsAdmin);
        Links = await _registry.ListLinksAsync();
    }
}

public class LinkModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;
    private readonly ITenantDatabaseBootstrap _tenants;
    private readonly SyncOptions _sync;

    public LinkModel(
        IDatabaseRegistry registry,
        IAuditService audit,
        ITenantDatabaseBootstrap tenants,
        IOptions<SyncOptions> sync)
    {
        _registry = registry;
        _audit = audit;
        _tenants = tenants;
        _sync = sync.Value;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
    [BindProperty] public int MasterDbId { get; set; }
    [BindProperty] public List<int> SubDbIds { get; set; } = [];
    [BindProperty] public string ReturnTo { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Error { get; set; }
    public int? MaxDatabases { get; set; }
    public string LinkedActiveIds { get; set; } = "";
    private string TenantCode => (_sync.TenantCode ?? "").Trim();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsAdmin) return Forbid();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        var ids = await ActiveSubIdsAsync();
        if (ids.Count == 0)
            return await LinkFailedAsync("Choose at least one active Sub that is not the Master.");
        var capMessage = await OverLinkCapAsync(ids);
        if (capMessage is not null)
            return await LinkFailedAsync(capMessage);

        var added = await AddFreshLinksAsync(ids);
        if (added == 0)
            return await LinkFailedAsync("Those Master/Sub relationships already exist.");

        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER CREATED DBLINK", Result = "SUCCESS" });
        return FromDashboard ? RedirectToPage("/Index") : RedirectToPage("Index");
    }

    private bool FromDashboard =>
        string.Equals(ReturnTo, "dashboard", StringComparison.OrdinalIgnoreCase);

    private List<int> ChosenSubIds() =>
        SubDbIds.Where(id => id > 0 && id != MasterDbId).Distinct().ToList();

    private async Task<List<int>> ActiveSubIdsAsync()
    {
        var active = (await _registry.ListAsync()).Select(d => d.DbId).ToHashSet();
        if (!active.Contains(MasterDbId))
            return [];
        return ChosenSubIds().Where(active.Contains).ToList();
    }

    private async Task<string?> OverLinkCapAsync(List<int> ids)
    {
        var active = (await _registry.ListAsync()).Select(d => d.DbId).ToHashSet();
        var linked = (await _registry.ListLinksAsync())
            .SelectMany(link => new[] { link.MasterDbId, link.SubDbId })
            .Where(active.Contains);
        var max = await _tenants.GetMaxDatabasesAsync(TenantCode);
        return DatabasePlanLimit.NewLinksMessage(max, linked, MasterDbId, ids);
    }

    private async Task<int> AddFreshLinksAsync(List<int> ids)
    {
        var existing = (await _registry.ListLinksAsync())
            .Where(l => l.MasterDbId == MasterDbId)
            .Select(l => l.SubDbId)
            .ToHashSet();
        var added = 0;
        foreach (var subId in ids.Where(id => !existing.Contains(id)))
            added += await TryAddLinkAsync(subId);
        return added;
    }

    private async Task<int> TryAddLinkAsync(int subId)
    {
        try
        {
            await _registry.AddLinkAsync(MasterDbId, subId, CurrentEmail);
            return 1;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }

    private async Task<IActionResult> LinkFailedAsync(string message)
    {
        if (FromDashboard)
        {
            TempData["LinkError"] = message;
            return RedirectToPage("/Index");
        }
        Error = true;
        Message = message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Databases = await _registry.ListAsync();
        Links = await _registry.ListLinksAsync();
        MaxDatabases = await _tenants.GetMaxDatabasesAsync(TenantCode);
        var active = Databases.Select(d => d.DbId).ToHashSet();
        LinkedActiveIds = string.Join(",", Links
            .SelectMany(link => new[] { link.MasterDbId, link.SubDbId })
            .Where(active.Contains)
            .Distinct());
    }
}

public class EditModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;
    private readonly ISqlApiSecretProvisioner _secrets;
    private readonly ITenantDatabaseBootstrap _tenants;
    private readonly SyncOptions _sync;

    public EditModel(
        IDatabaseRegistry registry,
        IAuditService audit,
        ISqlApiSecretProvisioner secrets,
        ITenantDatabaseBootstrap tenants,
        IOptions<SyncOptions> sync)
    {
        _registry = registry;
        _audit = audit;
        _secrets = secrets;
        _tenants = tenants;
        _sync = sync.Value;
    }

    [BindProperty] public RegisteredDatabase Db { get; set; } = new();
    [BindProperty] public string ApiAccessKey { get; set; } = "";
    [BindProperty] public string ApiSecretKey { get; set; } = "";
    [BindProperty] public string ReturnTo { get; set; } = "";
    public string Message { get; set; } = "";
    public string TenantCode => (_sync.TenantCode ?? "").Trim();
    public bool AutoSqlApiCredRef => _sync.AutoAssignSqlApiCredRefByDbId && TenantCode.Length > 0;
    public string DefaultApiBaseUrl => "https://api.sql.my";
    public string SqlApiCredRefPlaceholder
    {
        get
        {
            var tenant = TenantCode.Length > 0 ? TenantCode : "{TENANT_CODE}";
            return $"proacc/tenant/{tenant}/{SqlApiCredRefConvention.BundleName}";
        }
    }
    public string SqlApiCredRefHint =>
        AutoSqlApiCredRef
            ? $"One secret for this tenant. The Code field selects that company's keys inside {SqlApiCredRefConvention.BuildBundleSecretId(TenantCode)}."
            : "Secrets Manager name, for example proacc/tenant/{TENANT_CODE}/sqlApiCredentials";

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!IsAdmin) return Forbid();
        if (IsCreatePage)
        {
            ApplyNewDefaults();
            PrepareDisplay();
            Message = await OverCapMessageAsync() ?? "";
            return Page();
        }
        if (id is not > 0)
            return RedirectToPage("/Databases/Create");
        var existing = await _registry.GetAsync(id.Value);
        if (existing is null) return RedirectToPage("Index");
        Db = existing;
        Db.DbRole = MasterOrSub(Db.DbRole);
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForDisplay(Db.ApiCredRef);
        return Page();
    }

    private bool IsCreatePage =>
        string.Equals(RouteData.Values["page"]?.ToString(), "/Databases/Create", StringComparison.OrdinalIgnoreCase);

    private void ApplyNewDefaults()
    {
        Db.DbRole = DatabaseRoles.Master;
        if (string.IsNullOrWhiteSpace(Db.ApiCredRef))
            Db.ApiCredRef = SqlApiCredRefPlaceholder;
        if (string.IsNullOrWhiteSpace(Db.FbHost))
            Db.FbHost = "localhost";
        if (string.IsNullOrWhiteSpace(Db.FbCredRef))
            Db.FbCredRef = "masterkey";
    }

    private void PrepareDisplay()
    {
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForDisplay(Db.ApiCredRef);
    }

    private static string MasterOrSub(string? role) =>
        string.Equals(role?.Trim(), DatabaseRoles.Sub, StringComparison.OrdinalIgnoreCase)
            ? DatabaseRoles.Sub
            : DatabaseRoles.Master;

    public IActionResult OnGetFind(string? name)
    {
        if (!IsAdmin) return Forbid();
        return new JsonResult(new { paths = DatabaseFileBrowser.FindByName(name) });
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsAdmin) return Forbid();
        if (ApiKeysArePartial())
            return DbFailed("Enter both the access key and the secret key, or leave both blank.");
        var capMessage = await OverCapMessageAsync();
        if (capMessage is not null)
            return DbFailed(capMessage);

        Db.DbRole = MasterOrSub(Db.DbRole);
        Db.CompanyName = (Db.DisplayName ?? "").Trim();
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForStorage(Db.ApiCredRef);
        var wasNew = Db.DbId == 0;
        var id = await _registry.SaveAsync(Db, CurrentEmail);
        await SaveApiKeysAsync(id);
        await WriteDatabaseAuditAsync(wasNew, id);
        return FromDashboard ? RedirectToPage("/Index") : RedirectToPage("Details", new { id });
    }

    private bool FromDashboard =>
        string.Equals(ReturnTo, "dashboard", StringComparison.OrdinalIgnoreCase);

    private IActionResult DbFailed(string message)
    {
        if (FromDashboard)
        {
            TempData["DbError"] = message;
            return RedirectToPage("/Index");
        }
        Message = message;
        PrepareForm();
        return Page();
    }

    private async Task<string?> OverCapMessageAsync()
    {
        if (Db.DbId != 0)
            return null;
        var max = await _tenants.GetMaxDatabasesAsync(TenantCode);
        var active = await _registry.ListAsync();
        return DatabasePlanLimit.NewDatabaseMessage(max, active.Count);
    }

    private bool ApiKeysArePartial()
    {
        var hasAccess = !string.IsNullOrWhiteSpace(ApiAccessKey);
        var hasSecret = !string.IsNullOrWhiteSpace(ApiSecretKey);
        return hasAccess != hasSecret;
    }

    private void PrepareForm()
    {
        Db.DbRole = MasterOrSub(Db.DbRole);
        Db.ApiBaseUrl = DefaultApiBaseUrl;
        Db.ApiCredRef = SqlApiCredRefConvention.ForDisplay(Db.ApiCredRef);
    }

    private async Task SaveApiKeysAsync(int id)
    {
        if (string.IsNullOrWhiteSpace(ApiAccessKey) || string.IsNullOrWhiteSpace(ApiSecretKey))
            return;
        var saved = await _registry.GetAsync(id);
        if (saved is null || string.IsNullOrWhiteSpace(saved.DbCode))
            return;
        var (ok, message) = await _secrets.SaveCredentialsAsync(
            _sync.TenantCode, saved.DbCode, ApiAccessKey, ApiSecretKey);
        if (!ok)
            TempData["Flash"] = message;
    }

    private Task WriteDatabaseAuditAsync(bool wasNew, int id) =>
        _audit.WriteAsync(new AuditEntry
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
}

public class DetailsModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IConnectionTestService _test;
    private readonly IAuditService _audit;
    private readonly ISqlApiCredentialBundle _bundle;
    private readonly SyncOptions _sync;

    public DetailsModel(
        IDatabaseRegistry registry,
        IConnectionTestService test,
        IAuditService audit,
        ISqlApiCredentialBundle bundle,
        IOptions<SyncOptions> sync)
    {
        _registry = registry;
        _test = test;
        _audit = audit;
        _bundle = bundle;
        _sync = sync.Value;
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
        var existing = await _registry.GetAsync(id);
        if (existing is not null)
            await _bundle.RemoveAsync(_sync.TenantCode, existing.DbCode);
        await _registry.DeactivateAsync(id, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry { UserEmail = CurrentEmail, Action = "USER DISABLED DATABASE", DbId = id, Result = "SUCCESS" });
        return RedirectToPage("Index");
    }
}
