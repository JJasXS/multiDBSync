using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Pages.Accounts;

public partial class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly ISyncOrchestrator _sync;
    private readonly IMappingService _mapping;
    private readonly IAuditService _audit;
    private readonly SqlAccountingWriteOptions _writeOptions;

    public IndexModel(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        ISyncOrchestrator sync,
        IMappingService mapping,
        IAuditService audit,
        IOptions<SqlAccountingWriteOptions> writeOptions)
    {
        _registry = registry;
        _read = read;
        _write = write;
        _sync = sync;
        _mapping = mapping;
        _audit = audit;
        _writeOptions = writeOptions.Value;
    }

    /// <summary>Configured write provider (Rest or Sdk). Used for company write-readiness checks.</summary>
    public string WriteProvider => _writeOptions.Provider ?? SqlAccountingWriteProviders.Rest;

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "accounts";
    [BindProperty(SupportsGet = true)] public int SourceDbId { get; set; }
    [BindProperty] public List<string> SelectedCodes { get; set; } = [];
    [BindProperty] public List<int> SelectedSubDbIds { get; set; } = [];

    [BindProperty] public string EditCode { get; set; } = "";
    [BindProperty] public string EditDescription { get; set; } = "";
    [BindProperty] public string EditDescription2 { get; set; } = "";
    [BindProperty] public string EditAccType { get; set; } = "";
    [BindProperty] public string EditSpecialAccType { get; set; } = "";
    [BindProperty] public string EditTax { get; set; } = "";
    [BindProperty] public int EditCashFlowType { get; set; }
    [BindProperty] public string EditSic { get; set; } = "";

    [BindProperty] public string DeleteCode { get; set; } = "";
    [BindProperty] public int DeleteMappingId { get; set; }
    [BindProperty] public int DeleteSubDbId { get; set; }

    [BindProperty(SupportsGet = true)] public int DbId { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = EntityCodes.Customer;
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string CreditTerm { get; set; } = PartnerMasterFactory.DefaultCreditTerm;
    [BindProperty] public string Address1 { get; set; } = "";
    [BindProperty] public string Address2 { get; set; } = "";
    [BindProperty] public string Address3 { get; set; } = "";
    [BindProperty] public string Address4 { get; set; } = "";
    [BindProperty] public string Phone1 { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Remark { get; set; } = "";
    [BindProperty] public bool PushToSubs { get; set; } = true;

    public List<RegisteredDatabase> MasterDatabases { get; set; } = [];
    public List<RegisteredDatabase> TargetSubs { get; set; } = [];
    public List<MasterRecord> Records { get; set; } = [];
    public RegisteredDatabase? SelectedSource { get; set; }
    public FlashToastModel? FlashToast { get; set; }
    public HashSet<string> NeedsUpdateCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PendingDeleteRow> PendingDeletes { get; set; } = [];

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<MasterRecord> Existing { get; set; } = [];
    public List<MasterRecord> CreditTerms { get; set; } = [];
    public List<RegisteredDatabase> SubTargets { get; set; } = [];
    public List<RegisteredDatabase> SubTargetsMissingApi { get; set; } = [];

    public bool IsPartnersTab =>
        string.Equals(NormalizeTab(Tab), "partners", StringComparison.OrdinalIgnoreCase);

    public string EntityTitle => PartnerMasterFactory.EntityTitle(EntityCode);

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = NormalizeTab(Tab);
        if (!IsPartnersTab)
            return RedirectToPage("/Push/Index", new { tab = "push", EntityCode = EntityCodes.Account, SourceDbId });

        await LoadPartnersAsync();
        return Page();
    }

    private static string NormalizeTab(string? tab) =>
        string.Equals(tab, "partners", StringComparison.OrdinalIgnoreCase) ? "partners" : "accounts";

    public sealed class PendingDeleteRow
    {
        public string Code { get; set; } = "";
        public int MappingId { get; set; }
        public int SubDbId { get; set; }
        public string SubDbCode { get; set; } = "";
        public string SubDisplayName { get; set; } = "";
    }
}
