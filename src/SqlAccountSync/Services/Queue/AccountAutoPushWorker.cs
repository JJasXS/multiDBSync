using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.Queue;

/// <summary>
/// Periodically queues missing CUSTOMER, SUPPLIER, and ACCOUNT codes from MASTER/BOTH to linked subs.
/// A cycle runs only for entities whose auto-push checkbox is on.
/// </summary>
public sealed class AccountAutoPushWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SyncOptions _options;
    private readonly ILogger<AccountAutoPushWorker> _logger;

    public AccountAutoPushWorker(
        IServiceScopeFactory scopes,
        IOptions<SyncOptions> options,
        ILogger<AccountAutoPushWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(_options.AccountAutoPushSeconds <= 0 ? 60 : _options.AccountAutoPushSeconds, 15, 3600));
        _logger.LogInformation(
            "Master auto-push checks every {Seconds}s. A cycle runs only when a Customer, Supplier, or Account checkbox is on.",
            delay.TotalSeconds);

        if (!await WaitAsync(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            if (!await WaitAsync(delay, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMasterMissingAutoPushService>();
            var (queued, skipped, messages) = await service.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            if (queued > 0)
            {
                _logger.LogInformation(
                    "Master auto-push cycle queued {Queued} job(s); skipped pairs={Skipped}. {Detail}",
                    queued, skipped, string.Join("; ", messages.Take(8)));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Master auto-push cycle failed.");
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
