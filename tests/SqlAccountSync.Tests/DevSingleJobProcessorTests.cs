using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Transactions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SqlAccountSync.Tests;

public class DevSingleJobProcessorTests
{
    [Fact]
    public async Task Refuses_Outside_Development()
    {
        var queue = new FakeQueue();
        var proc = Build(isDev: false, allow: true, queue, new FakeRegistry(), new CountingSync(), new CountingTxns());

        var result = await proc.ProcessExactAsync(42);

        Assert.False(result.Ok);
        Assert.Contains("Development", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, queue.ClaimExactCalls);
        Assert.Equal(0, queue.ClaimAnyCalls);
    }

    [Fact]
    public async Task Refuses_When_AllowDevSingleJobProcess_False()
    {
        var queue = new FakeQueue();
        var proc = Build(isDev: true, allow: false, queue, new FakeRegistry(), new CountingSync(), new CountingTxns());

        var result = await proc.ProcessExactAsync(42);

        Assert.False(result.Ok);
        Assert.Contains("AllowDevSingleJobProcess", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, queue.ClaimExactCalls);
    }

    [Fact]
    public async Task Refuses_Unauthorized_Destination_Without_Claim()
    {
        var job = new SyncJob
        {
            JobId = 7,
            JobKind = "MASTER",
            Status = JobStatuses.Pending,
            DestDbId = 99,
            EntityCode = EntityCodes.Customer
        };
        var queue = new FakeQueue { Job = job };
        var registry = new FakeRegistry
        {
            Db = new RegisteredDatabase
            {
                DbId = 99,
                DbCode = "STEPPES",
                FbPath = @"C:/eStream/SQLAccounting/DB/ACC-STEPPES.FDB",
                DbRole = DatabaseRoles.Sub
            }
        };
        var sync = new CountingSync();
        var proc = Build(isDev: true, allow: true, queue, registry, sync, new CountingTxns());

        var result = await proc.ProcessExactAsync(7);

        Assert.False(result.Ok);
        Assert.Contains("MIRACLE", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, queue.ClaimExactCalls);
        Assert.Equal(0, queue.ClaimAnyCalls);
        Assert.Equal(0, sync.ProcessCalls);
    }

    [Fact]
    public async Task Processes_Exact_Miracle_Job_Without_ClaimAsync()
    {
        var job = new SyncJob
        {
            JobId = 11,
            JobKind = "MASTER",
            Status = JobStatuses.Pending,
            DestDbId = 12,
            EntityCode = EntityCodes.Customer
        };
        var queue = new FakeQueue { Job = job };
        var registry = new FakeRegistry
        {
            Db = new RegisteredDatabase
            {
                DbId = 12,
                DbCode = "MIRACLE",
                FbPath = @"C:/eStream/SQLAccounting/DB/ACC-MIRACLE.FDB",
                DbRole = DatabaseRoles.Sub
            }
        };
        var sync = new CountingSync();
        var txns = new CountingTxns();
        var proc = Build(isDev: true, allow: true, queue, registry, sync, txns);

        var result = await proc.ProcessExactAsync(11);

        Assert.True(result.Ok);
        Assert.True(result.Claimed);
        Assert.True(result.Processed);
        Assert.Equal("MIRACLE", result.DestDbCode);
        Assert.Equal(1, queue.ClaimExactCalls);
        Assert.Equal(0, queue.ClaimAnyCalls);
        Assert.Equal(1, sync.ProcessCalls);
        Assert.Equal(0, txns.ProcessCalls);
        Assert.Equal(11, queue.LastClaimExactJobId);
    }

    [Fact]
    public async Task Processes_Txn_Job_Via_TransactionEngine()
    {
        var job = new SyncJob
        {
            JobId = 22,
            JobKind = "TXN",
            Status = JobStatuses.Retrying,
            DestDbId = 14,
            EntityCode = "SI_TO_PI"
        };
        var queue = new FakeQueue { Job = job };
        var registry = new FakeRegistry
        {
            Db = new RegisteredDatabase
            {
                DbId = 14,
                DbCode = "ONYAT",
                FbPath = @"C:/eStream/SQLAccounting/DB/ACC-ONYAT.FDB",
                DbRole = DatabaseRoles.Sub
            }
        };
        var sync = new CountingSync();
        var txns = new CountingTxns();
        var proc = Build(isDev: true, allow: true, queue, registry, sync, txns);

        var result = await proc.ProcessExactAsync(22);

        Assert.True(result.Ok);
        Assert.Equal(1, txns.ProcessCalls);
        Assert.Equal(0, sync.ProcessCalls);
        Assert.Equal(0, queue.ClaimAnyCalls);
    }

    [Theory]
    [InlineData("MIRACLE", @"C:/db/ACC-MIRACLE.FDB", true)]
    [InlineData("ONYAT", @"C:/db/ACC-ONYAT.FDB", true)]
    [InlineData("STEPPES", @"C:/db/ACC-STEPPES.FDB", false)]
    [InlineData("OTHER", @"C:/db/ACC-UAMAT.FDB", false)]
    public void IsAuthorizedDevTarget_Miracle_Onyat_Only(string code, string path, bool expected)
    {
        var db = new RegisteredDatabase { DbCode = code, FbPath = path };
        Assert.Equal(expected, DevSingleJobProcessor.IsAuthorizedDevTarget(db));
    }

    private static DevSingleJobProcessor Build(
        bool isDev,
        bool allow,
        FakeQueue queue,
        FakeRegistry registry,
        CountingSync sync,
        CountingTxns txns) =>
        new(
            new FakeHostEnv(isDev),
            MsOptions.Create(new SyncOptions { AllowDevSingleJobProcess = allow }),
            queue,
            registry,
            sync,
            txns,
            NullLogger<DevSingleJobProcessor>.Instance);

