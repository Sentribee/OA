using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IServerResourceService
{
    Task<IReadOnlyList<ServerResourceSnapshot>> ListAsync(
        int usedInstanceCount,
        CancellationToken cancellationToken);

    Task<ServerResourceControlResult> StartAsync(
        string instanceName,
        CancellationToken cancellationToken);

    Task<ServerResourceControlResult> StopAsync(
        string instanceName,
        CancellationToken cancellationToken);
}

public sealed record ServerResourceControlResult(
    bool Success,
    string Message);
