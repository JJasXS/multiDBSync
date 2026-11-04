using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Connections;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Pages.Databases;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IAuditService _audit;
    private readonly ITenantDatabaseBootstrap _tenants;
    private readonly ISdkConnectionBundle _sdkBundle;
    private readonly SyncOptions _sync;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IDatabaseRegistry registry,
        IAuditService audit,
        ITenantDatabaseBootstrap tenants,
        ISdkConnectionBundle sdkBundle,
        IOptions<SyncOptions> sync,
        ILogger<IndexModel> logger)
    {
        _registry = registry;
        _audit = audit;
        _tenants = tenants;
        _sdkBundle = sdkBundle;
        _sync = sync.Value;
        _logger = logger;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];

    public async Task OnGetAsync()
    {
        Databases = await _registry.ListAsync(includeInactive: true);
        Links = await _registry.ListLinksAsync();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(int id, bool active, string scope = "")
    {
        if (!IsAdmin) return Forbid();
        var existing = await _registry.GetAsync(id);
        if (existing is null) return RedirectToPage();
        var capMessage = await ActivateCapMessageAsync(existing, active);
        if (capMessage is not null)
        {
            TempData["DbStatusError"] = capMessage;
            return RedirectToPage();
        }

        foreach (var dbId in await IdsToUpdateAsync(existing, active, scope))
            await SetOneAsync(dbId, active);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!IsAdmin) return Forbid();
        var existing = await _registry.GetAsync(id);
        if (existing is null) return RedirectToPage();
        try
        {
            await _registry.DeleteAsync(id);
        }
        catch (Exception)
        {
            TempData["DbStatusError"] = "Could not delete the company.";
            return RedirectToPage();
        }

        var (secretOk, secretMsg) = await _sdkBundle
            .RemoveCompanyAsync(existing.DbCode)
            .ConfigureAwait(false);
        if (!secretOk)
        {
            _logger.LogWarning(
                "Deleted company {Code} from registry but Secrets Manager remove failed: {Detail}",
                existing.DbCode, secretMsg);
            TempData["DbStatusError"] =
                $"Company removed locally, but AWS secret was not updated: {secretMsg}";
        }

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = "USER DELETED DATABASE",
            DbId = id,
            Entity = "DATABASE",
            RecordId = existing.DbCode,
            Result = secretOk ? "SUCCESS" : "PARTIAL",
            AfterJson = secretOk ? "" : secretMsg
        });
        return RedirectToPage();
    }

    private async Task<List<int>> IdsToUpdateAsync(RegisteredDatabase existing, bool active, string scope)
    {
        if (active || !DisableLinkedSubs(existing, scope))
            return [existing.DbId];
        var activeIds = (await _registry.ListAsync()).Select(d => d.DbId).ToHashSet();
        var subs = (await _registry.ListLinksAsync())
            .Where(link => link.MasterDbId == existing.DbId && activeIds.Contains(link.SubDbId))
            .Select(link => link.SubDbId)
            .Distinct()
            .ToList();
        subs.Insert(0, existing.DbId);
        return subs;
    }

    private static bool DisableLinkedSubs(RegisteredDatabase existing, string scope) =>
        string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
        && (existing.DbRole is DatabaseRoles.Master or DatabaseRoles.Both);

    private async Task SetOneAsync(int dbId, bool active)
    {
        await _registry.SetActiveAsync(dbId, active, CurrentEmail);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = active ? "USER ENABLED DATABASE" : "USER DISABLED DATABASE",
            DbId = dbId,
            Result = "SUCCESS"
        });
    }

    private async Task<string?> ActivateCapMessageAsync(RegisteredDatabase existing, bool active)
    {
        if (!active || existing.IsActive)
            return null;
        var tenant = (_sync.TenantCode ?? "").Trim();
        var max = await _tenants.GetMaxDatabasesAsync(tenant);
        var activeCount = (await _registry.ListAsync()).Count;
        return DatabasePlanLimit.NewDatabaseMessage(max, activeCount);
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

    public async Task<IActionResult> OnPostUnlinkAsync(int linkId)
    {
        if (!IsAdmin) return Forbid();
        if (linkId <= 0)
            return await LinkFailedAsync("Choose a relationship to unlink.");
        await RemoveLinkAsync(linkId);
        return FromDashboard ? RedirectToPage("/Index") : RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostUpdateLinksAsync(int[]? linkIds, int[]? keepLinkIds)
    {
        if (!IsAdmin) return Forbid();
        var keep = new HashSet<int>(keepLinkIds ?? []);
        foreach (var id in (linkIds ?? []).Where(id => id > 0 && !keep.Contains(id)).Distinct())
            await RemoveLinkAsync(id);
        return FromDashboard ? RedirectToPage("/Index") : RedirectToPage("Index");
    }

    private async Task RemoveLinkAsync(int linkId)
    {
        await _registry.DeactivateLinkAsync(linkId);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = "USER REMOVED DBLINK",
            RecordId = linkId.ToString(),
            Result = "SUCCESS"
        });
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
    private readonly ITenantDatabaseBootstrap _tenants;
    private readonly SyncOptions _sync;
    private readonly IWriteProviderSettings _writeProvider;
    private readonly ISdkConnectionBundle _sdkBundle;
    private readonly ISqlApiSecretProvisioner _apiSecrets;

    public EditModel(
        IDatabaseRegistry registry,
        IAuditService audit,
        ITenantDatabaseBootstrap tenants,
        IOptions<SyncOptions> sync,
        IWriteProviderSettings writeProvider,
        ISdkConnectionBundle sdkBundle,
        ISqlApiSecretProvisioner apiSecrets)
    {
        _registry = registry;
        _audit = audit;
        _tenants = tenants;
        _sync = sync.Value;
        _writeProvider = writeProvider;
        _sdkBundle = sdkBundle;
        _apiSecrets = apiSecrets;
    }

    [BindProperty] public RegisteredDatabase Db { get; set; } = new();
    [BindProperty] public string ReturnTo { get; set; } = "";
    [BindProperty] public string SdkUsername { get; set; } = "ADMIN";
    [BindProperty] public string SdkPassword { get; set; } = "";
    [BindProperty] public string SdkDcfPath { get; set; } = @"C:\eStream\SQLAccounting\Share\SQLACC.DCF";
    [BindProperty] public string SdkDatabase { get; set; } = "";
    [BindProperty] public string ApiAccessKey { get; set; } = "";
    [BindProperty] public string ApiSecretKey { get; set; } = "";
    [BindProperty] public string WriteMode { get; set; } = SqlAccountingWriteProviders.Sdk;
    public string Message { get; set; } = "";
    public bool IsSdkMode { get; private set; } = true;
    public string TenantCode => (_sync.TenantCode ?? "").Trim();
    public string SdkSecretHint =>
        TenantCode.Length > 0
            ? $"Saved into Secrets Manager: proacc/tenant/{TenantCode}/sdkConnections"
            : "Set TENANT_CODE so SDK connections resolve from Secrets Manager.";
    public string ApiSecretHint =>
        TenantCode.Length > 0
            ? $"Saved into Secrets Manager: proacc/tenant/{TenantCode}/sqlApiCredentials"
            : "Set TENANT_CODE so SQL API keys resolve from Secrets Manager.";

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!IsAdmin) return Forbid();
        await LoadWriteModeAsync().ConfigureAwait(false);
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
        await PrefillSdkFieldsAsync().ConfigureAwait(false);
        return Page();
    }

    private bool IsCreatePage =>
        string.Equals(RouteData.Values["page"]?.ToString(), "/Databases/Create", StringComparison.OrdinalIgnoreCase);

    private void ApplyNewDefaults()
    {
        Db.DbRole = DatabaseRoles.Master;
        ApplyFirebirdDefaults(Db);
    }

    private static void ApplyFirebirdDefaults(RegisteredDatabase db)
    {
        if (string.IsNullOrWhiteSpace(db.FbHost))
            db.FbHost = "localhost";
        if (db.FbPort <= 0)
            db.FbPort = 3050;
        if (string.IsNullOrWhiteSpace(db.FbUser))
            db.FbUser = "SYSDBA";
        if (string.IsNullOrWhiteSpace(db.FbCredRef))
            db.FbCredRef = "masterkey";
    }

    private void PrepareDisplay()
    {
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
        try
        {
            return await SavePostedDatabaseAsync();
        }
        catch (Exception ex)
        {
            return await DbFailed(DatabaseSaveError(ex));
        }
    }

    private async Task<IActionResult> SavePostedDatabaseAsync()
    {
        await ApplyPostedWriteModeAsync().ConfigureAwait(false);
        var capMessage = await OverCapMessageAsync();
        if (capMessage is not null)
            return await DbFailed(capMessage);

        Db.DbCode = (Db.DbCode ?? "").Trim();
        if (Db.DbCode.Length == 0)
            return await DbFailed("Company Code is required.");

        // Block duplicate Codes on create and on edit-to-another-code.
        var codeOwner = await _registry.FindByCodeAsync(Db.DbCode).ConfigureAwait(false);
        if (codeOwner is not null && codeOwner.DbId != Db.DbId)
            return await DbFailed($"Company Code '{Db.DbCode}' is already used. Choose a different Code.");

        string? previousCode = null;
        if (Db.DbId > 0)
        {
            var prior = await _registry.GetAsync(Db.DbId).ConfigureAwait(false);
            previousCode = prior?.DbCode?.Trim();
        }

        Db.DbRole = MasterOrSub(Db.DbRole);
        Db.CompanyName = (Db.DisplayName ?? "").Trim();
        ApplyFirebirdDefaults(Db);
        if (string.IsNullOrWhiteSpace(Db.ApiBaseUrl))
            Db.ApiBaseUrl = "https://api.sql.my";
        if (IsSdkMode && string.IsNullOrWhiteSpace(SdkDatabase) && !string.IsNullOrWhiteSpace(Db.FbPath))
            SdkDatabase = Path.GetFileName(Db.FbPath.Replace('/', '\\'));

        // Create + edit: upsert SDK/API credentials in Secrets Manager under the (new) Code.
        var secretError = await SaveModeCredentialsAsync().ConfigureAwait(false);
        if (secretError is not null)
            return await DbFailed(secretError);

        var wasNew = Db.DbId == 0;
        var id = await _registry.SaveAsync(Db, CurrentEmail);

        // If Code was renamed on edit, drop the old sdkConnections row.
        if (!string.IsNullOrWhiteSpace(previousCode)
            && !string.Equals(previousCode, Db.DbCode, StringComparison.OrdinalIgnoreCase)
            && IsSdkMode)
        {
            var (removed, removeMsg) = await _sdkBundle.RemoveCompanyAsync(previousCode).ConfigureAwait(false);
            if (!removed)
            {
                TempData["DbStatusError"] =
                    $"Saved, but could not remove old AWS SDK login '{previousCode}': {removeMsg}";
            }
        }

        await WriteDatabaseAuditAsync(wasNew, id);
        if (FromDashboard)
            return RedirectToPage("/Index");
        if (FromList)
            return RedirectToPage("/Databases/Index");
        return RedirectToPage("Details", new { id });
    }

    private async Task LoadWriteModeAsync()
    {
        var mode = await _writeProvider.GetAsync().ConfigureAwait(false);
        WriteMode = SqlAccountingWriteProviders.IsSdk(mode)
            ? SqlAccountingWriteProviders.Sdk
            : SqlAccountingWriteProviders.Rest;
        IsSdkMode = SqlAccountingWriteProviders.IsSdk(WriteMode);
    }

    private async Task ApplyPostedWriteModeAsync()
    {
        WriteMode = SqlAccountingWriteProviders.IsSdk(WriteMode)
            ? SqlAccountingWriteProviders.Sdk
            : SqlAccountingWriteProviders.Rest;
        IsSdkMode = SqlAccountingWriteProviders.IsSdk(WriteMode);
        await _writeProvider.SetAsync(WriteMode).ConfigureAwait(false);
    }

    private async Task PrefillSdkFieldsAsync()
    {
        var cred = await _sdkBundle.ResolveAsync(Db.DbCode).ConfigureAwait(false);
        if (cred is null)
            return;
        SdkUsername = string.IsNullOrWhiteSpace(cred.Username) ? "ADMIN" : cred.Username;
        SdkDcfPath = cred.DcfPath;
        SdkDatabase = cred.Database;
        SdkPassword = "";
    }

    private async Task<string?> SaveModeCredentialsAsync()
    {
        if (IsSdkMode)
            return await SaveSdkCredentialsAsync().ConfigureAwait(false);
        return await SaveApiCredentialsAsync().ConfigureAwait(false);
    }

    private async Task<string?> SaveSdkCredentialsAsync()
    {
        var code = (Db.DbCode ?? "").Trim();
        if (code.Length == 0)
            return "Company Code is required for SDK login.";

        var existing = await _sdkBundle.ResolveAsync(code).ConfigureAwait(false);
        var password = SdkPassword ?? "";
        if (string.IsNullOrWhiteSpace(password) && existing is not null)
            password = existing.Password;

        var dcf = string.IsNullOrWhiteSpace(SdkDcfPath) ? existing?.DcfPath ?? "" : SdkDcfPath.Trim();
        var database = string.IsNullOrWhiteSpace(SdkDatabase) ? existing?.Database ?? "" : SdkDatabase.Trim();
        if (string.IsNullOrWhiteSpace(dcf) || string.IsNullOrWhiteSpace(database))
            return "SDK DCF path and database file name are required.";
        if (string.IsNullOrWhiteSpace(password))
            return "SDK password is required (leave blank only when updating an existing company to keep the current password).";

        var (ok, message) = await _sdkBundle.SaveCompanyAsync(new SdkCompanyCredentials
        {
            Name = code,
            DcfPath = dcf,
            Database = database,
            Username = string.IsNullOrWhiteSpace(SdkUsername) ? "ADMIN" : SdkUsername.Trim(),
            Password = password
        }).ConfigureAwait(false);
        return ok ? null : message;
    }

    private async Task<string?> SaveApiCredentialsAsync()
    {
        var access = (ApiAccessKey ?? "").Trim();
        var secret = (ApiSecretKey ?? "").Trim();
        if (access.Length == 0 && secret.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(Db.ApiCredRef))
                return null;
            return "Access key and secret key are required for API mode.";
        }
        if (access.Length == 0 || secret.Length == 0)
            return "Access key and secret key are both required for API mode.";

        var (ok, message) = await _apiSecrets.SaveCredentialsAsync(TenantCode, Db.DbCode, access, secret)
            .ConfigureAwait(false);
        if (!ok)
            return message;

        Db.ApiCredRef = SqlApiCredRefConvention.BuildBundleCredRef(TenantCode);
        return null;
    }

    private static string DatabaseSaveError(Exception ex)
    {
        var text = ex.ToString();
        if (text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-803", StringComparison.Ordinal))
            return "A company with that code already exists.";

        // Surface the real cause (AWS / Firebird / validation) — path/code looking fine is not the check.
        var detail = (ex.GetBaseException().Message ?? "").Trim();
        if (detail.Length == 0)
            return "Could not save this company. Check the code and database path, then try again.";
        if (detail.Length > 220)
            detail = detail[..220] + "…";
        return "Could not save this company: " + detail;
    }

    private bool FromDashboard =>
        string.Equals(ReturnTo, "dashboard", StringComparison.OrdinalIgnoreCase);

    private bool FromDetails =>
        string.Equals(ReturnTo, "details", StringComparison.OrdinalIgnoreCase);

    private bool FromList =>
        string.Equals(ReturnTo, "list", StringComparison.OrdinalIgnoreCase);

    private async Task<IActionResult> DbFailed(string message)
    {
        if (FromDashboard)
        {
            TempData["DbError"] = message;
            return RedirectToPage("/Index");
        }
        if (FromList && Db.DbId > 0)
        {
            TempData["DbError"] = message;
            TempData["DbEditId"] = Db.DbId;
            return RedirectToPage("/Databases/Index");
        }
        if (FromDetails && Db.DbId > 0)
        {
            TempData["DbError"] = message;
            return RedirectToPage("Details", new { id = Db.DbId });
        }
        Message = message;
        await LoadWriteModeAsync().ConfigureAwait(false);
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

    private void PrepareForm()
    {
        Db.DbRole = MasterOrSub(Db.DbRole);
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

    public DetailsModel(
        IDatabaseRegistry registry,
        IConnectionTestService test,
        IAuditService audit)
    {
        _registry = registry;
        _test = test;
        _audit = audit;
    }

    public RegisteredDatabase? Db { get; set; }
    public List<DatabaseLink> Links { get; set; } = [];
    public string Message { get; set; } = "";
    public ConnectionTestResult? LastTest { get; set; }
    public int ActiveSubCount { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return RedirectToPage("Index");
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync(int id)
    {
        if (!CanOperate) return Forbid();
        if (!await LoadAsync(id) || Db is null) return RedirectToPage("Index");
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
        return Page();
    }

    private async Task<bool> LoadAsync(int id)
    {
        Db = await _registry.GetAsync(id);
        if (Db is null) return false;
        Links = (await _registry.ListLinksAsync()).Where(l => l.MasterDbId == id || l.SubDbId == id).ToList();
        var activeIds = (await _registry.ListAsync()).Select(d => d.DbId).ToHashSet();
        ActiveSubCount = Links.Count(l => l.MasterDbId == id && activeIds.Contains(l.SubDbId));
        return true;
    }
}
