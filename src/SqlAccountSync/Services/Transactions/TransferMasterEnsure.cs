using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Services.Transactions;

public sealed class TransferEnsureResult
{
    public string? Error { get; init; }
    public string Detail { get; init; } = "";

    public static TransferEnsureResult Ok(string detail = "") => new() { Detail = detail };
    public static TransferEnsureResult Fail(string error) => new() { Error = error };
}

public interface ITransferMasterEnsure
{
    Task<TransferEnsureResult> EnsureAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, string user, int? originJobId, string txnStamp,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DryRunCheck>> PreviewAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates a destination supplier from the source customer, and missing line items,
/// when a transfer has no mapping yet.
/// </summary>
public sealed class TransferMasterEnsure : ITransferMasterEnsure
{
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly IMappingService _mapping;
    private readonly IItemPrerequisiteService _itemPrereqs;

    public TransferMasterEnsure(
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        IMappingService mapping,
        IItemPrerequisiteService itemPrereqs)
    {
        _read = read;
        _write = write;
        _mapping = mapping;
        _itemPrereqs = itemPrereqs;
    }

    public async Task<TransferEnsureResult> EnsureAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, string user, int? originJobId, string txnStamp,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var partyError = await EnsureSupplierAsync(
            source, srcDb, dstDb, destParty, requiresParty, user, originJobId, txnStamp, notes, cancellationToken)
            .ConfigureAwait(false);
        if (partyError is not null)
            return TransferEnsureResult.Fail(partyError);

        var itemError = await EnsureItemsAsync(
            source, srcDb, dstDb, user, originJobId, txnStamp, notes, cancellationToken).ConfigureAwait(false);
        if (itemError is not null)
            return TransferEnsureResult.Fail(itemError);

        return TransferEnsureResult.Ok(string.Join(" ", notes));
    }

