using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
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
    private static readonly string[] UomListPaths =
    [
        "/uom?offset=0&limit=200",
        "/stockuom?offset=0&limit=200",
        "/unitofmeasure?offset=0&limit=200"
    ];

    private readonly ISqlAccountApiClient _api;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly SqlAccountingWriteOptions _writeOptions;

    public ItemPrerequisiteService(
        ISqlAccountApiClient api,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        IOptions<SqlAccountingWriteOptions> writeOptions,
        ILogger<ItemPrerequisiteService> logger)
    {
        _api = api;
        _read = read;
        _write = write;
        _writeOptions = writeOptions.Value;
        _ = logger;
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
        var sdkMode = IsSdkProvider();

        var groupExists = await StockGroupExistsAsync(dest, stockGroup, sdkMode, cancellationToken).ConfigureAwait(false);
        if (!groupExists)
        {
            var group = source is not null
                ? await _read.GetStockGroupAsync(source, stockGroup, cancellationToken).ConfigureAwait(false)
                : null;
            group ??= new StockGroupRecord { Code = stockGroup, Description = stockGroup };

            var post = await _write.CreateStockGroupAsync(dest, group, cancellationToken).ConfigureAwait(false);
            if (post.Success)
            {
                result.Actions.Add(sdkMode
                    ? $"Created stock group {stockGroup} on destination via SDK ST_GROUP."
                    : $"Created stock group {stockGroup} on destination via API.");
            }
            else if (post.Message.Contains("unique", StringComparison.OrdinalIgnoreCase)
                     || post.Message.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase)
                     || post.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                result.Actions.Add($"Stock group {stockGroup} already exists on destination.");
            }
            else
            {
                result.Ok = false;
                result.Message = $"Could not create stock group {stockGroup} on destination: {post.Message}";
                return result;
            }
        }

        var baseUom = ResolveBaseUom(item);
        if (sdkMode)
        {
            // Standalone Maintain UOM BizObject is not implemented. Item create writes UOM via ST_ITEM.cdsUOM.
            result.Actions.Add(
                $"SDK mode: standalone /uom is not used. UOM '{baseUom}' will be written on item via ST_ITEM.cdsUOM.");
            result.LastApiDiagnosis =
                "Standalone UOM master CUD is unsupported in SDK mode; item prerequisite relies on ST_ITEM.cdsUOM.";
        }
        else
        {
            var uomApi = await FindWorkingUomListPathAsync(dest, cancellationToken).ConfigureAwait(false);
            if (uomApi is null)
            {
                result.Actions.Add(
                    $"UOM master API not available on {dest.DisplayName} (GET /uom returns HTML). " +
                    $"Item push will use UOM '{baseUom}' in POST /stockitem; tenant must accept it.");
                result.LastApiDiagnosis =
                    "No JSON UOM list endpoint on destination. POST /stockitem may fail with 'No corresponding master record found' until eStream enables UOM API or fixes item create.";
            }
            else
            {
                var uomExists = await _api.ExistsInListAsync(dest, uomApi, "code", baseUom, cancellationToken).ConfigureAwait(false);
                if (!uomExists)
                {
                    var uomPost = await TryCreateUomAsync(dest, uomApi, baseUom, cancellationToken).ConfigureAwait(false);
                    if (uomPost.Success)
                        result.Actions.Add($"Created UOM {baseUom} on destination via API.");
                    else
                        result.Actions.Add($"UOM {baseUom} not on destination and create failed: {uomPost.Message}");
                }
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
        var sdkMode = IsSdkProvider();

        var groupExists = await StockGroupExistsAsync(dest, stockGroup, sdkMode, cancellationToken).ConfigureAwait(false);
        checks.Add(new PrerequisiteCheck
        {
            Name = "Stock group on destination",
            Ok = true,
            Detail = groupExists
                ? $"Stock group {stockGroup} exists on destination."
                : dryRun
                    ? $"Stock group {stockGroup} missing. Would create via {(sdkMode ? "SDK ST_GROUP" : "POST /stockgroup")} before item create."
                    : $"Stock group {stockGroup} will be created via {(sdkMode ? "SDK ST_GROUP" : "API")}."
        });

        if (sdkMode)
        {
            checks.Add(new PrerequisiteCheck
            {
                Name = "UOM (SDK cdsUOM)",
                Ok = true,
                Detail = $"Standalone /uom unsupported in SDK mode. Item create embeds UOM '{baseUom}' via ST_ITEM.cdsUOM."
            });
            return checks;
        }

        var uomPath = await FindWorkingUomListPathAsync(dest, cancellationToken).ConfigureAwait(false);
        if (uomPath is null)
        {
            checks.Add(new PrerequisiteCheck
            {
                Name = "UOM master API",
                Ok = true,
                Detail = $"No JSON UOM endpoint on destination. Push uses '{baseUom}' in item payload (API may still reject)."
            });
        }
        else
        {
            var uomExists = await _api.ExistsInListAsync(dest, uomPath, "code", baseUom, cancellationToken).ConfigureAwait(false);
            checks.Add(new PrerequisiteCheck
            {
                Name = "UOM on destination",
                Ok = true,
                Detail = uomExists
                    ? $"UOM {baseUom} found via {uomPath}."
                    : dryRun
                        ? $"UOM {baseUom} missing. Would attempt POST before item create."
                        : $"UOM {baseUom} will be created if possible."
            });
        }

        return checks;
    }

    private async Task<bool> StockGroupExistsAsync(
        RegisteredDatabase dest, string stockGroup, bool sdkMode, CancellationToken cancellationToken)
    {
        // Prefer Firebird RO — works for any registered company without paid REST list calls.
        var fb = await _read.GetStockGroupAsync(dest, stockGroup, cancellationToken).ConfigureAwait(false);
        if (fb is not null)
            return true;

        if (sdkMode)
            return false;

        return await _api.ExistsInListAsync(dest, "/stockgroup?offset=0&limit=200", "code", stockGroup, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsSdkProvider() =>
        string.Equals(
            _writeOptions.Provider?.Trim(),
            SqlAccountingWriteProviders.Sdk,
            StringComparison.OrdinalIgnoreCase);

    private async Task<string?> FindWorkingUomListPathAsync(RegisteredDatabase dest, CancellationToken cancellationToken)
    {
        foreach (var path in UomListPaths)
        {
            var response = await _api.GetAsync(dest, path, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess && SqlAccountApiClient.IsJson(response.Body))
                return path;
        }
        return null;
    }

    private async Task<WriteResult> TryCreateUomAsync(
        RegisteredDatabase dest, string listPath, string uomCode, CancellationToken cancellationToken)
    {
        var postPath = listPath.Contains('?') ? listPath[..listPath.IndexOf('?')] : listPath;
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["code"] = uomCode,
            ["description"] = uomCode,
            ["isactive"] = true
        });
        return await _api.PostAsync(dest, postPath, payload, cancellationToken).ConfigureAwait(false);
    }

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
