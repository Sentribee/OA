namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeImageStorageService
{
    Task<StoredFile> UploadAsync(
        Stream content,
        string contentType,
        string extension,
        string category,
        CancellationToken cancellationToken);
}
