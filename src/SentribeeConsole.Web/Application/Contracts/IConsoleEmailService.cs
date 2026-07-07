namespace SentribeeConsole.Web.Application.Contracts;

public interface IConsoleEmailService
{
    Task<ConsoleEmailResult> SendProjectInvitationAsync(
        string email,
        string projectName,
        string invitationUrl,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendVerificationCodeAsync(
        string email,
        string code,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendEmployeeWelcomeAsync(
        string email,
        string companyName,
        string loginUrl,
        string temporaryPassword,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendMindMapSummaryAsync(
        string email,
        string companyName,
        string mapTitle,
        string shareUrl,
        string outlineText,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendMindMapInvitationAsync(
        string email,
        string companyName,
        string mapTitle,
        string shareUrl,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendMindMapStatusChangedAsync(
        string email,
        string companyName,
        string mapTitle,
        string mapStatus,
        string shareUrl,
        CancellationToken cancellationToken);

    Task<ConsoleEmailResult> SendMindMapFinalAsync(
        string email,
        string companyName,
        string mapTitle,
        string shareUrl,
        string outlineText,
        string? imageDataUrl,
        CancellationToken cancellationToken);
}

public sealed record ConsoleEmailResult(
    bool Success,
    string Provider,
    string? ProviderMessageId,
    string? ErrorText)
{
    public string Message => Success ? "Email sent." : ErrorText ?? "Email delivery failed.";
}
