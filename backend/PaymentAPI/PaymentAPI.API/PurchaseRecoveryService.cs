using Microsoft.Extensions.Options;
using PaymentAPI.Application.Services;
using PaymentAPI.Settings;

namespace PaymentAPI;

/// <summary>
/// Runs <see cref="PurchaseRecoveryJob"/> once at startup and then every
/// <see cref="PurchaseRecoverySettings.IntervalMinutes"/> (T23). A failed run is logged and the next run tries again.
/// </summary>
public sealed class PurchaseRecoveryService : BackgroundService
{
    private readonly PurchaseRecoveryJob _job;
    private readonly IOptions<PurchaseRecoverySettings> _options;
    private readonly ILogger<PurchaseRecoveryService> _logger;

    public PurchaseRecoveryService(
        PurchaseRecoveryJob job,
        IOptions<PurchaseRecoverySettings> options,
        ILogger<PurchaseRecoveryService> logger)
    {
        _job = job;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _options.Value;
        if (!settings.Enabled)
        {
            _logger.LogWarning("Purchase recovery is disabled (PurchaseRecovery__Enabled=false): stuck purchases are not settled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes));
        _logger.LogInformation(
            "Purchase recovery started: every {Interval}, purchases older than {StuckAfterMinutes} min.",
            interval, settings.StuckAfterMinutes);

        // Do not block host startup with the first run.
        await Task.Yield();

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    await _job.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Purchase recovery run failed; the next run tries again.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }
}