    private sealed class FakeHostEnv : IHostEnvironment
    {
        public FakeHostEnv(bool isDev) =>
            EnvironmentName = isDev ? Environments.Development : Environments.Production;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "SqlAccountSync.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class FakeQueue : IJobQueue
    {
        public SyncJob? Job { get; set; }
        public int ClaimExactCalls { get; private set; }
        public int ClaimAnyCalls { get; private set; }
        public int LastClaimExactJobId { get; private set; }

        public Task<SyncJob?> GetAsync(int jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Job is not null && Job.JobId == jobId ? Clone(Job) : null);

        public Task<SyncJob?> ClaimExactAsync(int jobId, string workerId, CancellationToken cancellationToken = default)
        {
            ClaimExactCalls++;
            LastClaimExactJobId = jobId;
            if (Job is null || Job.JobId != jobId)
                return Task.FromResult<SyncJob?>(null);
            Job.Status = JobStatuses.Processing;
            return Task.FromResult<SyncJob?>(Clone(Job));
        }

        public Task<SyncJob?> ClaimAsync(string workerId, CancellationToken cancellationToken = default)
        {
            ClaimAnyCalls++;
            return Task.FromResult<SyncJob?>(null);
        }

        public Task CompleteAsync(int jobId, string status, string? error, CancellationToken cancellationToken = default)
        {
            if (Job is not null && Job.JobId == jobId)
                Job.Status = status;
            return Task.CompletedTask;
        }

        public Task<JobEnqueueResult> EnqueueAsync(SyncJob job, CancellationToken cancellationToken = default, bool reviveCancelled = true) =>
            Task.FromResult(new JobEnqueueResult(job.JobId, true));
        public Task<List<SyncJob>> ListAsync(string? status = null, int take = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<SyncJob>());
        public Task CancelAsync(int jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CancelAllActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> CancelUnstartedWaitingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReclaimStaleProcessingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task UnlockProcessingAsync(int jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<QueueHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueHealthSnapshot());

        private static SyncJob Clone(SyncJob j) => new()
        {
            JobId = j.JobId,
            JobKind = j.JobKind,
            Status = j.Status,
            DestDbId = j.DestDbId,
            SourceDbId = j.SourceDbId,
            EntityCode = j.EntityCode,
            PayloadJson = j.PayloadJson,
            DryRun = j.DryRun
        };
    }

    private sealed class FakeRegistry : IDatabaseRegistry
    {
        public RegisteredDatabase? Db { get; set; }

        public Task<RegisteredDatabase?> GetAsync(int dbId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Db is not null && Db.DbId == dbId ? Db : null);

        public Task<RegisteredDatabase?> FindByCodeAsync(string dbCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Db is not null
                && string.Equals((Db.DbCode ?? "").Trim(), (dbCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                    ? Db
                    : null);

        public Task<List<RegisteredDatabase>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(Db is null ? new List<RegisteredDatabase>() : [Db]);

        public Task<int> SaveAsync(RegisteredDatabase db, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeactivateAsync(int dbId, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(int dbId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetActiveAsync(int dbId, bool active, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task PointSqlApiCredRefsAtBundleAsync(string tenantCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UpdateStatusAsync(int dbId, string status, bool success, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MarkSyncOkAsync(int dbId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<DatabaseLink>> ListLinksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<DatabaseLink>());
        public Task<int> AddLinkAsync(int masterId, int subId, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeactivateLinkAsync(int linkId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DashboardStats> DashboardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DashboardStats());
        public Task<DashboardRegistrySnapshot> ReadDashboardRegistryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DashboardRegistrySnapshot());
    }

    private sealed class CountingSync : ISyncOrchestrator
    {
        public int ProcessCalls { get; private set; }

        public Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            return Task.CompletedTask;
        }

        public Task<(int JobId, string Message)> EnqueueMasterAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, bool dryRun, string user, CancellationToken cancellationToken = default, bool reviveCancelled = true) =>
            throw new NotSupportedException();
        public Task<(int Queued, int Failed, List<string> Messages)> EnqueueMasterBatchAsync(int sourceDbId, IReadOnlyList<int> destDbIds, string entityCode, IReadOnlyList<string> masterCodes, bool dryRun, string user, CancellationToken cancellationToken = default, bool reviveCancelled = true) =>
            throw new NotSupportedException();
        public Task<(int JobId, string Message)> EnqueueMasterUpdateAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<(int JobId, string Message)> EnqueueMasterDeleteAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, int mappingId, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DryRunResult> DryRunMasterAsync(int sourceDbId, int destDbId, string entityCode, string masterCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<SyncRule>> ListRulesAsync(int linkId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> CountEnabledRulesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SaveRuleAsync(SyncRule rule, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SaveScopeAsync(int ruleId, string recordCode, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingTxns : ITransactionEngine
    {
        public int ProcessCalls { get; private set; }

        public Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            return Task.CompletedTask;
        }

        public Task<(int TxnPk, string TxnId, string Message)> EnqueueAsync(int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey, bool dryRun, string user, bool confirmed, IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DryRunResult> DryRunAsync(int sourceDbId, int destDbId, string txnTypeCode, string srcDocNoOrKey, IReadOnlyDictionary<int, decimal>? qtyBySeq = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RetryAsync(int txnPk, string user, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SyncTransaction?> GetAsync(int txnPk, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SyncTransaction?> GetByTxnIdAsync(string txnId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<SyncTransaction>> ListAsync(int take = 200, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<SyncTransaction>> ListPageAsync(DateTime? before, string? beforeKind, int beforeId, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<TxnEvent>> TimelineAsync(int txnPk, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<(string Code, string Name, string Src, string Dst, string DstPath)>> ListTypesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
