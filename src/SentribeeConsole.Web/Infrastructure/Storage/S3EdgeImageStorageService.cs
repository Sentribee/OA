using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Infrastructure.Storage;

public sealed class S3EdgeImageStorageService(
    IOptions<S3StorageOptions> options,
    HttpClient httpClient) : IEdgeImageStorageService, IFileStorageService
{
    private readonly S3StorageOptions _options = options.Value;

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
        var payload = uploadStream.ToArray();
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = $"{_options.Bucket}.s3.{_options.Region}.amazonaws.com";
        var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri($"https://{host}/{escapedKey}");
        var payloadHash = ToHex(SHA256.HashData(payload));
        var credentialScope = $"{dateStamp}/{_options.Region}/s3/aws4_request";
        const string signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = string.Create(
            CultureInfo.InvariantCulture,
            $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n");
        var canonicalRequest = string.Join('\n',
            "PUT",
            $"/{escapedKey}",
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = ToHex(Sign(GetSigningKey(dateStamp), stringToSign));
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={_options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var publicBaseUrl = string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? $"https://{host}"
            : _options.PublicBaseUrl.TrimEnd('/');
        return new StoredFile(key, $"{publicBaseUrl}/{escapedKey}");
    }

    private byte[] GetSigningKey(string dateStamp)
    {
        var dateKey = Sign(Encoding.UTF8.GetBytes($"AWS4{_options.SecretAccessKey}"), dateStamp);
        var dateRegionKey = Sign(dateKey, _options.Region);
        var dateRegionServiceKey = Sign(dateRegionKey, "s3");
        return Sign(dateRegionServiceKey, "aws4_request");
    }

    private void ValidateOptions()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.AccessKeyId)) missing.Add(nameof(_options.AccessKeyId));
        if (string.IsNullOrWhiteSpace(_options.SecretAccessKey)) missing.Add(nameof(_options.SecretAccessKey));
        if (string.IsNullOrWhiteSpace(_options.Region)) missing.Add(nameof(_options.Region));
        if (string.IsNullOrWhiteSpace(_options.Bucket)) missing.Add(nameof(_options.Bucket));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"S3 storage configuration is missing: {string.Join(", ", missing)}.");
        }
    }

    private static byte[] Sign(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
