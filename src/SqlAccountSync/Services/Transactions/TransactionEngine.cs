using System.Globalization;
using System.Text.Json;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Validation;

namespace SqlAccountSync.Services.Transactions;

public interface ITransactionEngine
{
    Task<(int TxnPk, string TxnId, string Message)> EnqueueAsync(
        int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey, bool dryRun, string user, bool confirmed,
        IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default);
    Task<DryRunResult> DryRunAsync(
        int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey,
        IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default);
    Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default);
    Task RetryAsync(int txnPk, string user, CancellationToken cancellationToken = default);
    Task<SyncTransaction?> GetAsync(int txnPk, CancellationToken cancellationToken = default);
    Task<SyncTransaction?> GetByTxnIdAsync(string txnId, CancellationToken cancellationToken = default);
    Task<List<SyncTransaction>> ListAsync(int take = 200, CancellationToken cancellationToken = default);
    Task<List<TxnEvent>> TimelineAsync(int txnPk, CancellationToken cancellationToken = default);
    Task<List<(string Code, string Name, string Src, string Dst, string DstPath)>> ListTypesAsync(CancellationToken cancellationToken = default);
}

public sealed class TransactionEngine : ITransactionEngine
{
    private readonly CentralDb _db;
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly IMappingService _mapping;
    private readonly IValidationService _validation;
    private readonly IJobQueue _queue;
    private readonly IAuditService _audit;
    private readonly IErrorLogService _errors;

    public TransactionEngine(
        CentralDb db,
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        IMappingService mapping,
        IValidationService validation,
        IJobQueue queue,
        IAuditService audit,
        IErrorLogService errors)
    {
        _db = db;
        _registry = registry;
        _read = read;
        _write = write;
        _mapping = mapping;
        _validation = validation;
        _queue = queue;
        _audit = audit;
        _errors = errors;
    }

