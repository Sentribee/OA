namespace SentribeeConsole.Web.Infrastructure.Storage;

public sealed class CosStorageOptions
{
    public const string SectionName = "CosStorage";

    public string SecretId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string PublicBaseUrl { get; set; } = string.Empty;
}
