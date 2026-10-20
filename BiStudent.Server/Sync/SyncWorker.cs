using BiStudent.Server.Firebase;
using BiStudent.Server.Services;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Sync;

public sealed class SyncWorker(
    SyncService sync, SyncSignal signal, BackupService backup,
    IOptions<FirebaseOptions> options, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (backup.TryEnterRequest())
                {
                    try { await sync.RunAsync(stoppingToken); }
                    finally { backup.ExitRequest(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ошибка фоновой синхронизации Firebase");
            }

            try
            {
                await signal.WaitAsync(
                    TimeSpan.FromSeconds(options.Value.SyncIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
