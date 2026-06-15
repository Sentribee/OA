namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class PanoramaTrainingDatasetExportWorker(
    PanoramaTrainingDatasetExportQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PanoramaTrainingDatasetExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var exporter = scope.ServiceProvider.GetRequiredService<PanoramaTrainingDatasetExporter>();
                var runStore = scope.ServiceProvider.GetRequiredService<YoloTrainingRunStore>();
                var runner = scope.ServiceProvider.GetRequiredService<YoloRemoteTrainingRunner>();
                var runId = await runStore.CreateOrUpdateRunAsync(
                    request.ProjectId,
                    request.ModelKind,
                    request.StartTrainingAfterExport ? null : DateTime.UtcNow,
                    "Staging",
                    "Preparing training images and YOLO labels.",
                    stoppingToken);
                await runStore.MarkStatusAsync(runId, "Exporting", "Downloading training images and YOLO labels.", stoppingToken);
                var exportResult = await exporter.ExportPendingAsync(request.ProjectId, request.ModelKind, stoppingToken);
                if (request.StartTrainingAfterExport)
                {
                    if (exportResult.ExportedCount == 0)
                    {
                        await runStore.MarkStatusAsync(
                            runId,
                            "Failed",
                            $"No training images were exported; skipped {exportResult.SkippedCount}. Remote training was not started.",
                            stoppingToken);
                        continue;
                    }

                    await runStore.MarkStatusAsync(runId, "Training", "Remote YOLO training is running.", stoppingToken);
                    var artifact = await runner.RunAsync(request.ModelKind, stoppingToken);
                    await runStore.CompleteTrainingAsync(
                        request.ProjectId,
                        request.ModelKind,
                        artifact,
                        exportResult.ExportedIds,
                        stoppingToken);
                    await runStore.MarkStatusAsync(
                        runId,
                        "Completed",
                        $"Remote YOLO training completed. Version {artifact.VersionName}; exported {exportResult.ExportedCount}.",
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var runStore = scope.ServiceProvider.GetRequiredService<YoloTrainingRunStore>();
                    var runId = await runStore.CreateOrUpdateRunAsync(
                        request.ProjectId,
                        request.ModelKind,
                        null,
                        "Failed",
                        exception.Message,
                        CancellationToken.None);
                    await runStore.MarkStatusAsync(runId, "Failed", exception.Message, CancellationToken.None);
                }
                catch (Exception statusException)
                {
                    logger.LogError(statusException, "Unable to mark failed training run for project {ProjectId}.", request.ProjectId);
                }

                logger.LogError(
                    exception,
                    "Unable to export pending {ModelKind} training dataset for project {ProjectId}.",
                    request.ModelKind,
                    request.ProjectId);
            }
        }
    }
}
