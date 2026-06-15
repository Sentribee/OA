using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.EdgeDevices;

[Authorize]
public sealed class IndexModel(
    IEdgeDeviceService edgeDeviceService,
    IEdgeRuntimeService edgeRuntimeService,
    IEdgeAiService edgeAiService,
    IProjectService projectService,
    IServerResourceService serverResourceService,
    IConfiguration configuration,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public CreateDeviceInput Input { get; set; } = new();

    [BindProperty]
    public EditDeviceInput EditInput { get; set; } = new();

    public IReadOnlyList<EdgeDevice> Devices { get; private set; } = [];

    public PagedResult<EdgeDevice> DevicePage { get; private set; } = new();

    public IReadOnlyList<DeviceCatalogItem> Catalog { get; private set; } = [];

    public IReadOnlyList<EdgeAiLogic> EdgeAiLogics { get; private set; } = [];

    public IReadOnlyList<ServerResourceSnapshot> ServerResources { get; private set; } = [];

    public Project Project { get; private set; } = new();

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    public bool ReopenCreateModal { get; private set; }

    public string GoogleMapsApiKey { get; private set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        await LoadPageAsync(adminId.Value, cancellationToken);
        StatusMessage = TempData["EdgeDeviceStatus"] as string;
        StatusIsError = TempData["EdgeDeviceStatusError"] as bool? ?? false;
        return Page();
    }

    public async Task<IActionResult> OnGetServerResourcesAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { authenticated = false });
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
        var resources = await LoadServerResourcesAsync(adminId.Value, cancellationToken);
        return new JsonResult(new
        {
            authenticated = true,
            resources = resources.Select(resource => new
            {
                instanceName = resource.InstanceName,
                publicDomain = resource.PublicDomain,
                status = resource.Status,
                publicIpAddress = resource.PublicIpAddress ?? "Unavailable",
                instanceType = resource.InstanceType ?? "Unavailable",
                gpuSummary = resource.GpuSummary ?? "Unavailable",
                memorySummary = resource.MemorySummary ?? "Unavailable",
                diskSummary = resource.DiskSummary ?? "Unavailable",
                loadSummary = resource.LoadSummary ?? "Unavailable",
                loadPercent = resource.LoadPercent,
                capacity = resource.Capacity,
                usedInstances = resource.UsedInstances,
                availableInstances = resource.AvailableInstances,
                usagePercent = resource.UsagePercent,
                metadataStatus = resource.MetadataStatus,
                updatedAt = ProjectTimeZone.Format(resource.UpdatedAtUtc, project.TimeZoneId, "HH:mm:ss")
            })
        });
    }

    public async Task<IActionResult> OnGetDeviceStatusesAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { authenticated = false });
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
        var page = await edgeDeviceService.ListByAdminAsync(adminId.Value, PageNumber, 100, cancellationToken);
        return new JsonResult(new
        {
            authenticated = true,
            devices = page.Items.Select(device => new
            {
                id = device.Id,
                isOnline = device.IsOnline,
                needsRemoteDeviceRepair = device.NeedsRemoteDeviceRepair,
                statusText = device.NeedsRemoteDeviceRepair ? "Remote device offline" : device.IsOnline ? "Online" : "Offline",
                lastHeartbeat = ProjectTimeZone.Format(device.LastHeartbeatAtUtc, project.TimeZoneId, "yyyy-MM-dd HH:mm:ss", "No heartbeat received"),
                runtimeStatus = device.RuntimeStatus ?? "Unknown",
                deviceStatus = device.DeviceStatus ?? "Unknown"
            })
        });
    }

    public async Task<IActionResult> OnPostStartRuntimeAsync(int deviceId, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { success = false, message = "Authentication required." }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, deviceId, cancellationToken);
        if (device is null)
        {
            return new JsonResult(new { success = false, message = "Edge device not found." }) { StatusCode = StatusCodes.Status404NotFound };
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return new JsonResult(new { success = false, message = "This project role cannot start edge device runtimes." }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        if (device.IsOnline)
        {
            return new JsonResult(new { success = true, message = "Edge device is already online." });
        }

        var result = await edgeRuntimeService.StartAsync(device, cancellationToken);
        return new JsonResult(new { success = result.Success, message = result.Message })
        {
            StatusCode = result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest
        };
    }

    public async Task<IActionResult> OnPostStartServerResourceAsync(string instanceName, CancellationToken cancellationToken)
    {
        return await ControlServerResourceAsync(
            instanceName,
            start: true,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostStopServerResourceAsync(string instanceName, CancellationToken cancellationToken)
    {
        return await ControlServerResourceAsync(
            instanceName,
            start: false,
            cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return Forbid();
        }

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            StatusMessage = BuildModelStateMessage();
            StatusIsError = true;
            ReopenCreateModal = true;
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }

        try
        {
            await edgeDeviceService.CreateAsync(
                adminId.Value,
                Input.Name,
                Input.Address,
                Input.Latitude,
                Input.Longitude,
                Input.GooglePlaceId,
                Input.StreetViewThumbnailUrl,
                Input.IpAddress,
                Input.ServerResourceInstanceName,
                Input.Description,
                Input.EdgeAiCodeVersionId,
                Input.Endpoints
                    .Where(endpoint => endpoint.Selected)
                    .Select(endpoint => new EdgeDeviceEndpointInput(
                        endpoint.CatalogDeviceId,
                        endpoint.DeviceName,
                        endpoint.AccessUrl))
                    .ToList(),
                cancellationToken);
            TempData["EdgeDeviceStatus"] = "Edge device created successfully.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Edge device creation validation failed for administrator {AdminId}.", adminId.Value);
            ModelState.AddModelError(string.Empty, exception.Message);
            StatusMessage = exception.Message;
            StatusIsError = true;
            ReopenCreateModal = true;
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(int deviceId, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return Forbid();
        }

        var removed = await edgeDeviceService.DeleteAsync(adminId.Value, deviceId, cancellationToken);
        TempData["EdgeDeviceStatus"] = removed ? "Edge device deleted." : "Edge device could not be found.";
        TempData["EdgeDeviceStatusError"] = !removed;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return Forbid();
        }

        ModelState.Clear();
        if (!TryValidateModel(EditInput, nameof(EditInput)))
        {
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }

        try
        {
            var updated = await edgeDeviceService.UpdateProfileAsync(
                adminId.Value,
                EditInput.DeviceId,
                EditInput.Name,
                EditInput.ServerResourceInstanceName,
                EditInput.Description,
                EditInput.Endpoints.Select(endpoint => new EdgeDeviceEndpointInput(
                    endpoint.CatalogDeviceId,
                    endpoint.DeviceName,
                    endpoint.AccessUrl)).ToList(),
                cancellationToken);
            TempData["EdgeDeviceStatus"] = updated ? "Edge device updated." : "Edge device could not be found.";
            TempData["EdgeDeviceStatusError"] = !updated;
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Edge device update validation failed for administrator {AdminId}.", adminId.Value);
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }
    }

    private async Task LoadPageAsync(int adminId, CancellationToken cancellationToken)
    {
        Catalog = await edgeDeviceService.ListCatalogAsync(cancellationToken);
        Project = await projectService.GetByAdminIdAsync(adminId, cancellationToken) ?? new Project();
        ServerResources = await LoadServerResourcesAsync(adminId, cancellationToken);
        GoogleMapsApiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
        DevicePage = await edgeDeviceService.ListByAdminAsync(adminId, PageNumber, 20, cancellationToken);
        Devices = DevicePage.Items;
        if (Input.Endpoints.Count == 0)
        {
            Input.Endpoints = Catalog.Select(item => new EndpointInput
            {
                CatalogDeviceId = item.Id,
                CatalogName = item.Name,
                DeviceName = item.Name,
                AccessUrl = string.Empty
            }).ToList();
        }
    }

    private async Task<IActionResult> ControlServerResourceAsync(
        string instanceName,
        bool start,
        CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return Forbid();
        }

        var result = start
            ? await serverResourceService.StartAsync(instanceName, cancellationToken)
            : await serverResourceService.StopAsync(instanceName, cancellationToken);

        TempData["EdgeDeviceStatus"] = result.Message;
        TempData["EdgeDeviceStatusError"] = !result.Success;
        return RedirectToPage();
    }

    private async Task<bool> CanManageEdgeDevicesAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
        return project?.CanManageEdgeDevices == true;
    }

    private async Task<IReadOnlyList<ServerResourceSnapshot>> LoadServerResourcesAsync(
        int adminId,
        CancellationToken cancellationToken)
    {
        var edgeAiDashboard = await edgeAiService.GetDashboardAsync(adminId, cancellationToken);
        EdgeAiLogics = edgeAiDashboard.Logics;
        var usedInstanceCount = EdgeAiLogics.SelectMany(logic => logic.Instances).Count();
        return await serverResourceService.ListAsync(usedInstanceCount, cancellationToken);
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    public string GetServerDomain(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return "Unbound";
        }

        return ServerResources.FirstOrDefault(resource =>
                string.Equals(resource.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase))
            ?.PublicDomain ?? instanceName;
    }

    public static bool CanStartServerResource(ServerResourceSnapshot resource)
    {
        return string.Equals(resource.Status, "Stopped", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanStopServerResource(ServerResourceSnapshot resource)
    {
        return string.Equals(resource.Status, "Available", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class CreateDeviceInput
    {
        [Required]
        [StringLength(150)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(300)]
        [Display(Name = "Address")]
        public string Address { get; set; } = string.Empty;

        public decimal? Latitude { get; set; }

        public decimal? Longitude { get; set; }

        [StringLength(200)]
        public string? GooglePlaceId { get; set; }

        [StringLength(1000)]
        public string? StreetViewThumbnailUrl { get; set; }

        [Required]
        [StringLength(45)]
        [RegularExpression(@"^((25[0-5]|2[0-4]\d|1?\d?\d)(\.|$)){4}$|^([0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}$", ErrorMessage = "Enter a valid IP address.")]
        [Display(Name = "IP Address")]
        public string IpAddress { get; set; } = string.Empty;

        [StringLength(2000)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Required]
        [StringLength(80)]
        [Display(Name = "Server Resource")]
        public string ServerResourceInstanceName { get; set; } = string.Empty;

        [Required]
        [Display(Name = "AI Code Version")]
        public int? EdgeAiCodeVersionId { get; set; }

        public List<EndpointInput> Endpoints { get; set; } = [];
    }

    public sealed class EditDeviceInput
    {
        [Required]
        public int DeviceId { get; set; }

        [Required]
        [StringLength(150)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(2000)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Required]
        [StringLength(80)]
        [Display(Name = "Server Resource")]
        public string ServerResourceInstanceName { get; set; } = string.Empty;

        public List<EndpointInput> Endpoints { get; set; } = [];
    }

    public static string GetRequiredCatalogNames(EdgeAiCodeVersion version)
    {
        var text = string.Join(' ', version.Description, version.DirectoryStructure, version.FeatureList, version.Notes);
        var required = new List<string>();
        if (text.Contains("RTSP", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("camera", StringComparison.OrdinalIgnoreCase))
        {
            required.Add("RTSP Camera");
        }

        if (text.Contains("BLE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bracelet", StringComparison.OrdinalIgnoreCase))
        {
            required.Add("Bluetooth Gateway");
        }

        return string.Join("|", required.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string BuildModelStateMessage()
    {
        return ModelState
            .SelectMany(entry => entry.Value?.Errors ?? [])
            .Select(error => error.ErrorMessage)
            .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error))
            ?? "Check the highlighted fields and try again.";
    }

    public sealed class EndpointInput
    {
        public bool Selected { get; set; }

        public int? CatalogDeviceId { get; set; }

        public string CatalogName { get; set; } = string.Empty;

        [StringLength(150)]
        public string DeviceName { get; set; } = string.Empty;

        [StringLength(500)]
        public string AccessUrl { get; set; } = string.Empty;
    }

}
