using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Partners;

public class CreateModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly IAuditService _audit;

    public CreateModel(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        IAuditService audit)
    {
        _registry = registry;
        _read = read;
        _write = write;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)] public int DbId { get; set; }
    [BindProperty(SupportsGet = true)] public string EntityCode { get; set; } = EntityCodes.Customer;

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string CreditTerm { get; set; } = "30 Days";
    [BindProperty] public string Address1 { get; set; } = "";
    [BindProperty] public string Address2 { get; set; } = "";
    [BindProperty] public string Address3 { get; set; } = "";
    [BindProperty] public string Address4 { get; set; } = "";
    [BindProperty] public string Phone1 { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Remark { get; set; } = "";

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<MasterRecord> Existing { get; set; } = [];
    public FlashToastModel? FlashToast { get; set; }

    public string EntityTitle =>
        string.Equals(EntityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase) ? "Supplier" : "Customer";

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!CanOperate) return Forbid();

        EntityCode = NormalizeEntity(EntityCode);
        await LoadAsync();

        var name = (CompanyName ?? "").Trim();
        if (name.Length == 0)
        {
            FlashToast = new FlashToastModel { Title = "Company name is required.", Ok = false };
            return Page();
        }

        // Server regenerates from company name (eQuotation-style); client preview may be stale.
        Code = PartnerCodeRules.SuggestNext(EntityCode, Existing.Select(r => r.Code), name);
        var code = Code;

        var format = PartnerCodeRules.Validate(code);
        if (!format.Ok)
        {
            FlashToast = new FlashToastModel { Title = format.Message, Ok = false };
            return Page();
        }

        var db = Databases.FirstOrDefault(d => d.DbId == DbId);
        if (db is null)
        {
            FlashToast = new FlashToastModel { Title = "Select a database.", Ok = false };
            return Page();
        }

        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
        {
            FlashToast = new FlashToastModel
            {
                Title = $"{db.DisplayName} has no SQL Accounting API credentials configured.",
                Ok = false
            };
            return Page();
        }

        if (await _read.MasterExistsByCodeAsync(db, EntityCode, code))
        {
            FlashToast = new FlashToastModel
            {
                Title = $"{EntityTitle} code '{code}' already exists on {db.DisplayName}.",
                Ok = false
            };
            return Page();
        }

        var stamp = $"MANUAL|{db.DbCode}|{EntityCode}|{code}|{DateTime.UtcNow:yyyyMMddHHmmss}";
        var record = BuildRecord(code, name);
        var write = await _write.CreateMasterAsync(db, EntityCode, record, stamp, db, EntityCode);

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = CurrentEmail,
            Action = $"CREATE_{EntityCode}",
            DbId = db.DbId,
            Entity = EntityCode,
            RecordId = code,
            Result = write.Success ? "SUCCESS" : "FAIL",
            AfterJson = write.Message ?? ""
        });

        if (write.Success)
        {
            FlashToast = new FlashToastModel
            {
                Title = $"Created {EntityTitle.ToLowerInvariant()} '{code}' on {db.DisplayName}.",
                Lines = string.IsNullOrWhiteSpace(write.Message) ? [] : [write.Message],
                Ok = true
            };
            ClearForm();
            await LoadExistingAsync(db);
            Code = PartnerCodeRules.SuggestNext(EntityCode, Existing.Select(r => r.Code), CompanyName);
        }
        else
        {
            FlashToast = new FlashToastModel
            {
                Title = $"Could not create {EntityTitle.ToLowerInvariant()}.",
                Lines = string.IsNullOrWhiteSpace(write.Message) ? [] : [write.Message],
                Ok = false
            };
        }

        return Page();
    }

    private async Task LoadAsync()
    {
        EntityCode = NormalizeEntity(EntityCode);
        Databases = (await _registry.ListAsync())
            .Where(d => d.IsActive)
            .OrderBy(d => d.DisplayName)
            .ToList();

        if (DbId == 0)
            DbId = Databases.FirstOrDefault()?.DbId ?? 0;

        var db = Databases.FirstOrDefault(d => d.DbId == DbId);
        if (db is not null)
            await LoadExistingAsync(db);

        Code = PartnerCodeRules.SuggestNext(EntityCode, Existing.Select(r => r.Code), CompanyName);
    }

    private async Task LoadExistingAsync(RegisteredDatabase db)
    {
        try
        {
            var list = string.Equals(EntityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
                ? await _read.GetSuppliersAsync(db)
                : await _read.GetCustomersAsync(db);
            Existing = list.OrderBy(r => r.Code).ToList();
        }
        catch
        {
            Existing = [];
        }
    }

    private MasterRecord BuildRecord(string code, string name)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["companyname"] = name,
            ["creditterm"] = string.IsNullOrWhiteSpace(CreditTerm) ? "30 Days" : CreditTerm.Trim(),
            ["area"] = "----",
            ["agent"] = "----",
            ["companycategory"] = "----",
            ["currencycode"] = "----",
            ["country"] = "MY",
            ["status"] = "A"
        };
        PutIfPresent(fields, "address1", Address1);
        PutIfPresent(fields, "address2", Address2);
        PutIfPresent(fields, "address3", Address3);
        PutIfPresent(fields, "address4", Address4);
        PutIfPresent(fields, "phone1", Phone1);
        PutIfPresent(fields, "email", Email);
        PutIfPresent(fields, "remark", Remark);

        return new MasterRecord
        {
            Code = code,
            Description = name,
            Key = code,
            Fields = fields
        };
    }

    private void ClearForm()
    {
        Code = "";
        CompanyName = "";
        Address1 = "";
        Address2 = "";
        Address3 = "";
        Address4 = "";
        Phone1 = "";
        Email = "";
        Remark = "";
        CreditTerm = "30 Days";
    }

    private static void PutIfPresent(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[key] = value.Trim();
    }

    private static string NormalizeEntity(string? entity) =>
        string.Equals(entity, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
            ? EntityCodes.Supplier
            : EntityCodes.Customer;
}
