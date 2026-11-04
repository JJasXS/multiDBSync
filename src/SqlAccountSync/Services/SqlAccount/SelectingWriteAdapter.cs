using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.SqlAccount;

/// <summary>Routes writes to REST or SDK based on the nav toggle (SYNC_SETTING).</summary>
public sealed class SelectingWriteAdapter : ISqlAccountWriteAdapter
{
    private readonly SdkWriteAdapter _sdk;
    private readonly SqlAccountApiWriteAdapter _api;
    private readonly IWriteProviderSettings _settings;

    public SelectingWriteAdapter(
        SdkWriteAdapter sdk,
        SqlAccountApiWriteAdapter api,
        IWriteProviderSettings settings)
    {
        _sdk = sdk;
        _api = api;
        _settings = settings;
    }

    public async Task<WriteResult> CreateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null,
        CancellationToken cancellationToken = default)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.CreateMasterAsync(db, targetEntityCode, record, txnStamp, sourceDb, sourceEntityCode, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WriteResult> UpdateMasterAsync(
        RegisteredDatabase db, string targetEntityCode, MasterRecord record, string txnStamp,
        int destDocKey, int destParentDocKey, CancellationToken cancellationToken = default,
        RegisteredDatabase? sourceDb = null, string? sourceEntityCode = null)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.UpdateMasterAsync(
                db, targetEntityCode, record, txnStamp, destDocKey, destParentDocKey,
                cancellationToken, sourceDb, sourceEntityCode)
            .ConfigureAwait(false);
    }

    public async Task<WriteResult> DeleteMasterAsync(
        RegisteredDatabase db, string targetEntityCode, string code,
        CancellationToken cancellationToken = default)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.DeleteMasterAsync(db, targetEntityCode, code, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WriteResult> CreateDocumentAsync(
        RegisteredDatabase db, string apiPath, DocumentHeader document, string txnStamp,
        CancellationToken cancellationToken = default)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.CreateDocumentAsync(db, apiPath, document, txnStamp, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WriteResult> CreateStockGroupAsync(
        RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
    {
        if (await IsSdkAsync(cancellationToken).ConfigureAwait(false))
            return await _sdk.CreateStockGroupAsync(db, group, cancellationToken).ConfigureAwait(false);
        return await _api.CreateStockGroupAsync(db, group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WriteResult> CreateUomAsync(
        RegisteredDatabase db, string uom, CancellationToken cancellationToken = default)
    {
        if (await IsSdkAsync(cancellationToken).ConfigureAwait(false))
            return await _sdk.CreateUomAsync(db, uom, cancellationToken).ConfigureAwait(false);
        return new WriteResult
        {
            Success = false,
            Message = "UOM create via SQL Account REST is not available; switch to SDK or create the UOM in SQL Account."
        };
    }

    private async Task<ISqlAccountWriteAdapter> ActiveAsync(CancellationToken cancellationToken) =>
        await IsSdkAsync(cancellationToken).ConfigureAwait(false) ? _sdk : _api;

    private async Task<bool> IsSdkAsync(CancellationToken cancellationToken)
    {
        var mode = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return SqlAccountingWriteProviders.IsSdk(mode);
    }
}
