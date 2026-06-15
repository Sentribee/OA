namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class YoloTrainingScheduleWorker(
    IServiceScopeFactory scopeFactory,
    PanoramaTrainingDatasetExportQueue queue,
    ILogger<YoloTrainingScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await TryRunDueSchedulesAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRunDueSchedulesAsync(stoppingToken);
        }
    }

    private async Task TryRunDueSchedulesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunDueSchedulesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to process due AI model training schedules.");
        }
    }

    private async Task RunDueSchedulesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var runStore = scope.ServiceProvider.GetRequiredService<YoloTrainingRunStore>();
        var runs = await runStore.LoadDueRunsAsync(cancellationToken);
        foreach (var run in runs)
        {
            await runStore.MarkStatusAsync(run.Id, "Exporting", "Queued training data export.", cancellationToken);
            queue.QueueProject(run.ProjectId, run.ModelKind, startTrainingAfterExport: true);
            logger.LogInformation(
                "Queued due {ModelKind} YOLO training run {RunId} for project {ProjectId}.",
                run.ModelKind,
                run.Id,
                run.ProjectId);
        }
    }
}
