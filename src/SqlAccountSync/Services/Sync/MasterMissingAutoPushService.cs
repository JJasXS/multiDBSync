using System.Globalization;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Sync;

/// <summary>
/// Queues create (and when enabled update/delete) jobs for master party/account codes
/// that linked subs need, driven by Push-page auto-push checkboxes.
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
    private readonly SyncOptions _options;
    private readonly ILogger<MasterMissingAutoPushService> _logger;

    public MasterMissingAutoPushService(
        IAutoPushSettings settings,
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        IMappingService mapping,
        IOptions<SyncOptions> options,
        ILogger<MasterMissingAutoPushService> logger)
    {
        _settings = settings;
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
            var targets = PartnerAutoPushTargets.Resolve(master, databases, links);
            if (targets.Count == 0)
            {
                skippedPairs++;
                continue;
            }

            var masterCodes = await LoadCodesAsync(master, entities, messages, cancellationToken).ConfigureAwait(false);
            foreach (var dest in targets)
            {
                var pairQueued = await PushDestAsync(
                    master, dest, entities, masterCodes, messages, cancellationToken).ConfigureAwait(false);
                if (pairQueued == 0)
                    skippedPairs++;
                queued += pairQueued;
            }
        }

        return (queued, skippedPairs, messages);
    }

    private async Task<int> PushDestAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        IReadOnlyList<string> entities,
        Dictionary<string, HashSet<string>> masterCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var queued = 0;
        foreach (var entity in entities)
        {
            if (!masterCodes.TryGetValue(entity, out var source) || source.Count == 0)
                continue;

            // Account create stays here; account update/delete runs in AccountAutoPushService.
            if (string.Equals(entity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            {
                queued += await PushMissingAsync(master, dest, entity, source, messages, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!PartnerSyncRules.IsPartyEntity(entity))
                continue;

            queued += await PushMissingAsync(master, dest, entity, source, messages, cancellationToken)
                .ConfigureAwait(false);
            if (_options.AccountAutoUpdateEnabled)
                queued += await PushPartyChangedAsync(master, dest, entity, source, messages, cancellationToken)
                    .ConfigureAwait(false);
            if (_options.AccountAutoDeleteEnabled)
                queued += await PushPartyDeletedAsync(master, dest, entity, source, messages, cancellationToken)
                    .ConfigureAwait(false);
        }

        return queued;
    }

    private async Task<int> PushMissingAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        HashSet<string> source,
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
            return 0;
        }

        var missing = source.Where(code => !destCodes.Contains(code)).ToList();
        if (missing.Count == 0)
            return 0;

        var (queued, failed, batchMsgs) = await _sync.EnqueueMasterBatchAsync(
            master.DbId,
            [dest.DbId],
            entity,
            missing,
            dryRun: false,
            user: "system:auto-push",
            cancellationToken,
            reviveCancelled: false).ConfigureAwait(false);

        messages.Add($"{master.DbCode}→{dest.DbCode} {entity}: missing={missing.Count}, queued={queued}, failed={failed}");
        if (failed > 0)
            messages.AddRange(batchMsgs.Take(3));
        _logger.LogInformation(
            "Auto-push {Entity} {Src}→{Dest}: {Missing} missing, queued {Queued}, failed {Failed}",
            entity, master.DbCode, dest.DbCode, missing.Count, queued, failed);
        return queued;
    }

    private async Task<int> PushPartyChangedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        HashSet<string> sourceCodes,
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
            _logger.LogWarning(ex, "Auto-update: failed reading {Entity} from {Db}", entity, dest.DbCode);
            return 0;
        }

        var shared = sourceCodes.Where(destCodes.Contains).Take(Math.Max(1, _options.AccountAutoDeleteMaxPerCycle * 4)).ToList();
        if (shared.Count == 0)
            return 0;

        var sourceByCode = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase);
        var destByCode = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in shared)
        {
            var src = await _read.GetPartyDetailAsync(master, entity, code, cancellationToken).ConfigureAwait(false);
            var dst = await _read.GetPartyDetailAsync(dest, entity, code, cancellationToken).ConfigureAwait(false);
            if (src is not null)
                sourceByCode[code] = src;
            if (dst is not null)
                destByCode[code] = dst;
        }

        var changed = PartyFieldComparer.CollectDifferingCodes(sourceByCode, destByCode)
            .Take(Math.Max(1, _options.AccountAutoDeleteMaxPerCycle))
            .ToList();
        if (changed.Count == 0)
            return 0;

        var queued = 0;
        var failed = 0;
        foreach (var code in changed)
        {
            var (jobId, msg) = await _sync.EnqueueMasterUpdateAsync(
                master.DbId, dest.DbId, entity, code, "system:auto-push", cancellationToken)
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

        messages.Add($"{master.DbCode}→{dest.DbCode} {entity}: changed={changed.Count}, queued={queued}, failed={failed}");
        return queued;
    }

    private async Task<int> PushPartyDeletedAsync(
        RegisteredDatabase master,
        RegisteredDatabase dest,
        string entity,
        HashSet<string> masterCodes,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var maps = await _mapping.ListAsync(master.DbId, dest.DbId, entity, cancellationToken).ConfigureAwait(false);
        var candidates = maps
            .Where(m => string.Equals(m.Status, MappingStatuses.Mapped, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.IsNullOrWhiteSpace(m.SubKey))
            .Where(m => !masterCodes.Contains((m.MasterCode ?? "").Trim()))
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
                master.DbId, dest.DbId, entity, code, map.MappingId, "system:auto-push", cancellationToken)
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

    private async Task<Dictionary<string, HashSet<string>>> LoadCodesAsync(
        RegisteredDatabase db,
        IReadOnlyList<string> entities,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            try
            {
                map[entity] = await ReadCodesAsync(db, entity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-push: failed reading {Entity} from {Db}", entity, db.DbCode);
                messages.Add($"Skip {db.DbCode} {entity}: read failed ({ex.Message})");
            }
        }

        return map;
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
