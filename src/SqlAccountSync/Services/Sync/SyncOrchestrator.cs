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



namespace SqlAccountSync.Services.Sync;



public interface ISyncOrchestrator

{

    Task<(int JobId, string Message)> EnqueueMasterAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, bool dryRun, string user, CancellationToken cancellationToken = default);

    Task<(int Queued, int Failed, List<string> Messages)> EnqueueMasterBatchAsync(

        int sourceDbId,

        IReadOnlyList<int> destDbIds,

        string entityCode,

        IReadOnlyList<string> masterCodes,

        bool dryRun,

        string user,

        CancellationToken cancellationToken = default);

    Task<DryRunResult> DryRunMasterAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default);

    Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default);

    Task<List<SyncRule>> ListRulesAsync(int linkId, CancellationToken cancellationToken = default);

    Task<int> CountEnabledRulesAsync(CancellationToken cancellationToken = default);

    Task SaveRuleAsync(SyncRule rule, CancellationToken cancellationToken = default);

    Task SaveScopeAsync(int ruleId, string recordCode, bool enabled, CancellationToken cancellationToken = default);

}



public sealed class SyncOrchestrator : ISyncOrchestrator

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



    public SyncOrchestrator(

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



    public async Task<List<SyncRule>> ListRulesAsync(int linkId, CancellationToken cancellationToken = default)

    {

        if (!_db.IsConfigured)

            return [];

        return await _db.QueryAsync("SELECT * FROM SYNC_RULE WHERE LINKID=@LINKID ORDER BY ENTITYCODE", r => new SyncRule

        {

            RuleId = CentralDb.Int(r, "RULEID"),

            LinkId = CentralDb.Int(r, "LINKID"),

            EntityCode = CentralDb.Str(r, "ENTITYCODE"),

            Enabled = CentralDb.Flag(r, "ENABLED"),

            Direction = CentralDb.Str(r, "DIRECTION"),

            Mode = CentralDb.Str(r, "MODE"),

            ConflictStrategy = CentralDb.Str(r, "CONFLICTSTRATEGY"),

            ScheduleCron = CentralDb.Str(r, "SCHEDULECRON"),

            LastSyncAt = CentralDb.Time(r, "LASTSYNCAT")

        }, new { LINKID = linkId }, cancellationToken).ConfigureAwait(false);

    }



    public async Task<int> CountEnabledRulesAsync(CancellationToken cancellationToken = default)

    {

        if (!_db.IsConfigured)

            return 0;

        var n = await _db.ScalarAsync(

            "SELECT COUNT(*) FROM SYNC_RULE WHERE ENABLED=1",

            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(n ?? 0);

    }



    public async Task SaveRuleAsync(SyncRule rule, CancellationToken cancellationToken = default)

    {

        if (rule.RuleId == 0)

        {

            var id = (int)await _db.NextIdAsync("GEN_SYNC_RULE", cancellationToken).ConfigureAwait(false);

            await _db.ExecuteAsync(@"

INSERT INTO SYNC_RULE (RULEID, LINKID, ENTITYCODE, ENABLED, DIRECTION, MODE, CONFLICTSTRATEGY, SCHEDULECRON)

VALUES (@RULEID, @LINKID, @ENTITYCODE, @ENABLED, @DIRECTION, @MODE, @CONFLICTSTRATEGY, @SCHEDULECRON)",

                new

                {

                    RULEID = id,

                    LINKID = rule.LinkId,

                    ENTITYCODE = rule.EntityCode,

                    ENABLED = rule.Enabled ? 1 : 0,

                    DIRECTION = rule.Direction,

                    MODE = rule.Mode,

                    CONFLICTSTRATEGY = rule.ConflictStrategy,

                    SCHEDULECRON = rule.ScheduleCron

                }, cancellationToken).ConfigureAwait(false);

            return;

        }



        await _db.ExecuteAsync(@"

UPDATE SYNC_RULE SET ENABLED=@ENABLED, DIRECTION=@DIRECTION, MODE=@MODE, CONFLICTSTRATEGY=@CONFLICTSTRATEGY, SCHEDULECRON=@SCHEDULECRON

WHERE RULEID=@RULEID",

            new

            {

                RULEID = rule.RuleId,

                ENABLED = rule.Enabled ? 1 : 0,

                DIRECTION = rule.Direction,

                MODE = rule.Mode,

                CONFLICTSTRATEGY = rule.ConflictStrategy,

                SCHEDULECRON = rule.ScheduleCron

            }, cancellationToken).ConfigureAwait(false);

    }



    public async Task SaveScopeAsync(int ruleId, string recordCode, bool enabled, CancellationToken cancellationToken = default)

    {

        var existing = await _db.QuerySingleAsync(

            "SELECT FIRST 1 SCOPEID FROM SYNC_RULE_SCOPE WHERE RULEID=@RULEID AND RECORDCODE=@RECORDCODE",

            r => CentralDb.Int(r, "SCOPEID"), new { RULEID = ruleId, RECORDCODE = recordCode }, cancellationToken).ConfigureAwait(false);

        if (existing is 0)

        {

            var id = (int)await _db.NextIdAsync("GEN_SYNC_SCOPE", cancellationToken).ConfigureAwait(false);

            await _db.ExecuteAsync(@"

INSERT INTO SYNC_RULE_SCOPE (SCOPEID, RULEID, RECORDCODE, ENABLED)

VALUES (@SCOPEID, @RULEID, @RECORDCODE, @ENABLED)",

                new { SCOPEID = id, RULEID = ruleId, RECORDCODE = recordCode, ENABLED = enabled ? 1 : 0 }, cancellationToken);

            return;

        }

        await _db.ExecuteAsync("UPDATE SYNC_RULE_SCOPE SET ENABLED=@ENABLED WHERE SCOPEID=@SCOPEID",

            new { SCOPEID = existing, ENABLED = enabled ? 1 : 0 }, cancellationToken);

    }



    public async Task<(int JobId, string Message)> EnqueueMasterAsync(

        int sourceDbId, int destDbId, string entityCode, string masterCode, bool dryRun, string user, CancellationToken cancellationToken = default)

    {

        if (sourceDbId == destDbId)

            return (0, "Source and destination must be different databases.");



        var source = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);

        var dest = await _registry.GetAsync(destDbId, cancellationToken).ConfigureAwait(false);

        var dbCheck = _validation.ValidateDatabase(source, "Source");

        dbCheck.Errors.AddRange(_validation.ValidateDatabase(dest, "Destination").Errors);

        if (dbCheck.Errors.Count > 0)

            return (0, string.Join(" ", dbCheck.Errors));



        var record = await LoadSourceRecordAsync(source!, entityCode, masterCode, cancellationToken).ConfigureAwait(false);

        if (record is null)

            return (0, $"Source {entityCode} {masterCode} was not found.");



        var destEntity = PartnerSyncRules.ResolveTargetEntity(source, dest, entityCode);

        var payloadObj = new MasterSyncPayload

        {

            Record = record,

            SourceEntity = entityCode,

            DestEntity = destEntity

        };

        var payload = JsonSerializer.Serialize(payloadObj);

        var idem = IdempotencyKey.ForMasterData(

            source!.DbCode, dest!.DbCode, entityCode, destEntity, masterCode,

            IdempotencyKey.ContentHash(payload + (dryRun ? "|DRY" : "")));

        var jobId = await _queue.EnqueueAsync(new SyncJob

        {

            JobKind = dryRun ? "MASTER_DRYRUN" : "MASTER",

            SourceDbId = sourceDbId,

            DestDbId = destDbId,

            EntityCode = entityCode,

            PayloadJson = payload,

            DryRun = dryRun,

            IdemKey = idem,

            CreatedBy = user

        }, cancellationToken).ConfigureAwait(false);



        await _audit.WriteAsync(new AuditEntry

        {

            UserEmail = user,

            Action = dryRun ? "DRY_RUN_MASTER" : "ENQUEUE_MASTER",

            DbId = sourceDbId,

            Entity = entityCode,

            RecordId = masterCode,

            Result = "QUEUED"

        }, cancellationToken).ConfigureAwait(false);

        return (jobId, dryRun ? "Dry-run job queued." : "Synchronization job queued.");

    }



    public async Task<(int Queued, int Failed, List<string> Messages)> EnqueueMasterBatchAsync(

        int sourceDbId,

        IReadOnlyList<int> destDbIds,

        string entityCode,

        IReadOnlyList<string> masterCodes,

        bool dryRun,

        string user,

        CancellationToken cancellationToken = default)

    {

        var messages = new List<string>();

        var queued = 0;

        var failed = 0;

        var codes = masterCodes

            .Select(c => (c ?? "").Trim())

            .Where(c => c.Length > 0)

            .Distinct(StringComparer.OrdinalIgnoreCase)

            .ToList();

        var dests = destDbIds.Where(id => id > 0 && id != sourceDbId).Distinct().ToList();

        if (codes.Count == 0)

        {

            messages.Add("Select at least one record.");

            return (0, 0, messages);

        }

        if (dests.Count == 0)

        {

            messages.Add("Select at least one target database.");

            return (0, 0, messages);

        }



        foreach (var code in codes)

        {

            foreach (var destId in dests)

            {

                var (jobId, msg) = await EnqueueMasterAsync(sourceDbId, destId, entityCode, code, dryRun, user, cancellationToken)

                    .ConfigureAwait(false);

                if (jobId > 0)

                {

                    queued++;

                    messages.Add($"{code} → dest {destId}: job #{jobId}");

                }

                else

                {

                    failed++;

                    messages.Add($"{code} → dest {destId}: {msg}");

                }

            }

        }



        return (queued, failed, messages);

    }



    public async Task<DryRunResult> DryRunMasterAsync(

        int sourceDbId, int destDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default)

    {

        var result = new DryRunResult();

        var source = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);

        var dest = await _registry.GetAsync(destDbId, cancellationToken).ConfigureAwait(false);

        Add(result, "Source database", _validation.ValidateDatabase(source, "Source"));

        Add(result, "Destination database", _validation.ValidateDatabase(dest, "Destination"));

        if (source is null || dest is null)

        {

            result.WouldSucceed = false;

            result.Summary = "Cannot continue — database registry is incomplete.";

            return result;

        }



        var record = await LoadSourceRecordAsync(source, entityCode, masterCode, cancellationToken).ConfigureAwait(false);

        Add(result, "Source record", record is null ? ValidationResult.Fail($"{entityCode} {masterCode} not found.") : ValidationResult.Pass());

        if (record is not null)

            Add(result, "Required fields", _validation.ValidateMaster(record, entityCode));



        var destEntity = PartnerSyncRules.ResolveTargetEntity(source, dest, entityCode);

        if (!string.Equals(destEntity, entityCode, StringComparison.OrdinalIgnoreCase))

        {

            result.Checks.Add(new DryRunCheck

            {

                Name = "Target entity",

                Ok = true,

                Detail = $"Sub→Sub reciprocal sync: will create {destEntity} on destination (source is {entityCode})."

            });

        }



        var map = await _mapping.FindBySourceAsync(sourceDbId, destDbId, entityCode, masterCode, cancellationToken).ConfigureAwait(false);

        result.Checks.Add(new DryRunCheck

        {

            Name = "Existing mapping",

            Ok = true,

            Detail = map is null

                ? $"No mapping yet — a new {destEntity} record would be created on the destination."

                : $"Mapped to {map.SubCode} ({map.DstEntityCode})."

        });



        try

        {

            await _read.DiscoverSchemaAsync(dest, cancellationToken).ConfigureAwait(false);

            result.Checks.Add(new DryRunCheck { Name = "Target database accessible", Ok = true, Detail = "Read-only connection succeeded." });

        }

        catch (Exception ex)

        {

            result.Checks.Add(new DryRunCheck { Name = "Target database accessible", Ok = false, Detail = CredentialRedactor.Redact(ex.Message) });

        }



        result.WouldSucceed = result.Checks.All(c => c.Ok);

        result.Summary = result.WouldSucceed

            ? "Dry run passed. No data was modified."

            : "Dry run found problems. No data was modified.";

        return result;

    }



    public async Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default)

    {

        try

        {

            if (job.JobKind is "MASTER" or "MASTER_DRYRUN")

                await ProcessMaster(job, cancellationToken).ConfigureAwait(false);

            else if (job.JobKind is "TXN" or "TXN_DRYRUN")

                await ProcessTxnPlaceholder(job, cancellationToken).ConfigureAwait(false);

            else

                await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, "Unknown job kind.", cancellationToken).ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            await _errors.WriteAsync(new ErrorEntry

            {

                DbId = job.DestDbId,

                ErrorType = "JOB",

                UserMessage = "Synchronization job failed.",

                StackTrace = ex.ToString(),

                RetryCount = job.Attempts

            }, cancellationToken).ConfigureAwait(false);

            await _queue.CompleteAsync(job.JobId, JobStatuses.Retrying, "Job failed and is waiting for a safe retry.", cancellationToken).ConfigureAwait(false);

        }

    }



    private async Task ProcessMaster(SyncJob job, CancellationToken cancellationToken)

    {

        var payload = ParsePayload(job.PayloadJson);

        var record = payload.Record;

        var sourceEntity = string.IsNullOrWhiteSpace(payload.SourceEntity) ? job.EntityCode : payload.SourceEntity;

        var dest = await _registry.GetAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);

        var src = await _registry.GetAsync(job.SourceDbId, cancellationToken).ConfigureAwait(false);

        var destEntity = string.IsNullOrWhiteSpace(payload.DestEntity)

            ? PartnerSyncRules.ResolveTargetEntity(src, dest, sourceEntity)

            : payload.DestEntity;



        var v = _validation.ValidateDatabase(dest, "Destination");

        v.Errors.AddRange(_validation.ValidateMaster(record, sourceEntity).Errors);

        if (!v.Ok)

        {

            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, string.Join(" ", v.Errors), cancellationToken);

            return;

        }



        var existing = await _mapping.FindBySourceAsync(job.SourceDbId, job.DestDbId, sourceEntity, record.Code, cancellationToken).ConfigureAwait(false);

        ISqlAccountWriteAdapter writer = job.DryRun ? new DryRunWriteAdapter() : _write;

        var stamp = $"JOB-{job.JobId}";

        WriteResult write;



        if (existing is not null)

        {

            if (!job.DryRun)

                await _mapping.TouchLastSyncAtAsync(existing.MappingId, cancellationToken).ConfigureAwait(false);

            write = new WriteResult

            {

                Success = true,

                DocNo = existing.SubCode,

                Message = "Mapping exists; destination already identified. No duplicate created."

            };

        }

        else if (PartnerSyncRules.IsPartyEntity(destEntity)

                 && await _read.PartyExistsByStampAsync(dest!, destEntity, stamp, cancellationToken).ConfigureAwait(false))

        {

            write = new WriteResult

            {

                Success = true,

                Message = "Destination already has a record with this sync stamp. Skipped to prevent loop."

            };

        }

        else

        {

            var enriched = PartnerSyncRules.IsPartyEntity(sourceEntity)

                ? await LoadSourceRecordAsync(src!, sourceEntity, record.Code, cancellationToken).ConfigureAwait(false) ?? record

                : record;

            write = await writer.CreateMasterAsync(dest!, destEntity, enriched, stamp, src, sourceEntity, cancellationToken).ConfigureAwait(false);

            if (write.Success && !job.DryRun)

            {

                var subCode = string.IsNullOrWhiteSpace(write.DocNo) ? enriched.Code : write.DocNo;

                await _mapping.ConfirmPairAsync(new EntityMapping

                {

                    MasterDbId = job.SourceDbId,

                    SubDbId = job.DestDbId,

                    EntityCode = sourceEntity,

                    SrcEntityCode = sourceEntity,

                    DstEntityCode = destEntity,

                    MasterCode = enriched.Code,

                    SubCode = subCode,

                    MasterDesc = enriched.Description,

                    SubDesc = enriched.Description

                }, job.CreatedBy,

                    writeReverse: PartnerSyncRules.IsReciprocalPartySync(src, dest, sourceEntity),

                    originJobId: job.JobId,

                    cancellationToken).ConfigureAwait(false);

            }

        }



        if (!write.Success)

        {

            await _queue.CompleteAsync(job.JobId, JobStatuses.Retrying, write.Message, cancellationToken);

            return;

        }



        if (!job.DryRun)

            await _registry.MarkSyncOkAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);

        await _queue.CompleteAsync(job.JobId, JobStatuses.Completed, write.Message, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(new AuditEntry

        {

            UserEmail = job.CreatedBy,

            Action = job.DryRun ? "MASTER_DRYRUN_DONE" : "MASTER_SYNC_DONE",

            DbId = job.DestDbId,

            Entity = destEntity,

            RecordId = record.Code,

            Result = "SUCCESS"

        }, cancellationToken).ConfigureAwait(false);

    }



    private async Task<MasterRecord?> LoadSourceRecordAsync(

        RegisteredDatabase source, string entityCode, string code, CancellationToken cancellationToken)

    {

        if (string.Equals(entityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))

            return await _read.GetItemDetailAsync(source, code, cancellationToken).ConfigureAwait(false)

                   ?? await _read.FindAsync(source, entityCode, code, cancellationToken).ConfigureAwait(false);

        if (PartnerSyncRules.IsPartyEntity(entityCode))

            return await _read.GetPartyDetailAsync(source, entityCode, code, cancellationToken).ConfigureAwait(false)

                   ?? await _read.FindAsync(source, entityCode, code, cancellationToken).ConfigureAwait(false);

        return await _read.FindAsync(source, entityCode, code, cancellationToken).ConfigureAwait(false);

    }



    private static MasterSyncPayload ParsePayload(string json)

    {

        try

        {

            var wrapped = JsonSerializer.Deserialize<MasterSyncPayload>(json);

            if (wrapped?.Record is not null && !string.IsNullOrWhiteSpace(wrapped.Record.Code))

                return wrapped;

        }

        catch

        {

            // Legacy payload: plain MasterRecord JSON.

        }



        var legacy = JsonSerializer.Deserialize<MasterRecord>(json) ?? new MasterRecord();

        return new MasterSyncPayload { Record = legacy, SourceEntity = "", DestEntity = "" };

    }



    private Task ProcessTxnPlaceholder(SyncJob job, CancellationToken cancellationToken) =>

        _queue.CompleteAsync(job.JobId, JobStatuses.Failed, "Transaction jobs are processed by the transaction engine.", cancellationToken);



    private static void Add(DryRunResult result, string name, ValidationResult v) =>

        result.Checks.Add(new DryRunCheck { Name = name, Ok = v.Ok, Detail = v.Ok ? "OK" : string.Join(" ", v.Errors) });

}


