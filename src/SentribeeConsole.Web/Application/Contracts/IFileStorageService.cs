namespace SentribeeConsole.Web.Application.Contracts;

public interface IFileStorageService
{
    Task<StoredFile> UploadAsync(
        Stream content,
        string contentType,
        string extension,
        string category,
        CancellationToken cancellationToken);
}

public sealed record StoredFile(string Key, string PublicUrl);
