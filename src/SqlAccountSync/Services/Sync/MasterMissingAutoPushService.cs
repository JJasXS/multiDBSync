using System.Globalization;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Sync;

/// <summary>
/// Queues create/update/delete jobs for master party/account codes when the
/// master itself changes (fingerprints). SUB-only edits/deletes do not enqueue.
/// </summary>
public interface IMasterMissingAutoPushService
{
    Task<(int Queued, int SkippedPairs, List<string> Messages)> RunOnceAsync(
        CancellationToken cancellationToken = default);
}

public sealed class MasterMissingAutoPushService : IMasterMissingAutoPushService
{
    private readonly IAutoPushSettings _settings;
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISyncOrchestrator _sync;
    private readonly IMappingService _mapping;
    private readonly IMasterFingerprintStore _fingerprints;
    private readonly SyncOptions _options;
    private readonly SqlAccountingWriteOptions _writeOptions;
    private readonly ILogger<MasterMissingAutoPushService> _logger;

    public MasterMissingAutoPushService(
        IAutoPushSettings settings,
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        IMappingService mapping,
        IMasterFingerprintStore fingerprints,
        IOptions<SyncOptions> options,
        IOptions<SqlAccountingWriteOptions> writeOptions,
        ILogger<MasterMissingAutoPushService> logger)
    {
        _settings = settings;
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
        var flags = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var entities = flags.Entities();
        if (entities.Count == 0)
            return (0, 0, []);

        var messages = new List<string>();
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
        return await PushMastersAsync(masters, databases, links, entities, messages, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(int Queued, int SkippedPairs, List<string> Messages)> PushMastersAsync(
        List<RegisteredDatabase> masters,
        List<RegisteredDatabase> databases,
        IReadOnlyList<DatabaseLink> links,
        IReadOnlyList<string> entities,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var queued = 0;
        var skippedPairs = 0;
        foreach (var master in masters)
        {
            var targets = PartnerAutoPushTargets.Resolve(master, databases, links, _writeOptions.Provider);
            if (targets.Count == 0)
            {
                skippedPairs++;
                continue;
            }

            foreach (var entity in entities)
            {
                var pairQueued = await PushEntityAsync(
                    master, targets, entity, messages, cancellationToken).ConfigureAwait(false);
                if (pairQueued == 0)
                    skippedPairs++;
                queued += pairQueued;
            }
        }

        return (queued, skippedPairs, messages);
    }

    private async Task<int> PushEntityAsync(
        RegisteredDatabase master,
        IReadOnlyList<RegisteredDatabase> targets,
        string entity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        // Account update/delete (and fingerprint replace) run in AccountAutoPushService.
        if (string.Equals(entity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            return await PushAccountCreatesAsync(master, targets, messages, cancellationToken)
                .ConfigureAwait(false);

        if (!PartnerSyncRules.IsPartyEntity(entity))
            return 0;

        return await PushPartyEntityAsync(master, targets, entity, messages, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> PushAccountCreatesAsync(
        RegisteredDatabase master,
        IReadOnlyList<RegisteredDatabase> targets,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        List<GlAccountRow> chart;
        try
        {
            chart = await _read.GetGlChartAsync(master, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-push: failed reading ACCOUNT from {Db}", master.DbCode);
            messages.Add($"Skip {master.DbCode} ACCOUNT: read failed ({ex.Message})");
            return 0;
        }

        var current = GlAccountComparer.BuildHashes(chart);
        var stored = await _fingerprints.ListAsync(master.DbId, EntityCodes.Account, cancellationToken)
            .ConfigureAwait(false);
        var delta = MasterFingerprintDelta.Diff(current, stored);
        if (delta.SeedOnly)
        {
            await _fingerprints.ReplaceAllAsync(master.DbId, EntityCodes.Account, current, cancellationToken)
                .ConfigureAwait(false);
            messages.Add($"{master.DbCode} ACCOUNT: seeded {current.Count} fingerprints (no jobs)");
            return 0;
        }

        if (delta.Added.Count == 0)
            return 0;

        var queued = 0;
        var createsStillPending = false;
        foreach (var dest in targets)
        {
            var (createQueued, stillMissing) = await EnqueueCreatesAsync(
                master, dest, EntityCodes.Account, delta.Added, messages, cancellationToken)
                .ConfigureAwait(false);
            queued += createQueued;
            if (stillMissing)
                createsStillPending = true;
        }

        if (createsStillPending)
        {
            messages.Add(
                $"{master.DbCode} ACCOUNT: create still pending on at least one sub (fingerprints not advanced)");
            return queued;
        }

        // Persist only after creates are queued or already present on every linked sub.
        await _fingerprints.ReplaceAllAsync(master.DbId, EntityCodes.Account, current, cancellationToken)
            .ConfigureAwait(false);
        return queued;
    }

    private async Task<int> PushPartyEntityAsync(
        RegisteredDatabase master,
        IReadOnlyList<RegisteredDatabase> targets,
        string entity,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> current;
        try
        {
            current = await BuildPartyHashesAsync(master, entity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-push: failed reading {Entity} from {Db}", entity, master.DbCode);
            messages.Add($"Skip {master.DbCode} {entity}: read failed ({ex.Message})");
            return 0;
        }

        var stored = await _fingerprints.ListAsync(master.DbId, entity, cancellationToken).ConfigureAwait(false);
        var delta = MasterFingerprintDelta.Diff(current, stored);
        if (delta.SeedOnly)
        {
            await _fingerprints.ReplaceAllAsync(master.DbId, entity, current, cancellationToken)
                .ConfigureAwait(false);
            messages.Add($"{master.DbCode} {entity}: seeded {current.Count} fingerprints (no jobs)");
            return 0;
        }

        var queued = 0;
        var createsStillPending = false;
        foreach (var dest in targets)
        {
            if (delta.Added.Count > 0)
            {
                var (createQueued, stillMissing) = await EnqueueCreatesAsync(
                    master, dest, entity, delta.Added, messages, cancellationToken)
                    .ConfigureAwait(false);
                queued += createQueued;
                if (stillMissing)
                    createsStillPending = true;
            }

            if (_options.AccountAutoUpdateEnabled && delta.Changed.Count > 0)
            {
                queued += await EnqueueUpdatesAsync(
                    master, dest, entity, delta.Changed, messages, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_options.AccountAutoDeleteEnabled && delta.Removed.Count > 0)
            {
                queued += await PushPartyDeletedAsync(
                    master, dest, entity, delta.Removed, messages, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Do not advance fingerprints while creates are still missing on a linked sub —
        // otherwise a failed/skipped enqueue never retries the new master code.
        if (createsStillPending)
        {
            messages.Add(
                $"{master.DbCode} {entity}: create still pending on at least one sub (fingerprints not advanced)");
            _logger.LogWarning(
                "Auto-push {Entity} {Src}: leaving fingerprints unchanged so {Count} added code(s) can retry",
                entity, master.DbCode, delta.Added.Count);
            return queued;
        }

        await _fingerprints.ReplaceAllAsync(master.DbId, entity, current, cancellationToken)
            .ConfigureAwait(false);
        return queued;
    }

    private async Task<Dictionary<string, string>> BuildPartyHashesAsync(
        RegisteredDatabase master,
        string entity,
        CancellationToken cancellationToken)
    {
        var codes = await ReadCodesAsync(master, entity, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var detail = await _read.GetPartyDetailAsync(master, entity, code, cancellationToken)
                .ConfigureAwait(false);
            if (detail is null)
                continue;
            map[code] = PartyFieldComparer.ContentHash(detail);
        }

        return map;
    }

    /// <returns>Queued job count, and whether any added code is still missing on dest without a job.</returns>
    private async Task<(int Queued, bool StillMissing)> EnqueueCreatesAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        IReadOnlyList<string> addedCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        HashSet<string> destCodes;
        try
        {
            destCodes = await ReadCodesAsync(dest, entity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-push: failed reading {Entity} from {Db}", entity, dest.DbCode);
            messages.Add($"Skip {master.DbCode}→{dest.DbCode} {entity}: dest read failed");
            return (0, true);
        }

        var missing = addedCodes.Where(code => !destCodes.Contains(code)).ToList();
        if (missing.Count == 0)
            return (0, false);

        var (queued, failed, batchMsgs) = await _sync.EnqueueMasterBatchAsync(
            master.DbId,
            [dest.DbId],
            entity,
            missing,
            dryRun: false,
            user: AutoPushJobRules.PartyUser,
            cancellationToken,
            reviveCancelled: false).ConfigureAwait(false);

        messages.Add($"{master.DbCode}→{dest.DbCode} {entity}: added={missing.Count}, queued={queued}, failed={failed}");
        if (failed > 0)
            messages.AddRange(batchMsgs.Take(3));
        _logger.LogInformation(
            "Auto-push {Entity} {Src}→{Dest}: {Missing} added, queued {Queued}, failed {Failed}",
            entity, master.DbCode, dest.DbCode, missing.Count, queued, failed);
        // Still missing when nothing was queued for codes that dest does not have yet.
        return (queued, queued <= 0 || failed > 0);
    }

    private async Task<int> EnqueueUpdatesAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        IReadOnlyList<string> changed,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var limited = changed.Take(Math.Max(1, _options.AccountAutoDeleteMaxPerCycle)).ToList();
        var queued = 0;
        var failed = 0;
        foreach (var code in limited)
        {
            var (jobId, msg) = await _sync.EnqueueMasterUpdateAsync(
                master.DbId, dest.DbId, entity, code, AutoPushJobRules.PartyUser, cancellationToken)
                .ConfigureAwait(false);
            if (jobId > 0)
                queued++;
            else
            {
                failed++;
                if (failed <= 3)
                    messages.Add($"{master.DbCode}→{dest.DbCode} update {code}: {msg}");
            }
        }

        messages.Add($"{master.DbCode}→{dest.DbCode} {entity}: changed={limited.Count}, queued={queued}, failed={failed}");
        return queued;
    }

    private async Task<int> PushPartyDeletedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        IReadOnlyList<string> removedCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var removed = removedCodes
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maps = await _mapping.ListAsync(master.DbId, dest.DbId, entity, cancellationToken).ConfigureAwait(false);
        var candidates = maps
            .Where(m => string.Equals(m.Status, MappingStatuses.Mapped, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.IsNullOrWhiteSpace(m.SubKey))
            .Where(m => removed.Contains((m.MasterCode ?? "").Trim()))
            .Take(Math.Max(1, _options.AccountAutoDeleteMaxPerCycle))
            .ToList();
        if (candidates.Count == 0)
            return 0;

        var queued = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var map in candidates)
        {
            var code = (map.MasterCode ?? "").Trim();
            if (await _read.GlAccountHasActivityAsync(dest, code, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                messages.Add($"{master.DbCode}→{dest.DbCode} delete {code}: skipped (GL has activity)");
                continue;
            }

            var detail = await _read.GetPartyDetailAsync(dest, entity, code, cancellationToken).ConfigureAwait(false);
            if (HasOutstandingBalance(detail))
            {
                skipped++;
                messages.Add($"{master.DbCode}→{dest.DbCode} delete {code}: skipped (outstanding balance)");
                continue;
            }

            var (jobId, msg) = await _sync.EnqueueMasterDeleteAsync(
                master.DbId, dest.DbId, entity, code, map.MappingId, AutoPushJobRules.PartyUser, cancellationToken)
                .ConfigureAwait(false);
            if (jobId > 0)
                queued++;
            else
            {
                failed++;
                if (failed <= 3)
                    messages.Add($"{master.DbCode}→{dest.DbCode} delete {code}: {msg}");
            }
        }

        messages.Add(
            $"{master.DbCode}→{dest.DbCode} {entity}: delete-candidates={candidates.Count}, queued={queued}, skipped={skipped}, failed={failed}");
        return queued;
    }

    private async Task<HashSet<string>> ReadCodesAsync(
        RegisteredDatabase db, string entity, CancellationToken cancellationToken)
    {
        if (string.Equals(entity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var chart = await _read.GetGlChartAsync(db, cancellationToken).ConfigureAwait(false);
            return ToSet(chart.Select(row => row.Code));
        }

        var rows = string.Equals(entity, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
            ? await _read.GetSuppliersAsync(db, cancellationToken).ConfigureAwait(false)
            : await _read.GetCustomersAsync(db, cancellationToken).ConfigureAwait(false);
        return ToSet(rows.Select(row => row.Code));
    }

    private static HashSet<string> ToSet(IEnumerable<string?> codes) =>
        codes
            .Select(code => (code ?? "").Trim())
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasOutstandingBalance(MasterRecord? detail)
    {
        if (detail is null)
            return false;
        if (!detail.Fields.TryGetValue("outstanding", out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        return decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt)
            && Math.Abs(amt) > 0.0001m;
    }
}
