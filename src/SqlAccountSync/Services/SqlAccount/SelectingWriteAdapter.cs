using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount.Sdk;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.SqlAccount;

/// <summary>Routes writes to REST or SDK based on the nav toggle (SYNC_SETTING).</summary>
public sealed class SelectingWriteAdapter : ISqlAccountWriteAdapter
{
    private readonly SqlAccountSdkWriteAdapter _sdk;
    private readonly SqlAccountApiWriteAdapter _api;
    private readonly IWriteProviderSettings _settings;

    public SelectingWriteAdapter(
        SqlAccountSdkWriteAdapter sdk,
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
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.CreateStockGroupAsync(db, group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WriteResult> UpdateStockGroupAsync(
        RegisteredDatabase db, StockGroupRecord group, CancellationToken cancellationToken = default)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.UpdateStockGroupAsync(db, group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WriteResult> DeleteStockGroupAsync(
        RegisteredDatabase db, string code, CancellationToken cancellationToken = default)
    {
        var w = await ActiveAsync(cancellationToken).ConfigureAwait(false);
        return await w.DeleteStockGroupAsync(db, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ISqlAccountWriteAdapter> ActiveAsync(CancellationToken cancellationToken) =>
        await IsSdkAsync(cancellationToken).ConfigureAwait(false) ? _sdk : _api;

    private async Task<bool> IsSdkAsync(CancellationToken cancellationToken)
    {
        var mode = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return SqlAccountingWriteProviders.IsSdk(mode);
    }
}
