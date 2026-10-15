using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Sync;

/// <summary>
/// Queues create jobs for master codes that linked subs do not have yet.
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
    private readonly ILogger<MasterMissingAutoPushService> _logger;

    public MasterMissingAutoPushService(
        IAutoPushSettings settings,
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISyncOrchestrator sync,
        ILogger<MasterMissingAutoPushService> logger)
    {
        _settings = settings;
        _registry = registry;
        _read = read;
        _sync = sync;
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
            queued += await PushMissingAsync(master, dest, entity, source, messages, cancellationToken)
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
}
