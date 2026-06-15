namespace SentribeeConsole.Web.Application.Contracts;

public interface IPpeEventReviewService
{
    Task ReviewEventAsync(
        int eventId,
        byte[]? imageBytes,
        string? imageContentType,
        CancellationToken cancellationToken);
}
