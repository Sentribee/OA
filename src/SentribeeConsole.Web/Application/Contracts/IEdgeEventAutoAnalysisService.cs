namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeEventAutoAnalysisService
{
    Task<EdgeEventAutoAnalysisResult?> AnalyzeAsync(
        int eventId,
        int projectId,
        string deviceCode,
        string? imageUrl,
        byte[]? imageBytes,
        string? imageContentType,
        string? detectionJson,
        CancellationToken cancellationToken);
}

public sealed record EdgeEventAutoAnalysisResult(
    global::EdgeEventAnalysisResult Analysis,
    string? AnnotationJson,
    string? TrainingJsonUrl);
