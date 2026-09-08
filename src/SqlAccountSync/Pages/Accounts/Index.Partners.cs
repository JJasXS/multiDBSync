using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;

namespace SqlAccountSync.Pages.Accounts;

public partial class IndexModel
{
    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!CanOperate) return Forbid();

        Tab = "partners";
        EntityCode = PartnerMasterFactory.NormalizeEntity(EntityCode);
        await LoadPartnersAsync();

        var name = (CompanyName ?? "").Trim();
        if (name.Length == 0)
            return PartnersPage(FlashToasts.Fail("Company name is required."));

        Code = PartnerCodeRules.SuggestNext(EntityCode, Existing.Select(r => r.Code), name);
        var code = Code;

        var db = Databases.FirstOrDefault(d => d.DbId == DbId);
        var err = await ValidateCreateAsync(db, code);
        if (err is not null)
            return PartnersPage(err);

        var stamp = $"MANUAL|{db!.DbCode}|{EntityCode}|{code}|{DateTime.UtcNow:yyyyMMddHHmmss}";
        var record = PartnerMasterFactory.Build(
            code, name, CreditTerm, Address1, Address2, Address3, Address4, Phone1, Email, Remark);
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

        if (!write.Success)
        {
            return PartnersPage(FlashToasts.Fail(
                $"Could not create {EntityTitle.ToLowerInvariant()}.",
                string.IsNullOrWhiteSpace(write.Message) ? null : [write.Message]));
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(write.Message))
            lines.Add(write.Message);
        if (PushToSubs && SubTargets.Count > 0)
            lines.AddRange(await EnqueueSubPushAsync(db, code));

        FlashToast = FlashToasts.Ok(
            $"Created {EntityTitle.ToLowerInvariant()} '{code}' on {db.DisplayName}.", lines);
        ClearPartnerForm();
        await LoadExistingPartnersAsync(db);
        RefreshSuggestedCode();
        return Page();
    }

    private async Task LoadPartnersAsync()
    {
        EntityCode = PartnerMasterFactory.NormalizeEntity(EntityCode);
        Databases = (await _registry.ListAsync())
            .Where(d => d.IsActive)
            .OrderBy(d => d.DisplayName)
            .ToList();

        if (DbId == 0)
            DbId = Databases.FirstOrDefault()?.DbId ?? 0;

        var db = Databases.FirstOrDefault(d => d.DbId == DbId);
        if (db is null)
        {
            Existing = [];
            CreditTerms = [];
            SubTargets = [];
            SubTargetsMissingApi = [];
            RefreshSuggestedCode();
            return;
        }

        await LoadExistingPartnersAsync(db);
        await LoadCreditTermsAsync(db);
        await LoadPartnerSubTargetsAsync(db);
        RefreshSuggestedCode();
    }

    private async Task<FlashToastModel?> ValidateCreateAsync(RegisteredDatabase? db, string code)
    {
        var format = PartnerCodeRules.Validate(code);
        if (!format.Ok)
            return FlashToasts.Fail(format.Message);

        if (db is null)
            return FlashToasts.Fail("Select a database.");

        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return FlashToasts.Fail($"{db.DisplayName} has no SQL Accounting API credentials configured.");

        if (await _read.MasterExistsByCodeAsync(db, EntityCode, code))
            return FlashToasts.Fail($"{EntityTitle} code '{code}' already exists on {db.DisplayName}.");

        return null;
    }

    private async Task<List<string>> EnqueueSubPushAsync(RegisteredDatabase db, string code)
    {
        var lines = new List<string>();
        try
        {
            var (queued, failed, messages) = await _sync.EnqueueMasterBatchAsync(
                db.DbId,
                SubTargets.Select(s => s.DbId).ToList(),
                EntityCode,
                [code],
                dryRun: false,
                CurrentEmail);

            var names = string.Join(", ", SubTargets.Select(s => s.DisplayName));
            if (queued > 0)
                lines.Add($"Queued sync to {names}. Records appear on subs once the worker runs.");
            if (failed > 0)
                lines.Add($"Could not queue {failed} sub sync job(s).");
            lines.AddRange(messages.Where(m => !string.IsNullOrWhiteSpace(m)).Take(5));

            await _audit.WriteAsync(new AuditEntry
            {
                UserEmail = CurrentEmail,
                Action = $"AUTO_PUSH_{EntityCode}",
                DbId = db.DbId,
                Entity = EntityCode,
                RecordId = code,
                Result = failed == 0 ? "QUEUED" : "PARTIAL",
                AfterJson = $"queued={queued};failed={failed};targets={names}"
            });
        }
        catch (Exception ex)
        {
            lines.Add($"Auto-push to subs failed to enqueue: {ex.Message}");
        }

        return lines;
    }

    private async Task LoadPartnerSubTargetsAsync(RegisteredDatabase db)
    {
        var links = await _registry.ListLinksAsync();
        SubTargets = PartnerAutoPushTargets.Resolve(db, Databases, links);
        SubTargetsMissingApi = PartnerAutoPushTargets.ResolveSkippedMissingApi(db, Databases, links);
    }

    private async Task LoadExistingPartnersAsync(RegisteredDatabase db)
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

    private async Task LoadCreditTermsAsync(RegisteredDatabase db)
    {
        try
        {
            CreditTerms = (await _read.GetTermsAsync(db))
                .OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            CreditTerms = [];
        }

        CreditTerm = PartnerMasterFactory.PickCreditTerm(CreditTerms, CreditTerm);
    }

    private void ClearPartnerForm()
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
        CreditTerm = PartnerMasterFactory.DefaultCreditTerm;
        PushToSubs = true;
    }

    private void RefreshSuggestedCode() =>
        Code = PartnerCodeRules.SuggestNext(EntityCode, Existing.Select(r => r.Code), CompanyName);

    private IActionResult PartnersPage(FlashToastModel toast)
    {
        FlashToast = toast;
        return Page();
    }
}
