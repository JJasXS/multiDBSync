using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Services.Queue;

/// <summary>Periodically pushes missing GL accounts from MASTER/BOTH to linked subs.</summary>
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
        var seconds = _options.AccountAutoPushSeconds;
        if (seconds <= 0)
        {
            _logger.LogInformation("Account auto-push disabled (AccountAutoPushSeconds={Seconds}).", seconds);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 3600));
        _logger.LogInformation("Account auto-push enabled every {Seconds}s.", delay.TotalSeconds);

        // Short initial delay so the app finishes startup before first scan.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAccountAutoPushService>();
                var (queued, skipped, messages) = await service.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (queued > 0)
                {
                    _logger.LogInformation(
                        "Account auto-push cycle queued {Queued} job(s); skipped pairs={Skipped}. {Detail}",
                        queued, skipped, string.Join("; ", messages.Take(8)));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Account auto-push cycle failed.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
