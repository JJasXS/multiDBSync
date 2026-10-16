using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.Accounts;

/// <summary>
/// Account auto update/delete only (creates are handled by MasterMissingAutoPushService).
/// Mirrors field updates and tracked deletes when Sync options enable them.
/// </summary>
public interface IAccountAutoPushService
{
    Task<(int Queued, int SkippedPairs, List<string> Messages)> RunOnceAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AccountAutoPushService : IAccountAutoPushService
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISyncOrchestrator _sync;
    private readonly IMappingService _mapping;
    private readonly SyncOptions _options;
    private readonly ILogger<AccountAutoPushService> _logger;

    public AccountAutoPushService(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        IMappingService mapping,
        IOptions<SyncOptions> options,
        ILogger<AccountAutoPushService> logger)
    {
        _registry = registry;
        _read = read;
        _sync = sync;
        _mapping = mapping;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(int Queued, int SkippedPairs, List<string> Messages)> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var queued = 0;
        var skippedPairs = 0;

        var databases = (await _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(d => d.IsActive)
            .ToList();
        var masters = databases.Where(SyncTargetResolver.IsMasterOrBoth).ToList();
        if (masters.Count == 0)
        {
            messages.Add("No MASTER/BOTH databases.");
            return (0, 0, messages);
        }

        var links = await _registry.ListLinksAsync(cancellationToken).ConfigureAwait(false);

        foreach (var master in masters)
        {
            var targets = SyncTargetResolver.ResolveLinkedSubs(master, databases, links)
                .Where(PartnerAutoPushTargets.HasApiCredentials)
                .ToList();
            if (targets.Count == 0)
            {
                skippedPairs++;
                continue;
            }

            List<GlAccountRow> masterChart;
            try
            {
                masterChart = await _read.GetGlChartAsync(master, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account auto-push: failed reading chart from {Db}", master.DbCode);
                messages.Add($"Skip {master.DbCode}: chart read failed ({ex.Message})");
                continue;
            }

            if (masterChart.Count == 0)
                continue;

            foreach (var dest in targets)
            {
                var pairQueued = await ProcessPairAsync(
                    master, dest, masterChart, messages, cancellationToken).ConfigureAwait(false);
                if (pairQueued == 0)
                    skippedPairs++;
                queued += pairQueued;
            }
        }

        return (queued, skippedPairs, messages);
    }

    private async Task<int> ProcessPairAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        List<GlAccountRow> masterChart,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        List<GlAccountRow> destChart;
        try
        {
            destChart = await _read.GetGlChartAsync(dest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account auto-push: failed reading chart from {Db}", dest.DbCode);
            messages.Add($"Skip {master.DbCode}→{dest.DbCode}: dest chart read failed");
            return 0;
        }

        var queued = 0;

        if (_options.AccountAutoUpdateEnabled)
            queued += await PushChangedAsync(master, dest, masterChart, destChart, messages, cancellationToken)
                .ConfigureAwait(false);

        if (_options.AccountAutoDeleteEnabled)
            queued += await PushDeletedAsync(master, dest, masterChart, destChart, messages, cancellationToken)
                .ConfigureAwait(false);

        return queued;
    }

    private async Task<int> PushChangedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        List<GlAccountRow> masterChart,
        List<GlAccountRow> destChart,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var changed = GlAccountComparer.CollectDifferingCodes(masterChart, destChart);
        if (changed.Count == 0)
            return 0;

        var ordered = GlAccountMapper.OrderCodesParentsFirst(changed, masterChart);
        var queued = 0;
        var failed = 0;
        foreach (var code in ordered)
        {
            var (jobId, msg) = await _sync.EnqueueMasterUpdateAsync(
                master.DbId, dest.DbId, EntityCodes.Account, code,
                "system:account-auto-push", cancellationToken).ConfigureAwait(false);
            if (jobId > 0)
                queued++;
            else
            {
                failed++;
                if (failed <= 5)
                    messages.Add($"{master.DbCode}→{dest.DbCode} update {code}: {msg}");
            }
        }

        messages.Add($"{master.DbCode}→{dest.DbCode}: changed={ordered.Count}, queued={queued}, failed={failed}");
        _logger.LogInformation(
            "Account auto-update {Src}→{Dest}: {Changed} changed, queued {Queued}, failed {Failed}",
            master.DbCode, dest.DbCode, ordered.Count, queued, failed);
        return queued;
    }

    private async Task<int> PushDeletedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        List<GlAccountRow> masterChart,
        List<GlAccountRow> destChart,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var masterCodes = masterChart
            .Select(a => (a.Code ?? "").Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var maps = await _mapping.ListAsync(master.DbId, dest.DbId, EntityCodes.Account, cancellationToken)
            .ConfigureAwait(false);
        var candidates = maps
            .Where(m => string.Equals(m.Status, MappingStatuses.Mapped, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.IsNullOrWhiteSpace(m.SubKey))
            .Where(m => !masterCodes.Contains((m.MasterCode ?? "").Trim()))
            .ToList();
        if (candidates.Count == 0)
            return 0;

        var levelByCode = destChart.ToDictionary(
            a => (a.Code ?? "").Trim(),
            a => a.Level,
            StringComparer.OrdinalIgnoreCase);

        // Children before parents so leaves are removed first.
        var ordered = candidates
            .OrderByDescending(m =>
                levelByCode.TryGetValue((m.MasterCode ?? "").Trim(), out var lvl) ? lvl : EstimateLevel(m.MasterCode))
            .ThenByDescending(m => (m.MasterCode ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _options.AccountAutoDeleteMaxPerCycle))
            .ToList();

        var queued = 0;
        var failed = 0;
        foreach (var map in ordered)
        {
            var code = (map.MasterCode ?? "").Trim();
            var (jobId, msg) = await _sync.EnqueueMasterDeleteAsync(
                master.DbId, dest.DbId, EntityCodes.Account, code, map.MappingId,
                "system:account-auto-push", cancellationToken).ConfigureAwait(false);
            if (jobId > 0)
                queued++;
            else
            {
                failed++;
                if (failed <= 5)
                    messages.Add($"{master.DbCode}→{dest.DbCode} delete {code}: {msg}");
            }
        }

        messages.Add(
            $"{master.DbCode}→{dest.DbCode}: delete-candidates={candidates.Count}, queued={queued}, failed={failed}");
        _logger.LogInformation(
            "Account auto-delete {Src}→{Dest}: {Candidates} candidates, queued {Queued}, failed {Failed}",
            master.DbCode, dest.DbCode, candidates.Count, queued, failed);
        return queued;
    }

    /// <summary>When the code is already gone from master chart, fall back to slash/depth heuristic.</summary>
    private static int EstimateLevel(string? code)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0)
            return 0;
        return c.Count(ch => ch is '-' or '/' or '.');
    }
}
