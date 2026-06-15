using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeRuntimeService
{
    Task<EdgeRuntimeStartResult> StartAsync(EdgeDevice device, CancellationToken cancellationToken);

    Task<IReadOnlyList<EdgeRuntimeEnvironmentVariable>> GetEditableEnvironmentAsync(
        EdgeDevice device,
        CancellationToken cancellationToken);

    Task<EdgeRuntimeStartResult> SaveEditableEnvironmentAsync(
        EdgeDevice device,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken);
}

public sealed record EdgeRuntimeStartResult(bool Success, string Message);

public sealed record EdgeRuntimeEnvironmentVariable(
    string Name,
    string Value,
    bool IsSecret,
    string Source);