    public Task<List<(string Code, string Name, string Src, string Dst, string DstPath)>> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<(string, string, string, string, string)>());
        return _db.QueryAsync("SELECT * FROM SYNC_TXNTYPE WHERE ISACTIVE=1 ORDER BY TXNTYPECODE", r => (
            CentralDb.Str(r, "TXNTYPECODE"),
            CentralDb.Str(r, "DISPLAYNAME"),
            CentralDb.Str(r, "SRCDOCTYPE"),
            CentralDb.Str(r, "DSTDOCTYPE"),
            CentralDb.Str(r, "DSTAPIPATH")
        ), cancellationToken: cancellationToken);
    }

    public async Task<(int TxnPk, string TxnId, string Message)> EnqueueAsync(
        int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey, bool dryRun, string user, bool confirmed,
        IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default)
    {
        if (!dryRun && !confirmed)
            return (0, "", "Confirmation is required before creating accounting documents.");

        var srcDb = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);
        var dstDb = await _registry.GetAsync(destDbId, cancellationToken).ConfigureAwait(false);
        var types = await ListTypesAsync(cancellationToken).ConfigureAwait(false);
        var type = types.FirstOrDefault(t => t.Code == txnTypeCode);
        if (type.Code is null)
            return (0, "", "Unknown transaction type.");

        DocumentHeader source;
        try
        {
            var found = await _read.GetDocumentAsync(srcDb!, type.Src, srcDocNoOrKey, cancellationToken).ConfigureAwait(false);
            if (found is null)
                return (0, "", $"Source document was not found in {srcDb!.DisplayName}.");
            source = found;
        }
        catch (Exception ex)
        {
            return (0, "", FirebirdErrorMapper.Describe(ex));
        }

        var qtyError = TryApplyQtyOverrides(source, qtyBySeq);
        if (qtyError is not null)
            return (0, "", qtyError);

        var idem = IdempotencyKey.ForTransaction(srcDb!.DbCode, dstDb!.DbCode, txnTypeCode, type.Src, source.DocKey, source.DocNo);
        var existing = await _db.QuerySingleAsync("SELECT FIRST 1 * FROM SYNC_TXN WHERE IDEMKEY=@IDEMKEY", MapTxn, new { IDEMKEY = idem }, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status == JobStatuses.Completed)
            return (existing.TxnPk, existing.TxnId, "This document was already synchronized. No duplicate will be created.");

        int txnPk;
        string txnId;
        if (existing is null)
        {
            txnPk = (int)await _db.NextIdAsync("GEN_SYNC_TXN", cancellationToken).ConfigureAwait(false);
            var seq = await _db.NextIdAsync("GEN_SYNC_TXNSEQ", cancellationToken).ConfigureAwait(false);
            txnId = $"TXN-{DateTime.UtcNow:yyyyMMdd}-{seq.ToString("000000", CultureInfo.InvariantCulture)}";
            try
            {
                await _db.ExecuteAsync(@"
INSERT INTO SYNC_TXN (TXNPK, TXNID, TXNTYPECODE, SOURCEDBID, DESTDBID, SRCDOCTYPE, SRCDOCKEY, SRCDOCNO, DSTDOCTYPE, IDEMKEY, STATUS, RETRYCOUNT, CREATEDAT, CREATEDBY, UPDATEDAT)
VALUES (@TXNPK, @TXNID, @TXNTYPECODE, @SOURCEDBID, @DESTDBID, @SRCDOCTYPE, @SRCDOCKEY, @SRCDOCNO, @DSTDOCTYPE, @IDEMKEY, 'PENDING', 0, CURRENT_TIMESTAMP, @USER, CURRENT_TIMESTAMP)",
                    new
                    {
                        TXNPK = txnPk,
                        TXNID = txnId,
                        TXNTYPECODE = txnTypeCode,
                        SOURCEDBID = sourceDbId,
                        DESTDBID = destDbId,
                        SRCDOCTYPE = type.Src,
                        SRCDOCKEY = source.DocKey,
                        SRCDOCNO = source.DocNo,
                        DSTDOCTYPE = type.Dst,
                        IDEMKEY = idem,
                        USER = user
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (FirebirdSql.Data.FirebirdClient.FbException ex) when (ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
            {
                existing = await _db.QuerySingleAsync("SELECT FIRST 1 * FROM SYNC_TXN WHERE IDEMKEY=@IDEMKEY", MapTxn, new { IDEMKEY = idem }, cancellationToken).ConfigureAwait(false);
                return (existing!.TxnPk, existing.TxnId, "An existing synchronization record was reused. No duplicate will be created.");
            }
        }
        else
        {
            txnPk = existing.TxnPk;
            txnId = existing.TxnId;
        }

        await AddEvent(txnPk, "Created", "SUCCESS", $"Queued from {source.DocType} {source.DocNo}", cancellationToken).ConfigureAwait(false);
        // IDEMKEY is VARCHAR(64); hash so "|EXEC"/ "|DRY" suffixes never truncate.
        var jobIdem = IdempotencyKey.Hash(idem + (dryRun ? "|DRY" : "|EXEC"));
        await _queue.EnqueueAsync(new SyncJob
        {
            JobKind = dryRun ? "TXN_DRYRUN" : "TXN",
            TxnId = txnPk,
            SourceDbId = sourceDbId,
            DestDbId = destDbId,
            PayloadJson = JsonSerializer.Serialize(new TxnPayload
            {
                TxnPk = txnPk,
                TxnType = txnTypeCode,
                DstApiPath = type.DstPath,
                SrcDoc = srcDocNoOrKey,
                QtyBySeq = qtyBySeq is null ? null : new Dictionary<int, decimal>(qtyBySeq)
            }),
            DryRun = dryRun,
            IdemKey = jobIdem,
            CreatedBy = user
        }, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = user,
            Action = dryRun ? "TXN_DRYRUN" : "TXN_ENQUEUE",
            DbId = destDbId,
            Entity = txnTypeCode,
            RecordId = txnId,
            Result = "QUEUED",
            RequestId = txnPk.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        return (txnPk, txnId, dryRun ? "Dry-run queued. The destination will not be modified." : "Transaction queued for execution.");
    }

    public async Task<DryRunResult> DryRunAsync(
        int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey,
        IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default)
    {
        var result = new DryRunResult();
        var srcDb = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);
        var dstDb = await _registry.GetAsync(destDbId, cancellationToken).ConfigureAwait(false);
        Add(result, "Source database", _validation.ValidateDatabase(srcDb, "Source"));
        Add(result, "Destination database", _validation.ValidateDatabase(dstDb, "Destination"));
        var types = await ListTypesAsync(cancellationToken).ConfigureAwait(false);
        var type = types.FirstOrDefault(t => t.Code == txnTypeCode);
        if (type.Code is null)
        {
            result.Checks.Add(new DryRunCheck { Name = "Transaction type", Ok = false, Detail = "Unknown type." });
            result.Summary = "Dry run failed. No data was modified.";
            return result;
        }
        DocumentHeader? source;
        try
        {
            source = await _read.GetDocumentAsync(srcDb!, type.Src, srcDocNoOrKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Checks.Add(new DryRunCheck
            {
                Name = "Source document",
                Ok = false,
                Detail = FirebirdErrorMapper.Describe(ex)
            });
            result.Summary = "Dry run failed. No data was modified.";
            return result;
        }
        if (source is null)
        {
            result.Checks.Add(new DryRunCheck
            {
                Name = "Source document",
                Ok = false,
                Detail = $"No {type.Src} document '{(srcDocNoOrKey ?? "").Trim()}' in {srcDb!.DisplayName}."
            });
            result.Summary = "Dry run failed. No data was modified.";
            return result;
        }

        var qtyError = TryApplyQtyOverrides(source, qtyBySeq);
        if (qtyError is not null)
        {
            result.Checks.Add(new DryRunCheck { Name = "Quantity overrides", Ok = false, Detail = qtyError });
            result.Summary = "Dry run failed. No data was modified.";
            return result;
        }

        result.Checks.Add(new DryRunCheck { Name = "Source document", Ok = true, Detail = $"{source.DocType} {source.DocNo}" });
        var requiresParty = !IsStockTransfer(type.Dst);
        var destParty = DestPartyEntityFor(type.Dst);
        var mapped = await TranslateAsync(source, sourceDbId, destDbId, destParty, requiresParty, cancellationToken).ConfigureAwait(false);
        var v = await _validation.ValidateMappedDocumentAsync(
            source, sourceDbId, destDbId, destParty, requiresParty, cancellationToken).ConfigureAwait(false);
        Add(result, "Mappings and fields", v);
        foreach (var line in source.Lines)
        {
            var map = await _mapping.FindAsync(sourceDbId, destDbId, EntityCodes.Item, line.ItemCode, cancellationToken).ConfigureAwait(false);
            var locDetail = string.IsNullOrWhiteSpace(line.FromLocation) && string.IsNullOrWhiteSpace(line.ToLocation)
                ? $"→ {(map?.SubCode ?? "unmapped")} qty {line.Qty}"
                : $"→ {(map?.SubCode ?? "unmapped")} qty {line.Qty} ({line.FromLocation} → {line.ToLocation})";
            result.Checks.Add(new DryRunCheck
            {
                Name = $"Item {line.ItemCode}",
                Ok = map is not null,
                Detail = map is null ? "Unmapped" : locDetail
            });
        }
        result.WouldSucceed = result.Checks.All(c => c.Ok);
        result.Summary = result.WouldSucceed
            ? $"Would create {type.Dst} on {dstDb!.DisplayName} from {source.DocNo}. No data was modified."
            : "Dry run found problems. No data was modified.";
        _ = mapped;
        return result;
    }

    public async Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<TxnPayload>(job.PayloadJson);
        if (payload is null)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, "Job payload is invalid.", cancellationToken);
            return;
        }
        var txn = await GetAsync(payload.TxnPk, cancellationToken).ConfigureAwait(false);
        if (txn is null)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, "Transaction record missing.", cancellationToken);
            return;
        }

        try
        {
            await _db.ExecuteAsync("UPDATE SYNC_TXN SET STATUS='PROCESSING', STARTEDAT=CURRENT_TIMESTAMP, UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK",
                new { TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
            await AddEvent(txn.TxnPk, "Started", "SUCCESS", job.DryRun ? "Dry run" : "Execute", cancellationToken).ConfigureAwait(false);

            var reconciled = await ReconcileAsync(txn, cancellationToken).ConfigureAwait(false);
            if (reconciled.DestinationExists)
            {
                await RepairCompleted(txn, reconciled, cancellationToken).ConfigureAwait(false);
                await _queue.CompleteAsync(job.JobId, JobStatuses.Completed, "Destination already existed; record repaired.", cancellationToken);
                return;
            }

            var srcDb = await _registry.GetAsync(txn.SourceDbId, cancellationToken).ConfigureAwait(false);
            var dstDb = await _registry.GetAsync(txn.DestDbId, cancellationToken).ConfigureAwait(false);
            var source = await _read.GetDocumentAsync(srcDb!, txn.SrcDocType, string.IsNullOrWhiteSpace(txn.SrcDocKey) ? txn.SrcDocNo : txn.SrcDocKey, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                await Fail(txn, job, JobStatuses.Failed, "Source document was not found.", cancellationToken);
                return;
            }

            var qtyError = TryApplyQtyOverrides(source, payload.QtyBySeq);
            if (qtyError is not null)
            {
                await Fail(txn, job, JobStatuses.Failed, qtyError, cancellationToken);
                return;
            }

            var requiresParty = !IsStockTransfer(txn.DstDocType);
            var destParty = DestPartyEntityFor(txn.DstDocType);
            var v = await _validation.ValidateMappedDocumentAsync(
                source, txn.SourceDbId, txn.DestDbId, destParty, requiresParty, cancellationToken).ConfigureAwait(false);
            await AddEvent(txn.TxnPk, "Validation", v.Ok ? "SUCCESS" : "FAIL", v.Ok ? "Passed" : string.Join(" ", v.Errors), cancellationToken).ConfigureAwait(false);
            if (!v.Ok)
            {
                var status = v.Errors.Any(e => e.Contains("mapping", StringComparison.OrdinalIgnoreCase))
                    ? JobStatuses.WaitingForMapping
                    : JobStatuses.Failed;
                await Fail(txn, job, status, string.Join(" ", v.Errors), cancellationToken);
                return;
            }

            var translated = await TranslateAsync(source, txn.SourceDbId, txn.DestDbId, destParty, requiresParty, cancellationToken).ConfigureAwait(false);
            await AddEvent(txn.TxnPk, "Mapping", "SUCCESS", "All required mappings found.", cancellationToken).ConfigureAwait(false);

            ISqlAccountWriteAdapter writer = job.DryRun ? new DryRunWriteAdapter() : _write;
            var write = await writer.CreateDocumentAsync(dstDb!, payload.DstApiPath, translated, txn.TxnId, cancellationToken).ConfigureAwait(false);
            if (!write.Success)
            {
                await Fail(txn, job, JobStatuses.Retrying, write.Message, cancellationToken);
                return;
            }

            if (!job.DryRun)
            {
                await _db.ExecuteAsync(@"
UPDATE SYNC_TXN SET STATUS='COMPLETED', DSTDOCKEY=@DSTDOCKEY, DSTDOCNO=@DSTDOCNO, COMPLETEDAT=CURRENT_TIMESTAMP, UPDATEDAT=CURRENT_TIMESTAMP
WHERE TXNPK=@TXNPK",
                    new { DSTDOCKEY = write.DocKey, DSTDOCNO = write.DocNo, TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
                await _registry.MarkSyncOkAsync(txn.DestDbId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _db.ExecuteAsync("UPDATE SYNC_TXN SET STATUS='COMPLETED', UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK",
                    new { TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
            }

            await AddEvent(txn.TxnPk, job.DryRun ? "DryRun" : "Created destination", "SUCCESS",
                job.DryRun ? "No data was modified." : $"{txn.DstDocType} {write.DocNo} ({write.DocKey})", cancellationToken).ConfigureAwait(false);
            await AddEvent(txn.TxnPk, "Completed", "SUCCESS", "", cancellationToken).ConfigureAwait(false);
            await _queue.CompleteAsync(job.JobId, JobStatuses.Completed, write.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _errors.WriteAsync(new ErrorEntry
            {
                TxnPk = txn.TxnPk,
                DbId = txn.DestDbId,
                ErrorType = "TXN",
                UserMessage = "Transaction processing failed.",
                StackTrace = ex.ToString(),
                RetryCount = txn.RetryCount
            }, cancellationToken).ConfigureAwait(false);
            await Fail(txn, job, JobStatuses.Retrying, "Processing failed. The destination will be checked before any retry creates a document.", cancellationToken);
        }
    }

    public async Task RetryAsync(int txnPk, string user, CancellationToken cancellationToken = default)
    {
        var txn = await GetAsync(txnPk, cancellationToken).ConfigureAwait(false);
        if (txn is null)
            return;
        if (txn.Status is JobStatuses.Completed)
            return;

        var reconciled = await ReconcileAsync(txn, cancellationToken).ConfigureAwait(false);
        if (reconciled.DestinationExists)
        {
            await RepairCompleted(txn, reconciled, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry { UserEmail = user, Action = "TXN_RETRY_REPAIRED", RecordId = txn.TxnId, Result = "SUCCESS" }, cancellationToken);
            return;
        }

        var types = await ListTypesAsync(cancellationToken).ConfigureAwait(false);
        var type = types.FirstOrDefault(t => t.Code == txn.TxnTypeCode);
        await _db.ExecuteAsync("UPDATE SYNC_TXN SET STATUS='RETRYING', RETRYCOUNT=RETRYCOUNT+1, UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK",
            new { TXNPK = txnPk }, cancellationToken).ConfigureAwait(false);
        await _queue.EnqueueAsync(new SyncJob
        {
            JobKind = "TXN",
            TxnId = txnPk,
            SourceDbId = txn.SourceDbId,
            DestDbId = txn.DestDbId,
            PayloadJson = JsonSerializer.Serialize(new TxnPayload { TxnPk = txnPk, TxnType = txn.TxnTypeCode, DstApiPath = type.DstPath, SrcDoc = txn.SrcDocNo }),
            IdemKey = IdempotencyKey.Hash(txn.IdemKey + "|RETRY|" + Guid.NewGuid().ToString("N")[..8]),
            CreatedBy = user
        }, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry { UserEmail = user, Action = "TXN_RETRY", RecordId = txn.TxnId, Result = "QUEUED", RequestId = txn.TxnPk.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
    }

    public Task<SyncTransaction?> GetAsync(int txnPk, CancellationToken cancellationToken = default) =>
        _db.QuerySingleAsync(@"
SELECT T.*, S.DISPLAYNAME AS SOURCENAME, D.DISPLAYNAME AS DESTNAME
FROM SYNC_TXN T
JOIN SYNC_DATABASE S ON S.DBID=T.SOURCEDBID
JOIN SYNC_DATABASE D ON D.DBID=T.DESTDBID
WHERE T.TXNPK=@TXNPK", MapTxn, new { TXNPK = txnPk }, cancellationToken);

    public Task<SyncTransaction?> GetByTxnIdAsync(string txnId, CancellationToken cancellationToken = default) =>
        _db.QuerySingleAsync(@"
SELECT T.*, S.DISPLAYNAME AS SOURCENAME, D.DISPLAYNAME AS DESTNAME
FROM SYNC_TXN T
JOIN SYNC_DATABASE S ON S.DBID=T.SOURCEDBID
JOIN SYNC_DATABASE D ON D.DBID=T.DESTDBID
WHERE T.TXNID=@TXNID", MapTxn, new { TXNID = txnId }, cancellationToken);

    public Task<List<SyncTransaction>> ListAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(new List<SyncTransaction>());
        return _db.QueryAsync($@"
SELECT FIRST {Math.Clamp(take, 1, 500)} T.*, S.DISPLAYNAME AS SOURCENAME, D.DISPLAYNAME AS DESTNAME
FROM SYNC_TXN T
JOIN SYNC_DATABASE S ON S.DBID=T.SOURCEDBID
JOIN SYNC_DATABASE D ON D.DBID=T.DESTDBID
ORDER BY T.TXNPK DESC", MapTxn, cancellationToken: cancellationToken);
    }

    public Task<List<TxnEvent>> TimelineAsync(int txnPk, CancellationToken cancellationToken = default) =>
        _db.QueryAsync("SELECT * FROM SYNC_TXN_EVENT WHERE TXNPK=@TXNPK ORDER BY EVENTID", r => new TxnEvent
        {
            EventId = CentralDb.Int(r, "EVENTID"),
            TxnPk = CentralDb.Int(r, "TXNPK"),
            AtUtc = CentralDb.TimeReq(r, "ATUTC"),
            Step = CentralDb.Str(r, "STEPNAME"),
            Result = CentralDb.Str(r, "RESULT"),
            Detail = CentralDb.Str(r, "DETAIL")
        }, new { TXNPK = txnPk }, cancellationToken);

    private async Task<ReconcileResult> ReconcileAsync(SyncTransaction txn, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(txn.DstDocKey))
            return new ReconcileResult { DestinationExists = true, DocKey = txn.DstDocKey, DocNo = txn.DstDocNo, Source = "SYNC_TXN" };

        var dstDb = await _registry.GetAsync(txn.DestDbId, cancellationToken).ConfigureAwait(false);
        if (dstDb is null)
            return new ReconcileResult();

        var stamped = await _read.FindByTxnStampAsync(dstDb, txn.TxnId, cancellationToken).ConfigureAwait(false);
        if (stamped.DestinationExists)
            return stamped;

        return await _read.FindTransferAsync(dstDb, txn.SrcDocType, txn.SrcDocKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairCompleted(SyncTransaction txn, ReconcileResult found, CancellationToken cancellationToken)
    {
        await _db.ExecuteAsync(@"
UPDATE SYNC_TXN SET STATUS='COMPLETED', DSTDOCKEY=@DSTDOCKEY, DSTDOCNO=@DSTDOCNO, COMPLETEDAT=CURRENT_TIMESTAMP, UPDATEDAT=CURRENT_TIMESTAMP
WHERE TXNPK=@TXNPK",
            new { DSTDOCKEY = found.DocKey, DSTDOCNO = found.DocNo, TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
        await AddEvent(txn.TxnPk, "Reconciled", "SUCCESS", $"Destination already existed ({found.Source}). No duplicate created.", cancellationToken).ConfigureAwait(false);
    }

    private async Task Fail(SyncTransaction txn, SyncJob job, string status, string message, CancellationToken cancellationToken)
    {
        await _db.ExecuteAsync("UPDATE SYNC_TXN SET STATUS=@STATUS, ERRORMESSAGE=@ERR, UPDATEDAT=CURRENT_TIMESTAMP WHERE TXNPK=@TXNPK",
            new { STATUS = status, ERR = message, TXNPK = txn.TxnPk }, cancellationToken).ConfigureAwait(false);
        await AddEvent(txn.TxnPk, "Failed", "FAIL", message, cancellationToken).ConfigureAwait(false);
        await _queue.CompleteAsync(job.JobId, status, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentHeader> TranslateAsync(
        DocumentHeader source, int masterDbId, int subDbId, string? destPartyEntity, bool requiresParty, CancellationToken cancellationToken)
    {
        var dest = new DocumentHeader
        {
            DocType = source.DocType,
            DocDate = source.DocDate,
            PostDate = source.PostDate ?? source.DocDate,
            TaxDate = source.TaxDate ?? source.DocDate,
            DueDate = source.DueDate,
            Amount = source.Amount,
            Currency = source.Currency,
            CurrencyRate = source.CurrencyRate,
            PartyName = source.PartyName,
            Description = string.IsNullOrWhiteSpace(source.Description)
                ? (requiresParty ? "Purchase" : "Stock transfer")
                : source.Description,
            Terms = source.Terms,
            Area = source.Area,
            Agent = source.Agent,
            Project = source.Project,
            Note = source.Note,
            Fields = new Dictionary<string, string>(source.Fields, StringComparer.OrdinalIgnoreCase)
        };

        if (requiresParty)
        {
            var party = await _mapping.FindPartyMapAsync(masterDbId, subDbId, EntityCodes.Customer, source.PartyCode, destPartyEntity, cancellationToken).ConfigureAwait(false);
            dest.PartyCode = party?.SubCode ?? source.PartyCode;
            if (!string.IsNullOrWhiteSpace(party?.SubDesc))
                dest.PartyName = party.SubDesc;
        }
        else
        {
            dest.PartyCode = source.PartyCode;
        }

        foreach (var line in source.Lines)
        {
            var item = await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Item, line.ItemCode, cancellationToken).ConfigureAwait(false);
            var tax = NeedsMap(line.TaxCode)
                ? await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.TaxCode, line.TaxCode, cancellationToken).ConfigureAwait(false)
                : null;
            var wh = NeedsMap(line.Warehouse)
                ? await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, line.Warehouse, cancellationToken).ConfigureAwait(false)
                : null;
            var fromLoc = NeedsMap(line.FromLocation)
                ? await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, line.FromLocation, cancellationToken).ConfigureAwait(false)
                : null;
            var toLoc = NeedsMap(line.ToLocation)
                ? await _mapping.FindAsync(masterDbId, subDbId, EntityCodes.Warehouse, line.ToLocation, cancellationToken).ConfigureAwait(false)
                : null;
            dest.Lines.Add(new DocumentLine
            {
                Seq = line.Seq,
                ItemCode = item?.SubCode ?? line.ItemCode,
                Description = line.Description,
                Description2 = line.Description2,
                Qty = line.Qty,
                Uom = line.Uom,
                Rate = line.Rate,
                UnitPrice = line.UnitPrice,
                Amount = line.Amount,
                TaxCode = tax?.SubCode ?? (NeedsMap(line.TaxCode) ? line.TaxCode : ""),
                Warehouse = wh?.SubCode ?? (NeedsMap(line.Warehouse) ? line.Warehouse : "----"),
                FromLocation = fromLoc?.SubCode ?? (NeedsMap(line.FromLocation) ? line.FromLocation : line.FromLocation),
                ToLocation = toLoc?.SubCode ?? (NeedsMap(line.ToLocation) ? line.ToLocation : line.ToLocation),
                Batch = line.Batch,
                Project = line.Project,
                Account = line.Account,
                Disc = line.Disc,
                TaxInclusive = line.TaxInclusive,
                IrbmClassification = line.IrbmClassification,
                DeliveryDate = line.DeliveryDate ?? source.DocDate,
                Fields = new Dictionary<string, string>(line.Fields, StringComparer.OrdinalIgnoreCase)
            });
        }
        return dest;
    }

    private static string? DestPartyEntityFor(string dstDocType) => dstDocType.ToUpperInvariant() switch
    {
        "PI" or "GR" or "PR" => EntityCodes.Supplier,
        _ => null
    };

    private static bool IsStockTransfer(string? docType) =>
        string.Equals((docType ?? "").Trim(), "ST", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies per-seq quantity overrides. Returns an error message, or null on success.</summary>
    private static string? TryApplyQtyOverrides(DocumentHeader source, IReadOnlyDictionary<int, decimal>? qtyBySeq)
    {
        if (qtyBySeq is null || qtyBySeq.Count == 0)
            return null;

        foreach (var line in source.Lines)
        {
            if (!qtyBySeq.TryGetValue(line.Seq, out var qty))
                continue;
            if (qty <= 0)
                return $"Line {line.Seq}: quantity must be greater than zero.";
            if (qty > line.Qty)
                return $"Line {line.Seq}: quantity {qty} exceeds source quantity {line.Qty}.";
            line.Qty = qty;
            line.Amount = line.Qty * line.UnitPrice;
        }

        source.Amount = source.Lines.Sum(l => l.Amount);
        return null;
    }

    private static bool NeedsMap(string? code)
    {
        var c = (code ?? "").Trim();
        return c.Length > 0 && c != "----";
    }

    private async Task AddEvent(int txnPk, string step, string result, string detail, CancellationToken cancellationToken)
    {
        var id = await _db.NextIdAsync("GEN_SYNC_EVENT", cancellationToken).ConfigureAwait(false);
        await _db.ExecuteAsync(@"
INSERT INTO SYNC_TXN_EVENT (EVENTID, TXNPK, ATUTC, STEPNAME, RESULT, DETAIL)
VALUES (@EVENTID, @TXNPK, CURRENT_TIMESTAMP, @STEPNAME, @RESULT, @DETAIL)",
            new { EVENTID = id, TXNPK = txnPk, STEPNAME = step, RESULT = result, DETAIL = detail }, cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DryRunResult result, string name, ValidationResult v) =>
        result.Checks.Add(new DryRunCheck { Name = name, Ok = v.Ok, Detail = v.Ok ? "OK" : string.Join(" ", v.Errors) });

    private static SyncTransaction MapTxn(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        TxnPk = CentralDb.Int(r, "TXNPK"),
        TxnId = CentralDb.Str(r, "TXNID"),
        TxnTypeCode = CentralDb.Str(r, "TXNTYPECODE"),
        SourceDbId = CentralDb.Int(r, "SOURCEDBID"),
        DestDbId = CentralDb.Int(r, "DESTDBID"),
        SrcDocType = CentralDb.Str(r, "SRCDOCTYPE"),
        SrcDocKey = CentralDb.Str(r, "SRCDOCKEY"),
        SrcDocNo = CentralDb.Str(r, "SRCDOCNO"),
        DstDocType = CentralDb.Str(r, "DSTDOCTYPE"),
        DstDocKey = CentralDb.Str(r, "DSTDOCKEY"),
        DstDocNo = CentralDb.Str(r, "DSTDOCNO"),
        IdemKey = CentralDb.Str(r, "IDEMKEY"),
        Status = CentralDb.Str(r, "STATUS"),
        ErrorMessage = CentralDb.Str(r, "ERRORMESSAGE"),
        RetryCount = CentralDb.Int(r, "RETRYCOUNT"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT"),
        StartedAt = CentralDb.Time(r, "STARTEDAT"),
        CompletedAt = CentralDb.Time(r, "COMPLETEDAT"),
        CreatedBy = CentralDb.Str(r, "CREATEDBY"),
        UpdatedAt = CentralDb.TimeReq(r, "UPDATEDAT"),
        SourceName = Has(r, "SOURCENAME") ? CentralDb.Str(r, "SOURCENAME") : "",
        DestName = Has(r, "DESTNAME") ? CentralDb.Str(r, "DESTNAME") : ""
    };

    private static bool Has(FirebirdSql.Data.FirebirdClient.FbDataReader r, string col)
    {
        try { return r.GetOrdinal(col) >= 0; } catch { return false; }
    }

    private sealed class TxnPayload
    {
        public int TxnPk { get; set; }
        public string TxnType { get; set; } = "";
        public string DstApiPath { get; set; } = "";
        public string SrcDoc { get; set; } = "";
        public Dictionary<int, decimal>? QtyBySeq { get; set; }
    }
}
