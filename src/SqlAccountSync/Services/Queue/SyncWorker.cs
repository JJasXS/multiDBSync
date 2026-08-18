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
                var job = await queue.ClaimAsync(workerId, stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation("Processing job {JobId} kind {Kind}", job.JobId, job.JobKind);
                if (job.JobKind.StartsWith("TXN", StringComparison.OrdinalIgnoreCase))
                    await txns.ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                else
                    await sync.ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
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
