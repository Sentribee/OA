using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeDeviceService
{
    Task<IReadOnlyList<DeviceCatalogItem>> ListCatalogAsync(CancellationToken cancellationToken);

    Task<PagedResult<EdgeDevice>> ListByAdminAsync(
        int adminId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken);

    Task<EdgeDevice?> FindByAdminAsync(int adminId, int deviceId, CancellationToken cancellationToken);

    Task<EdgeDevice> CreateAsync(
        int adminId,
        string name,
        string address,
        decimal? latitude,
        decimal? longitude,
        string? googlePlaceId,
        string? streetViewThumbnailUrl,
        string ipAddress,
        string serverResourceInstanceName,
        string? description,
        int? edgeAiCodeVersionId,
        IReadOnlyList<EdgeDeviceEndpointInput> endpoints,
        CancellationToken cancellationToken);

    Task<bool> UpdateProfileAsync(
        int adminId,
        int deviceId,
        string name,
        string serverResourceInstanceName,
        string? description,
        IReadOnlyList<EdgeDeviceEndpointInput> endpoints,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(int adminId, int deviceId, CancellationToken cancellationToken);

    Task<PagedResult<EdgeEvent>> ListEventsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<EdgeEventSubject>> ListEventSubjectsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EdgeDevice>> ListEventDevicesByAdminAsync(int adminId, CancellationToken cancellationToken);

    Task<EdgeEventStatusCounts> GetEventStatusCountsAsync(
        int adminId,
        EdgeEventFilters filters,
        CancellationToken cancellationToken);
}
