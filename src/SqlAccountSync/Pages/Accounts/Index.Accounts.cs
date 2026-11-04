using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Pages.Accounts;

public partial class IndexModel
{
    public async Task<IActionResult> OnPostPushAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "accounts";
        await LoadAccountsAsync();

        if (SelectedSource is null || !SyncTargetResolver.IsMasterOrBoth(SelectedSource))
            return AccountsPage(FlashToasts.Fail("Select a Master (or BOTH) database as the source."));

        if (SelectedCodes.Count == 0 || SelectedSubDbIds.Count == 0)
            return AccountsPage(FlashToasts.Fail("Select at least one account and one sub database."));

        var (queued, failed, messages) = await _sync.EnqueueMasterBatchAsync(
            SourceDbId, SelectedSubDbIds, EntityCodes.Account, SelectedCodes,
            dryRun: false, CurrentEmail);

        FlashToast = FlashToasts.Queue(queued, failed, messages, "account job");
        await LoadAccountsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveEditAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "accounts";
        await LoadAccountsAsync();

        if (SelectedSource is null)
            return AccountsPage(FlashToasts.Fail("Select a Master (or BOTH) database as the source."));

        FlashToast = await AccountChartEdit.SaveAsync(
            _read, _write, _sync, SelectedSource, TargetSubs, EditInput(), CurrentEmail, WriteProvider);
        await LoadAccountsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "accounts";
        await LoadAccountsAsync();

        if (SelectedSource is null)
            return AccountsPage(FlashToasts.Fail("Select a Master (or BOTH) database as the source."));

        var code = (DeleteCode ?? "").Trim();
        if (code.Length == 0 || DeleteMappingId <= 0 || DeleteSubDbId <= 0)
            return AccountsPage(FlashToasts.Fail("Delete request is incomplete."));

        var (jobId, msg) = await _sync.EnqueueMasterDeleteAsync(
            SourceDbId, DeleteSubDbId, EntityCodes.Account, code, DeleteMappingId, CurrentEmail);

        FlashToast = jobId > 0
            ? FlashToasts.Ok($"Queued delete {code} (job #{jobId}).")
            : FlashToasts.Fail(msg);

        await LoadAccountsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteFromSubsAsync()
    {
        if (!CanOperate) return Forbid();
        Tab = "accounts";
        await LoadAccountsAsync();

        if (SelectedSource is null)
            return AccountsPage(FlashToasts.Fail("Select a Master (or BOTH) database as the source."));

        var code = (DeleteCode ?? "").Trim();
        if (code.Length == 0)
            return AccountsPage(FlashToasts.Fail("Account code is required."));

        var queued = 0;
        var failed = 0;
        var messages = new List<string>();

        foreach (var sub in TargetSubs.Where(s => PartnerAutoPushTargets.IsWriteReady(s, WriteProvider)))
            await DeleteAccountOnSubAsync(code, sub, messages, () => queued++, () => failed++);

        FlashToast = FlashToasts.Queue(queued, failed, messages, "delete");
        await LoadAccountsAsync();
        return Page();
    }

    private async Task DeleteAccountOnSubAsync(
        string code,
        RegisteredDatabase sub,
        List<string> messages,
        Action onQueued,
        Action onFailed)
    {
        var map = await _mapping.FindBySourceAsync(SourceDbId, sub.DbId, EntityCodes.Account, code);
        if (map is not null && !string.IsNullOrWhiteSpace(map.SubKey))
        {
            var (jobId, msg) = await _sync.EnqueueMasterDeleteAsync(
                SourceDbId, sub.DbId, EntityCodes.Account, code, map.MappingId, CurrentEmail);
            if (jobId > 0)
            {
                onQueued();
                messages.Add($"{code} → {sub.DisplayName} (job #{jobId})");
            }
            else
            {
                onFailed();
                messages.Add($"{code} → {sub.DisplayName}: {msg}");
            }
            return;
        }

        if (!await _read.MasterExistsByCodeAsync(sub, EntityCodes.Account, code))
        {
            messages.Add($"{code} → {sub.DisplayName}: not present.");
            return;
        }

        var write = await _write.DeleteMasterAsync(sub, EntityCodes.Account, code);
        if (!write.Success)
        {
            onFailed();
            messages.Add($"{code} → {sub.DisplayName}: {write.Message}");
            return;
        }

        onQueued();
        messages.Add($"{code} → {sub.DisplayName}: deleted.");
        if (map is not null)
            await _mapping.DisableAsync(map.MappingId, CurrentEmail);
    }

    private AccountEditInput EditInput() => new()
    {
        Code = EditCode,
        Description = EditDescription,
        Description2 = EditDescription2,
        AccType = EditAccType,
        SpecialAccType = EditSpecialAccType,
        Tax = EditTax,
        CashFlowType = EditCashFlowType,
        Sic = EditSic
    };

    private IActionResult AccountsPage(FlashToastModel toast)
    {
        FlashToast = toast;
        return Page();
    }

    private async Task LoadAccountsAsync()
    {
        NeedsUpdateCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingDeletes = [];

        var databases = (await _registry.ListAsync())
            .Where(d => d.IsActive)
            .ToList();

        MasterDatabases = databases
            .Where(SyncTargetResolver.IsMasterOrBoth)
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (SourceDbId == 0 || MasterDatabases.All(d => d.DbId != SourceDbId))
            SourceDbId = MasterDatabases.FirstOrDefault()?.DbId ?? 0;

        SelectedSource = MasterDatabases.FirstOrDefault(d => d.DbId == SourceDbId);
        if (SelectedSource is null)
        {
            TargetSubs = [];
            Records = [];
            return;
        }

        var links = await _registry.ListLinksAsync();
        TargetSubs = SyncTargetResolver.ResolveLinkedSubs(SelectedSource, databases, links);

        List<GlAccountRow> masterChart;
        try
        {
            masterChart = await _read.GetGlChartAsync(SelectedSource);
            Records = GlAccountMapper.ToMasterRecords(masterChart);
        }
        catch
        {
            Records = [];
            return;
        }

        if (TargetSubs.Count == 0 || masterChart.Count == 0)
            return;

        await ComputeAccountStatusesAsync(SelectedSource, masterChart, TargetSubs);
    }

    private async Task ComputeAccountStatusesAsync(
        RegisteredDatabase master,
        List<GlAccountRow> masterChart,
        List<RegisteredDatabase> subs)
    {
        var masterByCode = GlAccountComparer.IndexByCode(masterChart);
        var masterCodes = masterByCode.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var needsUpdate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<PendingDeleteRow>();

        foreach (var sub in subs)
        {
            List<GlAccountRow> destChart;
            try
            {
                destChart = await _read.GetGlChartAsync(sub);
            }
            catch
            {
                continue;
            }

            foreach (var code in GlAccountComparer.CollectDifferingCodes(masterChart, destChart))
                needsUpdate.Add(code);

            var maps = await _mapping.ListAsync(master.DbId, sub.DbId, EntityCodes.Account);
            foreach (var map in maps)
            {
                if (!string.Equals(map.Status, MappingStatuses.Mapped, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(map.SubKey))
                    continue;
                var code = (map.MasterCode ?? "").Trim();
                if (code.Length == 0 || masterCodes.Contains(code))
                    continue;
                pending.Add(new PendingDeleteRow
                {
                    Code = code,
                    MappingId = map.MappingId,
                    SubDbId = sub.DbId,
                    SubDbCode = sub.DbCode,
                    SubDisplayName = sub.DisplayName
                });
            }
        }

        NeedsUpdateCodes = needsUpdate;
        PendingDeletes = pending
            .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SubDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