    public async Task<IReadOnlyList<DryRunCheck>> PreviewAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, CancellationToken cancellationToken = default)
    {
        var checks = new List<DryRunCheck>();
        var supplier = await PreviewSupplierAsync(source, srcDb, dstDb, destParty, requiresParty, cancellationToken)
            .ConfigureAwait(false);
        if (supplier is not null)
            checks.Add(supplier);

        checks.AddRange(await PreviewItemsAsync(source, srcDb, dstDb, cancellationToken).ConfigureAwait(false));
        return checks;
    }

    public static ValidationResult IgnoreAutoCreated(ValidationResult source, IReadOnlyList<DryRunCheck> preview)
    {
        var errors = source.Errors.Where(error => !IsCovered(error, preview)).ToList();
        return errors.Count == 0
            ? ValidationResult.Pass()
            : new ValidationResult { Ok = false, Errors = errors, Warnings = source.Warnings };
    }

    private async Task<string?> EnsureSupplierAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, string user, int? originJobId, string txnStamp,
        List<string> notes, CancellationToken cancellationToken)
    {
        var code = (source.PartyCode ?? "").Trim();
        if (!NeedsSupplier(requiresParty, destParty, code))
            return null;

        var existing = await _mapping.FindPartyMapAsync(
            srcDb.DbId, dstDb.DbId, EntityCodes.Customer, code, EntityCodes.Supplier, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return null;

        var record = await LoadPartyAsync(srcDb, code, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return $"Customer '{code}' was not found on the source.";

        var destCode = await ResolveDestSupplierCodeAsync(dstDb, code, record.Description, cancellationToken)
            .ConfigureAwait(false);
        record.Code = destCode;

        if (!await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Supplier, destCode, cancellationToken).ConfigureAwait(false))
        {
            var write = await _write.CreateMasterAsync(
                dstDb, EntityCodes.Supplier, record, txnStamp, srcDb, EntityCodes.Customer, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Success)
                return write.Message;
            notes.Add($"Created supplier {destCode} on {dstDb.DisplayName}.");
        }
        else
        {
            notes.Add($"Linked existing supplier {destCode} on {dstDb.DisplayName}.");
        }

        return await LinkAsync(
            srcDb, dstDb, EntityCodes.Customer, EntityCodes.Supplier, code, destCode, record, user, originJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> EnsureItemsAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string user, int? originJobId, string txnStamp, List<string> notes, CancellationToken cancellationToken)
    {
        foreach (var code in ItemCodes(source))
        {
            var error = await EnsureItemAsync(
                code, srcDb, dstDb, user, originJobId, txnStamp, notes, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                return error;
        }
        return null;
    }

    private async Task<string?> EnsureItemAsync(
        string code, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string user, int? originJobId, string txnStamp, List<string> notes, CancellationToken cancellationToken)
    {
        var existing = await _mapping.FindAsync(srcDb.DbId, dstDb.DbId, EntityCodes.Item, code, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return null;

        var record = await LoadItemAsync(srcDb, code, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return $"Item '{code}' was not found on the source.";
        record.Code = code;

        if (await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Item, code, cancellationToken).ConfigureAwait(false))
        {
            notes.Add($"Linked existing item {code} on {dstDb.DisplayName}.");
            return await LinkAsync(
                srcDb, dstDb, EntityCodes.Item, EntityCodes.Item, code, code, record, user, originJobId, cancellationToken)
                .ConfigureAwait(false);
        }

        var prereq = await _itemPrereqs.EnsureAsync(dstDb, srcDb, record, cancellationToken).ConfigureAwait(false);
        if (!prereq.Ok)
            return prereq.Message;

        var write = await _write.CreateMasterAsync(
            dstDb, EntityCodes.Item, record, txnStamp, srcDb, EntityCodes.Item, cancellationToken).ConfigureAwait(false);
        if (!write.Success)
            return AppendDiagnosis(write.Message, prereq.LastApiDiagnosis);

        notes.Add($"Created item {code} on {dstDb.DisplayName}.");
        return await LinkAsync(
            srcDb, dstDb, EntityCodes.Item, EntityCodes.Item, code, code, record, user, originJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DryRunCheck?> PreviewSupplierAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string? destParty, bool requiresParty, CancellationToken cancellationToken)
    {
        var code = (source.PartyCode ?? "").Trim();
        if (!NeedsSupplier(requiresParty, destParty, code))
            return null;

        var existing = await _mapping.FindPartyMapAsync(
            srcDb.DbId, dstDb.DbId, EntityCodes.Customer, code, EntityCodes.Supplier, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return null;

        var record = await LoadPartyAsync(srcDb, code, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return Check("Supplier", false, $"Customer '{code}' was not found on the source.");

        var destCode = await ResolveDestSupplierCodeAsync(dstDb, code, record.Description, cancellationToken)
            .ConfigureAwait(false);
        var onDest = await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Supplier, destCode, cancellationToken)
            .ConfigureAwait(false);
        return onDest
            ? Check("Supplier", true, $"Supplier {destCode} already exists on {dstDb.DisplayName}. Would link the mapping.")
            : Check("Supplier", true, $"Would create supplier {destCode} on {dstDb.DisplayName}.");
    }

    private async Task<string> ResolveDestSupplierCodeAsync(
        RegisteredDatabase dstDb, string customerCode, string companyName, CancellationToken cancellationToken)
    {
        if (await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Supplier, customerCode, cancellationToken).ConfigureAwait(false))
            return customerCode;
        if (!await CustomerCodeTakenAsync(dstDb, customerCode, cancellationToken).ConfigureAwait(false))
            return customerCode;

        var suppliers = await _read.GetSuppliersAsync(dstDb, cancellationToken).ConfigureAwait(false);
        var codes = suppliers.Select(s => s.Code).Append(customerCode);
        return PartnerCodeRules.SuggestNext(EntityCodes.Supplier, codes, companyName);
    }

    private async Task<bool> CustomerCodeTakenAsync(
        RegisteredDatabase dstDb, string customerCode, CancellationToken cancellationToken)
    {
        if (await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Customer, customerCode, cancellationToken).ConfigureAwait(false))
            return true;
        return await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Account, customerCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<DryRunCheck>> PreviewItemsAsync(
        DocumentHeader source, RegisteredDatabase srcDb, RegisteredDatabase dstDb, CancellationToken cancellationToken)
    {
        var checks = new List<DryRunCheck>();
        foreach (var code in ItemCodes(source))
        {
            var check = await PreviewItemAsync(code, srcDb, dstDb, cancellationToken).ConfigureAwait(false);
            if (check is not null)
                checks.Add(check);
        }
        return checks;
    }

    private async Task<DryRunCheck?> PreviewItemAsync(
        string code, RegisteredDatabase srcDb, RegisteredDatabase dstDb, CancellationToken cancellationToken)
    {
        var existing = await _mapping.FindAsync(srcDb.DbId, dstDb.DbId, EntityCodes.Item, code, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return null;

        var onDest = await _read.MasterExistsByCodeAsync(dstDb, EntityCodes.Item, code, cancellationToken)
            .ConfigureAwait(false);
        if (onDest)
            return Check("Item " + code, true, $"Item {code} already exists on {dstDb.DisplayName}. Would link the mapping.");

        var record = await LoadItemAsync(srcDb, code, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Check("Item " + code, false, $"Item '{code}' was not found on the source.")
            : Check("Item " + code, true, $"Would create item {code} on {dstDb.DisplayName}.");
    }

    private async Task<string?> LinkAsync(
        RegisteredDatabase srcDb, RegisteredDatabase dstDb,
        string srcEntity, string dstEntity, string masterCode, string subCode, MasterRecord record,
        string user, int? originJobId, CancellationToken cancellationToken)
    {
        var desc = string.IsNullOrWhiteSpace(record.Description) ? masterCode : record.Description.Trim();
        var (ok, message, _) = await _mapping.ConfirmPairAsync(new EntityMapping
        {
            MasterDbId = srcDb.DbId,
            SubDbId = dstDb.DbId,
            EntityCode = srcEntity,
            SrcEntityCode = srcEntity,
            DstEntityCode = dstEntity,
            MasterCode = masterCode,
            SubCode = subCode,
            MasterDesc = desc,
            SubDesc = desc
        }, string.IsNullOrWhiteSpace(user) ? "sync" : user, false, originJobId, cancellationToken)
            .ConfigureAwait(false);
        return ok ? null : message;
    }

    private Task<MasterRecord?> LoadPartyAsync(RegisteredDatabase srcDb, string code, CancellationToken cancellationToken) =>
        _read.GetPartyDetailAsync(srcDb, EntityCodes.Customer, code, cancellationToken);

    private Task<MasterRecord?> LoadItemAsync(RegisteredDatabase srcDb, string code, CancellationToken cancellationToken) =>
        _read.GetItemDetailAsync(srcDb, code, cancellationToken);

    private static bool NeedsSupplier(bool requiresParty, string? destParty, string code) =>
        requiresParty
        && code.Length > 0
        && string.Equals(destParty, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ItemCodes(DocumentHeader source) =>
        source.Lines
            .Select(line => (line.ItemCode ?? "").Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsCovered(string error, IReadOnlyList<DryRunCheck> preview)
    {
        if (preview.Any(c => c.Ok && c.Name == "Supplier")
            && error.Contains("does not exist on the destination (no mapping)", StringComparison.OrdinalIgnoreCase))
            return true;

        return preview.Any(c =>
            c.Ok
            && c.Name.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)
            && error.Contains("no item mapping for " + c.Name["Item ".Length..] + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static string AppendDiagnosis(string message, string? diagnosis) =>
        string.IsNullOrWhiteSpace(diagnosis) ? message : message + " " + diagnosis;

    private static DryRunCheck Check(string name, bool ok, string detail) =>
        new() { Name = name, Ok = ok, Detail = detail };
}
