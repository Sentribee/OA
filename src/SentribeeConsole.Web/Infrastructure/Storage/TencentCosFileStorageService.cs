using COSXML;
using COSXML.Auth;
using COSXML.Model.Object;
using Microsoft.Extensions.Options;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Infrastructure.Storage;

public sealed class TencentCosFileStorageService(IOptions<CosStorageOptions> options) : IFileStorageService
{
    private readonly CosStorageOptions _options = options.Value;

    public async Task<StoredFile> UploadAsync(
        Stream content,
        string contentType,
        string extension,
        string category,
        CancellationToken cancellationToken)
    {
        ValidateOptions();

        var safeCategory = string.Join(
            '/',
            category.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => new string(segment
                    .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
                    .ToArray()))
                .Where(segment => segment.Length > 0));
        var safeExtension = extension.StartsWith('.') ? extension : $".{extension}";
        var key = $"{safeCategory}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{safeExtension}";

        await using var uploadStream = new MemoryStream();
        await content.CopyToAsync(uploadStream, cancellationToken);
        uploadStream.Position = 0;

        var config = new CosXmlConfig.Builder()
            .IsHttps(true)
            .SetAppid(_options.AppId)
            .SetRegion(_options.Region)
            .Build();
        var credentials = new DefaultQCloudCredentialProvider(
            _options.SecretId,
            _options.SecretKey,
            600);
        var client = new CosXmlServer(config, credentials);
        var request = new PutObjectRequest(_options.Bucket, key, uploadStream);
        request.SetRequestHeader("Content-Type", contentType);

        await Task.Run(() => client.PutObject(request), cancellationToken);

        return new StoredFile(
            key,
            $"{_options.PublicBaseUrl.TrimEnd('/')}/{key}");
    }

    private void ValidateOptions()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.SecretId)) missing.Add(nameof(_options.SecretId));
        if (string.IsNullOrWhiteSpace(_options.SecretKey)) missing.Add(nameof(_options.SecretKey));
        if (string.IsNullOrWhiteSpace(_options.AppId)) missing.Add(nameof(_options.AppId));
        if (string.IsNullOrWhiteSpace(_options.Region)) missing.Add(nameof(_options.Region));
        if (string.IsNullOrWhiteSpace(_options.Bucket)) missing.Add(nameof(_options.Bucket));
        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl)) missing.Add(nameof(_options.PublicBaseUrl));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"COS storage configuration is missing: {string.Join(", ", missing)}.");
        }
    }
}
