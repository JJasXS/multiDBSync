using SqlAccountSync.Models;

namespace SqlAccountSync.Services.SqlAccount;

/// <summary>Records intended writes without touching SQL Account.</summary>
public sealed class DryRunWriteAdapter : ISqlAccountWriteAdapter
{
    public List<string> Actions { get; } = [];

    public Task<WriteResult> CreateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null,
        CancellationToken cancellationToken = default)
    {
        var flip = sourceEntityCode is not null
            && !string.Equals(sourceEntityCode, targetEntityCode, StringComparison.OrdinalIgnoreCase);
        var label = flip ? $"{sourceEntityCode}→{targetEntityCode}" : targetEntityCode;
        Actions.Add($"WOULD create {label} {record.Code} on {db.DisplayName}");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        Actions.Add($"WOULD UPDATE {targetEntityCode} {record.Code} on {db.DisplayName} (dockey={destDocKey})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified.", DocNo = record.Code });
    }

    public Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code, CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD DELETE {targetEntityCode} {code} on {db.DisplayName}");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified." });
    }

    public Task<WriteResult> CreateDocumentAsync(
        RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp,
        CancellationToken cancellationToken = default)
    {
        Actions.Add($"WOULD CREATE {apiPath} as {document.DocType} on {db.DisplayName} ({document.Lines.Count} lines, {document.Amount:N2})");
        return Task.FromResult(new WriteResult { Success = true, Message = "Dry run: no data was modified." });
    }
}
