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
/// Account auto update/delete (creates are handled by MasterMissingAutoPushService).
/// Enqueues only when the master chart fingerprint changes — not on SUB drift.
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
    private readonly IMasterFingerprintStore _fingerprints;
    private readonly SyncOptions _options;
    private readonly SqlAccountingWriteOptions _writeOptions;
    private readonly ILogger<AccountAutoPushService> _logger;

    public AccountAutoPushService(
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        IMappingService mapping,
        IMasterFingerprintStore fingerprints,
        IOptions<SyncOptions> options,
        IOptions<SqlAccountingWriteOptions> writeOptions,
        ILogger<AccountAutoPushService> logger)
    {
        _registry = registry;
        _read = read;
        _sync = sync;
        _mapping = mapping;
        _fingerprints = fingerprints;
        _options = options.Value;
        _writeOptions = writeOptions.Value;
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
                .Where(d => PartnerAutoPushTargets.IsWriteReady(d, _writeOptions.Provider))
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

            var current = GlAccountComparer.BuildHashes(masterChart);
            var stored = await _fingerprints.ListAsync(master.DbId, EntityCodes.Account, cancellationToken)
                .ConfigureAwait(false);
            var delta = MasterFingerprintDelta.Diff(current, stored);
            if (delta.SeedOnly)
            {
                await _fingerprints.ReplaceAllAsync(master.DbId, EntityCodes.Account, current, cancellationToken)
                    .ConfigureAwait(false);
                messages.Add($"{master.DbCode} ACCOUNT: seeded {current.Count} fingerprints (no jobs)");
                continue;
            }

            foreach (var dest in targets)
            {
                var pairQueued = await ProcessPairAsync(
                    master, dest, masterChart, delta, messages, cancellationToken).ConfigureAwait(false);
                if (pairQueued == 0)
                    skippedPairs++;
                queued += pairQueued;
            }

            // Persist after create (MasterMissing) + update/delete decisions so SUB drift
            // cannot re-trigger, and newly added codes stop looking "added".
            await _fingerprints.ReplaceAllAsync(master.DbId, EntityCodes.Account, current, cancellationToken)
                .ConfigureAwait(false);
        }

        return (queued, skippedPairs, messages);
    }

    private async Task<int> ProcessPairAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        List<GlAccountRow> masterChart,
        MasterFingerprintDelta.Result delta,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var queued = 0;

        if (_options.AccountAutoUpdateEnabled && delta.Changed.Count > 0)
            queued += await PushChangedAsync(master, dest, masterChart, delta.Changed, messages, cancellationToken)
                .ConfigureAwait(false);

        if (_options.AccountAutoDeleteEnabled && delta.Removed.Count > 0)
            queued += await PushDeletedAsync(master, dest, delta.Removed, messages, cancellationToken)
                .ConfigureAwait(false);

        return queued;
    }

    private async Task<int> PushChangedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        List<GlAccountRow> masterChart,
        IReadOnlyList<string> changedCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var ordered = GlAccountMapper.OrderCodesParentsFirst(changedCodes.ToList(), masterChart);
        var queued = 0;
        var failed = 0;
        foreach (var code in ordered)
        {
            var (jobId, msg) = await _sync.EnqueueMasterUpdateAsync(
                master.DbId, dest.DbId, EntityCodes.Account, code,
                AutoPushJobRules.AccountUser, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<string> removedCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var removed = removedCodes
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var maps = await _mapping.ListAsync(master.DbId, dest.DbId, EntityCodes.Account, cancellationToken)
            .ConfigureAwait(false);
        var candidates = maps
            .Where(m => string.Equals(m.Status, MappingStatuses.Mapped, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.IsNullOrWhiteSpace(m.SubKey))
            .Where(m => removed.Contains((m.MasterCode ?? "").Trim()))
            .ToList();
        if (candidates.Count == 0)
            return 0;

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

        var levelByCode = destChart.ToDictionary(
            a => (a.Code ?? "").Trim(),
            a => a.Level,
            StringComparer.OrdinalIgnoreCase);

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
                AutoPushJobRules.AccountUser, cancellationToken).ConfigureAwait(false);
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
