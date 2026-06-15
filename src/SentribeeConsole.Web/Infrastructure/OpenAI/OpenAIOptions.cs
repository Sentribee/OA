namespace SentribeeConsole.Web.Infrastructure.OpenAI;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-5.4-mini";

    public string ImageModel { get; set; } = "gpt-image-1.5";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
}
