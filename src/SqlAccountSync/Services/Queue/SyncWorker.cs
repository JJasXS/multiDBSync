using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Sync;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Services.Queue;

public sealed class SyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(IServiceScopeFactory scopes, IOptions<SyncOptions> options, ILogger<SyncWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundWriteWorkers)
        {
            _logger.LogInformation(
                "SyncWorker idle: Sync:EnableBackgroundWriteWorkers=false (no job claim / no writes).");
            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            return;
        }

        var workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var delay = TimeSpan.FromSeconds(Math.Clamp(_options.WorkerPollSeconds, 2, 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                var sync = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                var txns = scope.ServiceProvider.GetRequiredService<ITransactionEngine>();

                var reclaimed = await queue.ReclaimStaleProcessingAsync(stoppingToken).ConfigureAwait(false);
                if (reclaimed > 0)
                    _logger.LogWarning("Reclaimed {Count} stale PROCESSING job(s) after lease expiry.", reclaimed);

                var stopped = await queue.CancelUnstartedWaitingAsync(stoppingToken).ConfigureAwait(false);
                if (stopped > 0)
                    _logger.LogInformation("Stopped {Count} job(s) that waited longer than {Seconds}s.", stopped, _options.MaxQueueWaitSeconds);

                var job = await queue.ClaimAsync(workerId, stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation("Processing job {JobId} kind {Kind}", job.JobId, job.JobKind);
                await SyncJobDispatch.ProcessAsync(job, sync, txns, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop failed.");
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
