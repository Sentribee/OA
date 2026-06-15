using System.Net;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public sealed class EdgeDeviceService(
    IEdgeDeviceRepository repository,
    IProjectRepository projectRepository,
    IEdgeAiRepository edgeAiRepository) : IEdgeDeviceService
{
    public Task<IReadOnlyList<DeviceCatalogItem>> ListCatalogAsync(CancellationToken cancellationToken)
    {
        return repository.ListCatalogAsync(cancellationToken);
    }

    public Task<PagedResult<EdgeDevice>> ListByAdminAsync(
        int adminId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return repository.ListByAdminAsync(adminId, NormalizePage(pageNumber), NormalizePageSize(pageSize), cancellationToken);
    }

    public Task<EdgeDevice?> FindByAdminAsync(int adminId, int deviceId, CancellationToken cancellationToken)
    {
        return repository.FindByAdminAsync(adminId, deviceId, cancellationToken);
    }

    public async Task<EdgeDevice> CreateAsync(
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
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipAddress, out _))
        {
            throw new ArgumentException("Enter a valid IP address.", nameof(ipAddress));
        }

        if (latitude.HasValue != longitude.HasValue)
        {
            throw new ArgumentException("Select a complete Google Maps address.", nameof(address));
        }

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ArgumentException("Select a valid Google Maps location.", nameof(address));
        }

        if (string.IsNullOrWhiteSpace(serverResourceInstanceName))
        {
            throw new ArgumentException("Bind a server resource before creating the edge device.", nameof(serverResourceInstanceName));
        }

        var validEndpoints = endpoints
            .Where(endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.DeviceName) &&
                !string.IsNullOrWhiteSpace(endpoint.AccessUrl))
            .Select(endpoint => new EdgeDeviceEndpointInput(
                endpoint.CatalogDeviceId,
                endpoint.DeviceName.Trim(),
                endpoint.AccessUrl.Trim()))
            .ToList();

        if (validEndpoints.Count == 0)
        {
            throw new ArgumentException("Configure at least one external device with a device name and access URL.", nameof(endpoints));
        }

        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Create a project before adding edge devices.");
        RequireEdgeDeviceManager(project);

        var selectedVersion = edgeAiCodeVersionId.HasValue
            ? await edgeAiRepository.FindVersionAsync(project.Id, edgeAiCodeVersionId.Value, cancellationToken)
            : null;
        if (edgeAiCodeVersionId.HasValue && selectedVersion is null)
        {
            throw new ArgumentException("Select a valid Edge AI code version.", nameof(edgeAiCodeVersionId));
        }

        var device = await repository.CreateAsync(
            adminId,
            project.Id,
            name.Trim(),
            address.Trim(),
            latitude,
            longitude,
            NormalizeOptional(googlePlaceId),
            NormalizeOptional(streetViewThumbnailUrl),
            ipAddress.Trim(),
            serverResourceInstanceName.Trim(),
            NormalizeOptional(description),
            validEndpoints,
            cancellationToken);

        if (selectedVersion is not null)
        {
            await edgeAiRepository.CreateInstanceAsync(
                selectedVersion.Value.LogicId,
                device.Id,
                selectedVersion.Value.VersionId,
                $"{device.Name} {selectedVersion.Value.LogicName}",
                "Pending",
                cancellationToken);
        }

        return device;
    }

    public async Task<bool> DeleteAsync(int adminId, int deviceId, CancellationToken cancellationToken)
    {
        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Create a project before deleting edge devices.");
        RequireEdgeDeviceManager(project);
        return await repository.DeleteAsync(adminId, deviceId, cancellationToken);
    }

    public async Task<bool> UpdateProfileAsync(
        int adminId,
        int deviceId,
        string name,
        string serverResourceInstanceName,
        string? description,
        IReadOnlyList<EdgeDeviceEndpointInput> endpoints,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverResourceInstanceName))
        {
            throw new ArgumentException("Bind a server resource before saving the edge device.", nameof(serverResourceInstanceName));
        }

        var validEndpoints = endpoints
            .Where(endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.DeviceName) &&
                !string.IsNullOrWhiteSpace(endpoint.AccessUrl))
            .Select(endpoint => new EdgeDeviceEndpointInput(
                endpoint.CatalogDeviceId,
                endpoint.DeviceName.Trim(),
                endpoint.AccessUrl.Trim()))
            .ToList();

        if (validEndpoints.Count == 0)
        {
            throw new ArgumentException("Add at least one external device configuration.", nameof(endpoints));
        }

        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Create a project before updating edge devices.");
        RequireEdgeDeviceManager(project);

        return await repository.UpdateProfileAsync(
            adminId,
            deviceId,
            name.Trim(),
            serverResourceInstanceName.Trim(),
            NormalizeOptional(description),
            validEndpoints,
            cancellationToken);
    }

    public async Task<PagedResult<EdgeEvent>> ListEventsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken);
        return await repository.ListEventsByAdminAsync(
            adminId,
            NormalizeEventFilters(filters, project?.TimeZoneId),
            NormalizePage(pageNumber),
            NormalizePageSize(pageSize),
                cancellationToken);
    }

    public async Task<PagedResult<EdgeEventSubject>> ListEventSubjectsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken);
        return await repository.ListEventSubjectsByAdminAsync(
            adminId,
            NormalizeEventFilters(filters, project?.TimeZoneId),
            NormalizePage(pageNumber),
            NormalizePageSize(pageSize),
            cancellationToken);
    }

    public Task<IReadOnlyList<EdgeDevice>> ListEventDevicesByAdminAsync(int adminId, CancellationToken cancellationToken)
    {
        return repository.ListEventDevicesByAdminAsync(adminId, cancellationToken);
    }

    public async Task<EdgeEventStatusCounts> GetEventStatusCountsAsync(
        int adminId,
        EdgeEventFilters filters,
        CancellationToken cancellationToken)
    {
        var project = await projectRepository.FindByAdminIdAsync(adminId, cancellationToken);
        return await repository.GetEventStatusCountsAsync(
            adminId,
            NormalizeEventFilters(filters, project?.TimeZoneId),
            cancellationToken);
    }

    private static int NormalizePage(int pageNumber) => Math.Max(1, pageNumber);

    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 5, 100);

    private static EdgeEventFilters NormalizeEventFilters(EdgeEventFilters filters, string? timeZoneId)
    {
        return filters with
        {
            DateFrom = filters.DateFrom.HasValue
                ? ProjectTimeZone.ConvertLocalToUtc(filters.DateFrom.Value.Date, timeZoneId)
                : null,
            DateTo = filters.DateTo.HasValue
                ? ProjectTimeZone.ConvertLocalToUtc(filters.DateTo.Value.Date.AddDays(1), timeZoneId)
                : null,
            LearningStatus = NormalizeOptional(filters.LearningStatus)
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void RequireEdgeDeviceManager(Project project)
    {
        if (!project.CanManageEdgeDevices)
        {
            throw new UnauthorizedAccessException("This project role cannot manage edge devices.");
        }
    }
}
