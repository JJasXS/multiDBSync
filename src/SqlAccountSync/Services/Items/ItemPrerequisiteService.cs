using SqlAccountSync.Models;
using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Items;

public sealed class PrerequisiteCheck
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class PrerequisiteResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Actions { get; } = [];
    public List<PrerequisiteCheck> Checks { get; } = [];
    public string? LastApiDiagnosis { get; set; }
}

public interface IItemPrerequisiteService
{
    Task<PrerequisiteResult> EnsureAsync(
        RegisteredDatabase dest,
        RegisteredDatabase? source,
        MasterRecord record,
        CancellationToken cancellationToken = default);

    Task<List<PrerequisiteCheck>> CheckAsync(
        RegisteredDatabase dest,
        RegisteredDatabase? source,
        MasterRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class ItemPrerequisiteService : IItemPrerequisiteService
{
    private readonly SelectingWriteAdapter _write;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ILogger<ItemPrerequisiteService> _logger;

    public ItemPrerequisiteService(
        ISqlAccountWriteAdapter write,
        ISqlAccountReadAdapter read,
        ILogger<ItemPrerequisiteService> logger)
    {
        _write = write as SelectingWriteAdapter
            ?? throw new InvalidOperationException("Item prerequisites require SelectingWriteAdapter.");
        _read = read;
        _logger = logger;
    }

    public Task<List<PrerequisiteCheck>> CheckAsync(
        RegisteredDatabase dest,
        RegisteredDatabase? source,
        MasterRecord record,
        CancellationToken cancellationToken = default) =>
        BuildChecksAsync(dest, source, record, dryRun: true, cancellationToken);

    public async Task<PrerequisiteResult> EnsureAsync(
        RegisteredDatabase dest,
        RegisteredDatabase? source,
        MasterRecord record,
        CancellationToken cancellationToken = default)
    {
        var checks = await BuildChecksAsync(dest, source, record, dryRun: false, cancellationToken).ConfigureAwait(false);
        var result = new PrerequisiteResult();
        result.Checks.AddRange(checks);

        var item = StockItemMapper.FromMasterRecord(record);
        var stockGroup = string.IsNullOrWhiteSpace(item.StockGroup) ? "DEFAULT" : item.StockGroup.Trim();

        var groupOnDest = await _read.GetStockGroupAsync(dest, stockGroup, cancellationToken).ConfigureAwait(false);
        if (groupOnDest is null)
        {
            var group = source is not null
                ? await _read.GetStockGroupAsync(source, stockGroup, cancellationToken).ConfigureAwait(false)
                : null;
            group ??= new StockGroupRecord { Code = stockGroup, Description = stockGroup };

            var post = await _write.CreateStockGroupAsync(dest, group, cancellationToken).ConfigureAwait(false);
            if (post.Success)
                result.Actions.Add($"Created stock group {stockGroup} on destination via SDK.");
            else if (LooksLikeExists(post.Message))
                result.Actions.Add($"Stock group {stockGroup} already exists on destination.");
            else
            {
                result.Ok = false;
                result.Message = $"Could not create stock group {stockGroup} on destination: {post.Message}";
                return result;
            }
        }

        var baseUom = ResolveBaseUom(item);
        var uoms = await _read.GetUomsAsync(dest, cancellationToken).ConfigureAwait(false);
        var uomExists = uoms.Any(u => string.Equals(u.Code, baseUom, StringComparison.OrdinalIgnoreCase));
        if (!uomExists)
        {
            var uomPost = await _write.CreateUomAsync(dest, baseUom, cancellationToken).ConfigureAwait(false);
            if (uomPost.Success)
                result.Actions.Add($"Created UOM {baseUom} on destination via SDK.");
            else
            {
                result.Actions.Add($"UOM {baseUom} not on destination and create failed: {uomPost.Message}");
                result.LastApiDiagnosis = uomPost.Message;
                _logger.LogWarning("UOM create via SDK failed for {Uom}: {Message}", baseUom, uomPost.Message);
            }
        }

        result.Ok = true;
        result.Message = result.Actions.Count > 0
            ? string.Join(" ", result.Actions)
            : "Prerequisites satisfied on destination.";
        return result;
    }

    private async Task<List<PrerequisiteCheck>> BuildChecksAsync(
        RegisteredDatabase dest,
        RegisteredDatabase? source,
        MasterRecord record,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var checks = new List<PrerequisiteCheck>();
        var item = StockItemMapper.FromMasterRecord(record);
        var stockGroup = string.IsNullOrWhiteSpace(item.StockGroup) ? "DEFAULT" : item.StockGroup.Trim();
        var baseUom = ResolveBaseUom(item);

        var groupOnDest = await _read.GetStockGroupAsync(dest, stockGroup, cancellationToken).ConfigureAwait(false);
        checks.Add(new PrerequisiteCheck
        {
            Name = "Stock group on destination",
            Ok = true,
            Detail = groupOnDest is not null
                ? $"Stock group {stockGroup} exists on destination."
                : dryRun
                    ? $"Stock group {stockGroup} missing. Would create via SDK before item create."
                    : $"Stock group {stockGroup} will be created via SDK."
        });

        var uoms = await _read.GetUomsAsync(dest, cancellationToken).ConfigureAwait(false);
        var uomExists = uoms.Any(u => string.Equals(u.Code, baseUom, StringComparison.OrdinalIgnoreCase));
        checks.Add(new PrerequisiteCheck
        {
            Name = "UOM on destination",
            Ok = true,
            Detail = uomExists
                ? $"UOM {baseUom} found on destination."
                : dryRun
                    ? $"UOM {baseUom} missing. Would attempt SDK create before item create."
                    : $"UOM {baseUom} will be created if possible."
        });

        return checks;
    }

    private static bool LooksLikeExists(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already", StringComparison.OrdinalIgnoreCase)
            || message.Contains("exists", StringComparison.OrdinalIgnoreCase));

    private static string ResolveBaseUom(StockItem item)
    {
        var copy = StockItemMapper.FromMasterRecord(new MasterRecord
        {
            Code = item.Code,
            Description = item.Description,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["uom"] = item.Uom,
                ["suom"] = item.Suom,
                ["stockgroup"] = item.StockGroup
            }
        });
        if (copy.Uoms.Count > 0)
            return copy.Uoms.First(u => u.IsBase).Uom;
        return copy.Uom is { Length: > 0 } ? copy.Uom : "PCS";
    }
}
