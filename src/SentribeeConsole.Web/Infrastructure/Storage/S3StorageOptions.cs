namespace SentribeeConsole.Web.Infrastructure.Storage;

public sealed class S3StorageOptions
{
    public const string SectionName = "S3Storage";

    public string AccessKeyId { get; init; } = string.Empty;

    public string SecretAccessKey { get; init; } = string.Empty;

    public string Region { get; init; } = "ap-southeast-2";

    public string Bucket { get; init; } = "sentribeebus";

    public string? PublicBaseUrl { get; init; }
}
