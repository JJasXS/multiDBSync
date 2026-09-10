using SqlAccountSync.Models;
using SqlAccountSync.Services.Mapping;

namespace SqlAccountSync.Services.Validation;

public interface IValidationService
{
    ValidationResult ValidateMaster(MasterRecord record, string entityCode);
    Task<ValidationResult> ValidateMappedDocumentAsync(
        DocumentHeader source, int masterDbId, int subDbId, string? destPartyEntity = null,
        bool requiresParty = true, CancellationToken cancellationToken = default);
    ValidationResult ValidateDatabase(RegisteredDatabase? db, string label);
}

public sealed class ValidationService : IValidationService
{
    private readonly IMappingService _mapping;

    public ValidationService(IMappingService mapping) => _mapping = mapping;

    public ValidationResult ValidateMaster(MasterRecord record, string entityCode)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(entityCode))
            errors.Add("Entity type is required.");
        if (string.IsNullOrWhiteSpace(record.Code))
            errors.Add("Code is required.");
        if (string.IsNullOrWhiteSpace(record.Description))
            errors.Add("Description is required.");
        return errors.Count == 0 ? ValidationResult.Pass() : ValidationResult.Fail(errors.ToArray());
    }

    public ValidationResult ValidateDatabase(RegisteredDatabase? db, string label)
    {
        if (db is null)
            return ValidationResult.Fail($"{label} database was not found.");
        if (!db.IsActive)
            return ValidationResult.Fail($"{label} database is disabled.");
        if (string.IsNullOrWhiteSpace(db.FbHost) || string.IsNullOrWhiteSpace(db.FbPath))
            return ValidationResult.Fail($"{label} connection is incomplete.");
        return ValidationResult.Pass();
    }

    public async Task<ValidationResult> ValidateMappedDocumentAsync(
        DocumentHeader source, int masterDbId, int subDbId, string? destPartyEntity = null,
        bool requiresParty = true, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(source.DocNo))
            errors.Add("Source document number is required.");
        if (source.DocDate is null)
            errors.Add("Document date is required.");
        if (requiresParty && string.IsNullOrWhiteSpace(source.PartyCode))
            errors.Add("Customer/supplier code is required.");
        if (source.Amount < 0)
            errors.Add("Document amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(source.Currency))
            errors.Add("Currency is required.");
        if (source.Lines.Count == 0)
            errors.Add("Document has no lines.");

        if (requiresParty)
        {
            var partyMap = await _mapping.FindPartyMapAsync(
                masterDbId, subDbId, EntityCodes.Customer, source.PartyCode, destPartyEntity, cancellationToken);
            if (partyMap is null)
                errors.Add($"Supplier/customer '{source.PartyCode}' does not exist on the destination (no mapping). Push the partner from source to destination first.");
        }

        foreach (var line in source.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemCode))
                errors.Add($"Line {line.Seq}: item code is required.");
            if (line.Qty <= 0)
                errors.Add($"Line {line.Seq}: quantity must be greater than zero.");
            if (line.UnitPrice < 0)
                errors.Add($"Line {line.Seq}: price cannot be negative.");
            var itemMap = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Item, line.ItemCode, cancellationToken);
            if (itemMap is null)
                errors.Add($"Line {line.Seq}: no item mapping for {line.ItemCode}.");
            if (!string.IsNullOrWhiteSpace(line.TaxCode) && line.TaxCode.Trim() != "----")
            {
                var tax = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.TaxCode, line.TaxCode, cancellationToken);
                if (tax is null)
                    errors.Add($"Line {line.Seq}: no tax mapping for {line.TaxCode}.");
            }
            if (!string.IsNullOrWhiteSpace(line.Warehouse) && line.Warehouse.Trim() != "----")
            {
                var wh = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, line.Warehouse, cancellationToken);
                if (wh is null)
                    errors.Add($"Line {line.Seq}: no warehouse mapping for {line.Warehouse}.");
            }

            await ValidateStockLocationsAsync(errors, line, masterDbId, subDbId, cancellationToken);
        }

        return errors.Count == 0 ? ValidationResult.Pass() : ValidationResult.Fail(errors.ToArray());
    }

    private async Task ValidateStockLocationsAsync(
        List<string> errors, DocumentLine line, int masterDbId, int subDbId, CancellationToken cancellationToken)
    {
        var from = (line.FromLocation ?? "").Trim();
        var to = (line.ToLocation ?? "").Trim();
        if (from.Length == 0 && to.Length == 0)
            return;

        if (from.Length == 0 || to.Length == 0)
        {
            errors.Add($"Line {line.Seq}: both from-location and to-location are required.");
            return;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Line {line.Seq}: from-location and to-location must differ.");

        if (from != "----")
        {
            var fromMap = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, from, cancellationToken);
            if (fromMap is null)
                errors.Add($"Line {line.Seq}: no warehouse mapping for from-location {from}.");
        }

        if (to != "----")
        {
            var toMap = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, to, cancellationToken);
            if (toMap is null)
                errors.Add($"Line {line.Seq}: no warehouse mapping for to-location {to}.");
        }
    }
}
