using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Mapping;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Items;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Validation;



namespace SqlAccountSync.Services.Sync;



public interface ISyncOrchestrator

{

    Task<(int JobId, string Message)> EnqueueMasterAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, bool dryRun, string user, CancellationToken cancellationToken = default, bool reviveCancelled = true);

    Task<(int Queued, int Failed, List<string> Messages)> EnqueueMasterBatchAsync(

        int sourceDbId,

        IReadOnlyList<int> destDbIds,

        string entityCode,

        IReadOnlyList<string> masterCodes,

        bool dryRun,

        string user,

        CancellationToken cancellationToken = default,

        bool reviveCancelled = true);

    Task<(int JobId, string Message)> EnqueueMasterUpdateAsync(
        int sourceDbId, int destDbId, string entityCode, string masterCode, string user,
        CancellationToken cancellationToken = default);

    Task<(int JobId, string Message)> EnqueueMasterDeleteAsync(
        int sourceDbId, int destDbId, string entityCode, string masterCode, int mappingId, string user,
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

    private readonly IItemPrerequisiteService _itemPrereqs;
    private readonly SyncOptions _sync;



    public SyncOrchestrator(

        CentralDb db,

        IDatabaseRegistry registry,

        ISqlAccountReadAdapter read,

        ISqlAccountWriteAdapter write,

        IMappingService mapping,

        IValidationService validation,

        IJobQueue queue,

        IAuditService audit,

        IErrorLogService errors,

        IItemPrerequisiteService itemPrereqs,
        IOptions<SyncOptions> sync)

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

        _itemPrereqs = itemPrereqs;
        _sync = sync.Value;

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

        int sourceDbId, int destDbId, string entityCode, string masterCode, bool dryRun, string user, CancellationToken cancellationToken = default, bool reviveCancelled = true)

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

        var enqueued = await _queue.EnqueueAsync(new SyncJob

        {

            JobKind = dryRun ? "MASTER_DRYRUN" : "MASTER",

            SourceDbId = sourceDbId,

            DestDbId = destDbId,

            EntityCode = entityCode,

            PayloadJson = payload,

            DryRun = dryRun,

            IdemKey = idem,

            CreatedBy = user,

            Priority = PartnerSyncRules.IsPartyEntity(entityCode) ? PartnerSyncRules.PartyPushPriority : 0

        }, cancellationToken, reviveCancelled).ConfigureAwait(false);

        var jobId = enqueued.JobId;
        if (enqueued.LeftCancelled)
            return (0, $"{masterCode} was stopped and was not queued again.");

        if (enqueued.IsNew)
        {
            await _audit.WriteAsync(new AuditEntry
            {
                UserEmail = user,
                Action = dryRun ? "DRY_RUN_MASTER" : "ENQUEUE_MASTER",
                DbId = sourceDbId,
                Entity = entityCode,
                RecordId = masterCode,
                Result = "QUEUED",
                RequestId = jobId.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
        }

        return (jobId, dryRun
            ? "Dry-run job queued."
            : $"Queued {masterCode} to {dest!.DisplayName} (job #{jobId}).");

    }



    public async Task<(int Queued, int Failed, List<string> Messages)> EnqueueMasterBatchAsync(

        int sourceDbId,

        IReadOnlyList<int> destDbIds,

        string entityCode,

        IReadOnlyList<string> masterCodes,

        bool dryRun,

        string user,

        CancellationToken cancellationToken = default,

        bool reviveCancelled = true)

    {

        var messages = new List<string>();

        var queued = 0;

        var failed = 0;

        var codes = masterCodes
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.Equals(entityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase) && sourceDbId > 0)
        {
            var sourceDb = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);
            if (sourceDb is not null)
            {
                var chart = await _read.GetGlChartAsync(sourceDb, cancellationToken).ConfigureAwait(false);
                codes = GlAccountMapper.OrderCodesParentsFirst(codes, chart);
            }
        }

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



        var dbById = new Dictionary<int, RegisteredDatabase>();

        if (sourceDbId > 0)

        {

            var sourceDb = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);

            if (sourceDb is not null)

                dbById[sourceDbId] = sourceDb;

        }

        foreach (var destId in dests)

        {

            var db = await _registry.GetAsync(destId, cancellationToken).ConfigureAwait(false);

            if (db is not null)

                dbById[destId] = db;

        }



        string DestLabel(int id) => dbById.TryGetValue(id, out var db) ? db.DisplayName : $"database {id}";



        foreach (var code in codes)

        {

            foreach (var destId in dests)

            {

                var (jobId, msg) = await EnqueueMasterAsync(sourceDbId, destId, entityCode, code, dryRun, user, cancellationToken, reviveCancelled)

                    .ConfigureAwait(false);

                if (jobId > 0)

                {

                    queued++;

                    messages.Add($"{code} to {DestLabel(destId)} (job #{jobId})");

                }

                else

                {

                    failed++;

                    messages.Add($"{code} to {DestLabel(destId)}: {msg}");

                }

            }

        }



        return (queued, failed, messages);

    }

    public async Task<(int JobId, string Message)> EnqueueMasterUpdateAsync(
        int sourceDbId, int destDbId, string entityCode, string masterCode, string user,
        CancellationToken cancellationToken = default)
    {
        if (sourceDbId == destDbId)
            return (0, "Source and destination must be different databases.");
        if (!PartnerSyncRules.IsPartyEntity(entityCode)
            && !string.Equals(entityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            return (0, "Update is only supported for customer, supplier, and account.");

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
            IdempotencyKey.ContentHash(payload + "|UPD"));

        var jobId = (await _queue.EnqueueAsync(new SyncJob
        {
            JobKind = "MASTER_UPDATE",
            SourceDbId = sourceDbId,
            DestDbId = destDbId,
            EntityCode = entityCode,
            PayloadJson = payload,
            IdemKey = idem,
            CreatedBy = user,
            Priority = PartnerSyncRules.IsPartyEntity(entityCode) ? PartnerSyncRules.PartyPushPriority : 0
        }, cancellationToken).ConfigureAwait(false)).JobId;

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = user,
            Action = "ENQUEUE_MASTER_UPDATE",
            DbId = sourceDbId,
            Entity = entityCode,
            RecordId = masterCode,
            Result = "QUEUED",
            RequestId = jobId.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);

        return (jobId, $"Queued update {masterCode} to {dest.DisplayName} (job #{jobId}).");
    }

    public async Task<(int JobId, string Message)> EnqueueMasterDeleteAsync(
        int sourceDbId, int destDbId, string entityCode, string masterCode, int mappingId, string user,
        CancellationToken cancellationToken = default)
    {
        if (sourceDbId == destDbId)
            return (0, "Source and destination must be different databases.");
        if (!PartnerSyncRules.IsPartyEntity(entityCode)
            && !string.Equals(entityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
            return (0, "Delete is only supported for customer, supplier, and account.");
        if (mappingId <= 0)
            return (0, "Mapping id is required for delete.");

        var source = await _registry.GetAsync(sourceDbId, cancellationToken).ConfigureAwait(false);
        var dest = await _registry.GetAsync(destDbId, cancellationToken).ConfigureAwait(false);
        var dbCheck = _validation.ValidateDatabase(source, "Source");
        dbCheck.Errors.AddRange(_validation.ValidateDatabase(dest, "Destination").Errors);
        if (dbCheck.Errors.Count > 0)
            return (0, string.Join(" ", dbCheck.Errors));

        var code = (masterCode ?? "").Trim();
        if (code.Length == 0)
            return (0, "Code is required for delete.");

        var destEntity = PartnerSyncRules.ResolveTargetEntity(source, dest, entityCode);
        var payloadObj = new MasterDeletePayload { Code = code, MappingId = mappingId };
        var payload = JsonSerializer.Serialize(payloadObj);
        var idem = IdempotencyKey.ForMasterData(
            source!.DbCode, dest!.DbCode, entityCode, destEntity, code,
            IdempotencyKey.ContentHash(payload + "|DEL"));

        var jobId = (await _queue.EnqueueAsync(new SyncJob
        {
            JobKind = "MASTER_DELETE",
            SourceDbId = sourceDbId,
            DestDbId = destDbId,
            EntityCode = entityCode,
            PayloadJson = payload,
            IdemKey = idem,
            CreatedBy = user,
            Priority = PartnerSyncRules.IsPartyEntity(entityCode) ? PartnerSyncRules.PartyPushPriority : 0
        }, cancellationToken).ConfigureAwait(false)).JobId;

        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = user,
            Action = "ENQUEUE_MASTER_DELETE",
            DbId = sourceDbId,
            Entity = entityCode,
            RecordId = code,
            Result = "QUEUED",
            RequestId = jobId.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);

        return (jobId, $"Queued delete {code} on {dest.DisplayName} (job #{jobId}).");
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

            result.Summary = "Cannot continue. Database registry is incomplete.";

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

                ? $"No mapping yet. A new {destEntity} record would be created on the destination."

                : $"Mapped to {map.SubCode} ({map.DstEntityCode})."

        });



        if (string.Equals(destEntity, EntityCodes.Item, StringComparison.OrdinalIgnoreCase) && map is null)

        {

            var existsOnDest = await _read.MasterExistsByCodeAsync(dest, destEntity, masterCode, cancellationToken).ConfigureAwait(false);

            result.Checks.Add(new DryRunCheck

            {

                Name = "Destination item code",

                Ok = true,

                Detail = existsOnDest

                    ? $"Code {masterCode} already exists on destination. Would link mapping only; no API create."

                    : $"Code {masterCode} not found on destination. Would call POST /stockitem."

            });

            if (!existsOnDest && record is not null)

            {

                foreach (var check in await _itemPrereqs.CheckAsync(dest, source, record, cancellationToken).ConfigureAwait(false))

                {

                    result.Checks.Add(new DryRunCheck { Name = check.Name, Ok = check.Ok, Detail = check.Detail });

                }

            }

        }



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

            else if (job.JobKind is "MASTER_UPDATE")

                await ProcessMasterUpdate(job, cancellationToken).ConfigureAwait(false);

            else if (job.JobKind is "MASTER_DELETE")

                await ProcessMasterDelete(job, cancellationToken).ConfigureAwait(false);

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

        else if ((string.Equals(destEntity, EntityCodes.Item, StringComparison.OrdinalIgnoreCase)
                  || string.Equals(destEntity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase)
                  || PartnerSyncRules.IsPartyEntity(destEntity))
                 && await _read.MasterExistsByCodeAsync(dest!, destEntity, record.Code, cancellationToken).ConfigureAwait(false))
        {
            var enriched = await LoadSourceRecordAsync(src!, sourceEntity, record.Code, cancellationToken).ConfigureAwait(false) ?? record;
            if (!job.DryRun)
            {
                await _mapping.ConfirmPairAsync(new EntityMapping
                {
                    MasterDbId = job.SourceDbId,
                    SubDbId = job.DestDbId,
                    EntityCode = sourceEntity,
                    SrcEntityCode = sourceEntity,
                    DstEntityCode = destEntity,
                    MasterCode = enriched.Code,
                    SubCode = enriched.Code,
                    MasterDesc = enriched.Description,
                    SubDesc = enriched.Description
                }, job.CreatedBy,
                    writeReverse: false,
                    originJobId: job.JobId,
                    cancellationToken).ConfigureAwait(false);
            }
            write = new WriteResult
            {
                Success = true,
                DocNo = enriched.Code,
                Message = $"Destination already has this {destEntity.ToLowerInvariant()} code. Mapping linked; no duplicate created."
            };
        }
        else
        {
            var enriched = await LoadSourceRecordAsync(src!, sourceEntity, record.Code, cancellationToken).ConfigureAwait(false) ?? record;
            string? prereqDiagnosis = null;
            if (string.Equals(destEntity, EntityCodes.Item, StringComparison.OrdinalIgnoreCase) && !job.DryRun)
            {
                var prereq = await _itemPrereqs.EnsureAsync(dest!, src, enriched, cancellationToken).ConfigureAwait(false);
                if (!prereq.Ok)
                {
                    await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, prereq.Message, cancellationToken).ConfigureAwait(false);
                    return;
                }
                prereqDiagnosis = prereq.LastApiDiagnosis;
            }

            if (string.Equals(destEntity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase) && !job.DryRun)
            {
                var ready = await EnsureAccountParentOnDestAsync(
                    src!, dest!, enriched, writer, stamp, job, cancellationToken).ConfigureAwait(false);
                if (!ready.Ok)
                {
                    await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, ready.Message, cancellationToken).ConfigureAwait(false);
                    return;
                }
                enriched = ready.Record;
            }
            else if (string.Equals(destEntity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase) && job.DryRun)
            {
                enriched.Fields[GlAccountMapper.FieldDestParent] = "0";
            }

            var blocked = await ClearUnusedPartyAccountAsync(dest!, destEntity, enriched.Code, job.DryRun, cancellationToken)
                .ConfigureAwait(false);
            if (blocked is not null)
            {
                await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, blocked, cancellationToken).ConfigureAwait(false);
                return;
            }

            write = await DirectCreateMasterAsync(
                writer, dest!, destEntity, enriched, stamp, src, sourceEntity, job, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Success && !string.IsNullOrWhiteSpace(prereqDiagnosis))
                write = new WriteResult { Success = false, Message = write.Message + " " + prereqDiagnosis };

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
                    SubKey = write.DocKey ?? "",
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
            var status = IsGlCodeConflict(write.Message) ? JobStatuses.Failed : JobStatuses.Retrying;
            var message = IsGlCodeConflict(write.Message)
                ? $"Account code {record.Code} already exists on the destination chart, so this {destEntity.ToLowerInvariant()} cannot be created with the same code."
                : write.Message;
            await _queue.CompleteAsync(job.JobId, status, message, cancellationToken);
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



    private async Task<(bool Ok, string Message, MasterRecord Record)> EnsureAccountParentOnDestAsync(
        RegisteredDatabase source,
        RegisteredDatabase dest,
        MasterRecord record,
        ISqlAccountWriteAdapter writer,
        string stamp,
        SyncJob job,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        if (depth > 20)
            return (false, $"Account parent chain too deep for '{record.Code}'.", record);

        var parentCode = GlAccountMapper.ParentCodeOf(record);
        if (parentCode.Length == 0)
        {
            record.Fields[GlAccountMapper.FieldDestParent] = "0";
            return (true, "", record);
        }

        var destChart = await _read.GetGlChartAsync(dest, cancellationToken).ConfigureAwait(false);
        var destParent = GlAccountMapper.ResolveDestParentDocKey(parentCode, destChart);
        if (destParent > 0)
        {
            record.Fields[GlAccountMapper.FieldDestParent] = destParent.ToString(CultureInfo.InvariantCulture);
            return (true, "", record);
        }

        if (await _read.MasterExistsByCodeAsync(dest, EntityCodes.Account, parentCode, cancellationToken).ConfigureAwait(false))
        {
            destChart = await _read.GetGlChartAsync(dest, cancellationToken).ConfigureAwait(false);
            destParent = GlAccountMapper.ResolveDestParentDocKey(parentCode, destChart);
            record.Fields[GlAccountMapper.FieldDestParent] = destParent.ToString(CultureInfo.InvariantCulture);
            if (destParent <= 0)
                return (false, $"Parent account '{parentCode}' exists on destination but dockey could not be resolved.", record);
            return (true, "", record);
        }

        var parentRecord = await LoadSourceRecordAsync(source, EntityCodes.Account, parentCode, cancellationToken)
            .ConfigureAwait(false);
        if (parentRecord is null)
            return (false, $"Parent account '{parentCode}' for '{record.Code}' was not found on source.", record);

        var parentReady = await EnsureAccountParentOnDestAsync(
            source, dest, parentRecord, writer, stamp, job, cancellationToken, depth + 1).ConfigureAwait(false);
        if (!parentReady.Ok)
            return parentReady;

        var parentWrite = await DirectCreateMasterAsync(
            writer, dest, EntityCodes.Account, parentReady.Record, stamp, source, EntityCodes.Account, job, cancellationToken)
            .ConfigureAwait(false);
        if (!parentWrite.Success)
            return (false, $"Could not create parent '{parentCode}': {parentWrite.Message}", record);

        if (!job.DryRun)
        {
            await _mapping.ConfirmPairAsync(new EntityMapping
            {
                MasterDbId = job.SourceDbId,
                SubDbId = job.DestDbId,
                EntityCode = EntityCodes.Account,
                SrcEntityCode = EntityCodes.Account,
                DstEntityCode = EntityCodes.Account,
                MasterCode = parentReady.Record.Code,
                SubCode = parentReady.Record.Code,
                MasterDesc = parentReady.Record.Description,
                SubDesc = parentReady.Record.Description
            }, job.CreatedBy,
                writeReverse: false,
                originJobId: job.JobId,
                cancellationToken).ConfigureAwait(false);
        }

        destChart = await _read.GetGlChartAsync(dest, cancellationToken).ConfigureAwait(false);
        destParent = GlAccountMapper.ResolveDestParentDocKey(parentCode, destChart);
        if (destParent <= 0)
            return (false, $"Parent '{parentCode}' was created but dockey is not visible yet on destination.", record);

        record.Fields[GlAccountMapper.FieldDestParent] = destParent.ToString(CultureInfo.InvariantCulture);
        return (true, "", record);
    }

    private async Task<MasterRecord?> LoadSourceRecordAsync(

        RegisteredDatabase source, string entityCode, string code, CancellationToken cancellationToken)

    {

        if (string.Equals(entityCode, EntityCodes.Item, StringComparison.OrdinalIgnoreCase))
            return await _read.GetItemDetailAsync(source, code, cancellationToken).ConfigureAwait(false)
                   ?? await _read.FindAsync(source, entityCode, code, cancellationToken).ConfigureAwait(false);

        if (string.Equals(entityCode, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var chart = await _read.GetGlChartAsync(source, cancellationToken).ConfigureAwait(false);
            var byKey = chart.ToDictionary(a => a.DocKey);
            var row = chart.FirstOrDefault(a =>
                string.Equals((a.Code ?? "").Trim(), (code ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            return row is null ? null : GlAccountMapper.ToMasterRecord(row, byKey);
        }

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

    private async Task ProcessMasterUpdate(SyncJob job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        var record = payload.Record;
        var sourceEntity = string.IsNullOrWhiteSpace(payload.SourceEntity) ? job.EntityCode : payload.SourceEntity;
        var destEntity = string.IsNullOrWhiteSpace(payload.DestEntity) ? sourceEntity : payload.DestEntity;
        var dest = await _registry.GetAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);
        var source = await _registry.GetAsync(job.SourceDbId, cancellationToken).ConfigureAwait(false);
        var v = _validation.ValidateDatabase(dest, "Destination");
        if (!v.Ok)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, string.Join(" ", v.Errors), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var stamp = $"JOB-{job.JobId}";
        var destDocKey = 0;
        var destParent = 0;
        if (string.Equals(destEntity, EntityCodes.Account, StringComparison.OrdinalIgnoreCase))
        {
            var destChart = await _read.GetGlChartAsync(dest!, cancellationToken).ConfigureAwait(false);
            var destRow = destChart.FirstOrDefault(a =>
                string.Equals((a.Code ?? "").Trim(), (record.Code ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (destRow is null)
            {
                await _queue.CompleteAsync(job.JobId, JobStatuses.Failed,
                    $"Account {record.Code} is missing on destination; create pass will handle it.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            destDocKey = destRow.DocKey;
            destParent = GlAccountMapper.ResolveDestParentDocKey(GlAccountMapper.ParentCodeOf(record), destChart);
            record.Fields[GlAccountMapper.FieldDestParent] = destParent.ToString(CultureInfo.InvariantCulture);
        }
        else if (!await _read.MasterExistsByCodeAsync(dest!, destEntity, record.Code, cancellationToken).ConfigureAwait(false))
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed,
                $"{destEntity} {record.Code} is missing on destination; create pass will handle it.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var write = await _write.UpdateMasterAsync(dest!, destEntity, record, stamp, destDocKey, destParent, cancellationToken, source, sourceEntity).ConfigureAwait(false);

        if (!write.Success)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Retrying, write.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var mapping = await _mapping.FindBySourceAsync(
            job.SourceDbId, job.DestDbId, sourceEntity, record.Code, cancellationToken)
            .ConfigureAwait(false);
        if (mapping is not null)
            await _mapping.TouchLastSyncAtAsync(mapping.MappingId, cancellationToken).ConfigureAwait(false);

        await _registry.MarkSyncOkAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);
        await _queue.CompleteAsync(job.JobId, JobStatuses.Completed, write.Message, cancellationToken)
            .ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = job.CreatedBy,
            Action = "MASTER_UPDATE_DONE",
            DbId = job.DestDbId,
            Entity = destEntity,
            RecordId = record.Code,
            Result = "SUCCESS"
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessMasterDelete(SyncJob job, CancellationToken cancellationToken)
    {
        MasterDeletePayload? del;
        try
        {
            del = JsonSerializer.Deserialize<MasterDeletePayload>(job.PayloadJson);
        }
        catch
        {
            del = null;
        }

        var code = (del?.Code ?? "").Trim();
        var mappingId = del?.MappingId ?? 0;
        if (code.Length == 0 || mappingId <= 0)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, "Delete payload is invalid.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var dest = await _registry.GetAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);
        var source = await _registry.GetAsync(job.SourceDbId, cancellationToken).ConfigureAwait(false);
        var v = _validation.ValidateDatabase(dest, "Destination");
        if (!v.Ok)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, string.Join(" ", v.Errors), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var sourceEntity = string.IsNullOrWhiteSpace(job.EntityCode) ? EntityCodes.Account : job.EntityCode;
        var destEntity = PartnerSyncRules.ResolveTargetEntity(source, dest, sourceEntity);

        var write = await _write.DeleteMasterAsync(dest!, destEntity, code, cancellationToken).ConfigureAwait(false);

        if (!write.Success)
        {
            await _queue.CompleteAsync(job.JobId, JobStatuses.Failed, write.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _mapping.DisableAsync(mappingId, job.CreatedBy, cancellationToken).ConfigureAwait(false);
        await _registry.MarkSyncOkAsync(job.DestDbId, cancellationToken).ConfigureAwait(false);
        await _queue.CompleteAsync(job.JobId, JobStatuses.Completed, write.Message, cancellationToken)
            .ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry
        {
            UserEmail = job.CreatedBy,
            Action = "MASTER_DELETE_DONE",
            DbId = job.DestDbId,
            Entity = destEntity,
            RecordId = code,
            Result = "SUCCESS"
        }, cancellationToken).ConfigureAwait(false);
    }



    private Task<WriteResult> DirectCreateMasterAsync(
        ISqlAccountWriteAdapter writer,
        RegisteredDatabase dest,
        string destEntity,
        MasterRecord record,
        string stamp,
        RegisteredDatabase? source,
        string sourceEntity,
        SyncJob job,
        CancellationToken cancellationToken) =>
        writer.CreateMasterAsync(dest, destEntity, record, stamp, source, sourceEntity, cancellationToken);

    private async Task<string?> ClearUnusedPartyAccountAsync(
        RegisteredDatabase dest, string destEntity, string code, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun || !PartnerSyncRules.IsPartyEntity(destEntity))
            return null;
        var key = (code ?? "").Trim();
        if (key.Length == 0)
            return null;
        if (await _read.MasterExistsByCodeAsync(dest, destEntity, key, cancellationToken).ConfigureAwait(false))
            return null;
        if (!await _read.MasterExistsByCodeAsync(dest, EntityCodes.Account, key, cancellationToken).ConfigureAwait(false))
            return null;
        if (await _read.GlAccountHasActivityAsync(dest, key, cancellationToken).ConfigureAwait(false))
            return $"Account code {key} already exists on {dest.DisplayName} and has postings, so this {destEntity.ToLowerInvariant()} cannot use that code.";

        // Unused same-code GL: allow create (API or Firebird fallback reuses it). Do not API-DELETE (hangs).
        return null;
    }

    private static bool IsApiTimeout(string? message)
    {
        var m = message ?? "";
        return m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Request Timed Out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("504", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGlCodeConflict(string? message) =>
        (message ?? "").Contains("GL_ACC_CODE", StringComparison.OrdinalIgnoreCase);

    private static void Add(DryRunResult result, string name, ValidationResult v) =>

        result.Checks.Add(new DryRunCheck { Name = name, Ok = v.Ok, Detail = v.Ok ? "OK" : string.Join(" ", v.Errors) });

}


