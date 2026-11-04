using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.Queue;

/// <summary>
/// Periodically queues missing/changed/deleted CUSTOMER, SUPPLIER, and ACCOUNT codes
/// from MASTER/BOTH to linked subs when the Push-page auto-push checkboxes are on.
/// </summary>
public sealed class AccountAutoPushWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SyncOptions _options;
    private readonly IAutoPushSchedule _schedule;
    private readonly ILogger<AccountAutoPushWorker> _logger;

    public AccountAutoPushWorker(
        IServiceScopeFactory scopes,
        IOptions<SyncOptions> options,
        IAutoPushSchedule schedule,
        ILogger<AccountAutoPushWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _schedule = schedule;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundWriteWorkers)
        {
            _logger.LogInformation(
                "AccountAutoPushWorker idle: Sync:EnableBackgroundWriteWorkers=false (no auto-push enqueue).");
            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            return;
        }

        var seconds = Math.Clamp(_options.AccountAutoPushSeconds <= 0 ? 60 : _options.AccountAutoPushSeconds, 15, 3600);
        var delay = TimeSpan.FromSeconds(seconds);
        _schedule.SetIntervalSeconds(seconds);
        _logger.LogInformation(
            "Master auto-push checks every {Seconds}s. A cycle runs only when a Customer, Supplier, or Account checkbox is on.",
            delay.TotalSeconds);

        var startup = TimeSpan.FromSeconds(10);
        _schedule.ArmAfter(startup);
        if (!await WaitAsync(startup, stoppingToken).ConfigureAwait(false))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            _schedule.ArmNextFromNow();
            if (!await WaitAsync(delay, stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAutoPushSettings>();
            var flags = await settings.GetAsync(stoppingToken).ConfigureAwait(false);

            var createService = scope.ServiceProvider.GetRequiredService<IMasterMissingAutoPushService>();
            var (queued, skipped, messages) = await createService.RunOnceAsync(stoppingToken).ConfigureAwait(false);

            if (flags.Account)
            {
                var accountUd = scope.ServiceProvider.GetRequiredService<IAccountAutoPushService>();
                var (accQueued, accSkipped, accMsgs) = await accountUd.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                queued += accQueued;
                skipped += accSkipped;
                messages.AddRange(accMsgs);
            }

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
