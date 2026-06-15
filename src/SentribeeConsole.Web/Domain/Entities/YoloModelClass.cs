namespace SentribeeConsole.Web.Domain.Entities;

public sealed record YoloModelClass
{
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;
}
