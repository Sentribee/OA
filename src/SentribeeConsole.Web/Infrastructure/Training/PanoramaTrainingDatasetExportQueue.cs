using System.Threading.Channels;

namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class PanoramaTrainingDatasetExportQueue
{
    private readonly Channel<TrainingDatasetExportRequest> _queue = Channel.CreateUnbounded<TrainingDatasetExportRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void QueueProject(int projectId, string modelKind, bool startTrainingAfterExport)
    {
        _queue.Writer.TryWrite(new TrainingDatasetExportRequest(projectId, modelKind, startTrainingAfterExport));
    }

    public IAsyncEnumerable<TrainingDatasetExportRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}

public sealed record TrainingDatasetExportRequest(
    int ProjectId,
    string ModelKind,
    bool StartTrainingAfterExport);
