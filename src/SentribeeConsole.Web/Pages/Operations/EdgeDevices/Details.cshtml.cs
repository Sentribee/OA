using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using QRCoder;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.EdgeDevices;

[Authorize]
public sealed class DetailsModel(
    IEdgeDeviceService edgeDeviceService,
    IEdgeRuntimeService edgeRuntimeService,
    IProjectService projectService,
    IServerResourceService serverResourceService,
    IWeatherForecastService weatherForecastService,
    IConfiguration configuration) : PageModel
{
    [BindProperty]
    public EditDeviceInput EditInput { get; set; } = new();

    [BindProperty]
    public List<EnvironmentVariableInput> EnvironmentInput { get; set; } = [];

    public EdgeDevice Device { get; private set; } = new();

    public Project Project { get; private set; } = new();

    public string GoogleMapsApiKey { get; private set; } = string.Empty;

    public string? LiveStreamUrl { get; private set; }

    public WeatherForecastSummary Weather { get; private set; } = new();

    public decimal StreetViewHeadingDegrees { get; private set; }

    public IReadOnlyList<BoundAppUser> BoundUsers { get; private set; } = [];

    public IReadOnlyList<EdgeDeviceDailyStatView> DailyStats { get; private set; } = [];

    public IReadOnlyList<EdgeDeviceDailyRiskPersonView> DailyRiskPeople { get; private set; } = [];

    public bool ShowRuntimeEnvironment { get; private set; }

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        Device = device;
        Project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
        StatusMessage = TempData["EdgeDeviceStatus"] as string;
        StatusIsError = TempData["EdgeDeviceStatusError"] as bool? ?? false;
        LiveStreamUrl = await ResolveLiveStreamUrlAsync(device, cancellationToken);
        Weather = await ResolveWeatherAsync(device, cancellationToken);
        BoundUsers = await LoadBoundUsersAsync(device, cancellationToken);
        DailyStats = await LoadDailyStatsAsync(device, cancellationToken);
        DailyRiskPeople = await LoadDailyRiskPeopleAsync(device, DailyStats.FirstOrDefault()?.StatDate, cancellationToken);
        ShowRuntimeEnvironment = Project.CanManageEdgeDevices && configuration.GetValue("EdgeRuntime:ShowEnvironmentEditor", false);
        if (ShowRuntimeEnvironment)
        {
            EnvironmentInput = await ResolveEnvironmentInputAsync(device, cancellationToken);
        }

        StreetViewHeadingDegrees = ResolveStreetViewHeading(device.StreetViewThumbnailUrl);
        EditInput = new EditDeviceInput
        {
            Name = device.Name,
            ServerResourceInstanceName = device.ServerResourceInstanceName ?? "i-05a6a5077f2ee8dd4",
            Description = device.Description,
            Endpoints = device.Endpoints.Select(endpoint => new EndpointInput
            {
                CatalogDeviceId = endpoint.CatalogDeviceId,
                DeviceName = endpoint.DeviceName,
                AccessUrl = endpoint.AccessUrl
            }).ToList()
        };
        GoogleMapsApiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, CancellationToken cancellationToken)
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
            var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
            if (device is null)
            {
                return NotFound();
            }

            Device = device;
            Project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
            LiveStreamUrl = await ResolveLiveStreamUrlAsync(device, cancellationToken);
            Weather = await ResolveWeatherAsync(device, cancellationToken);
            DailyStats = await LoadDailyStatsAsync(device, cancellationToken);
            DailyRiskPeople = await LoadDailyRiskPeopleAsync(device, DailyStats.FirstOrDefault()?.StatDate, cancellationToken);
            ShowRuntimeEnvironment = Project.CanManageEdgeDevices && configuration.GetValue("EdgeRuntime:ShowEnvironmentEditor", false);
            if (ShowRuntimeEnvironment)
            {
                EnvironmentInput = await ResolveEnvironmentInputAsync(device, cancellationToken);
            }

            StreetViewHeadingDegrees = ResolveStreetViewHeading(device.StreetViewThumbnailUrl);
            GoogleMapsApiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
            return Page();
        }

        try
        {
            var updated = await edgeDeviceService.UpdateProfileAsync(
                adminId.Value,
                id,
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
            return RedirectToPage(new { id });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
            if (device is null)
            {
                return NotFound();
            }

            Device = device;
            Project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
            LiveStreamUrl = await ResolveLiveStreamUrlAsync(device, cancellationToken);
            Weather = await ResolveWeatherAsync(device, cancellationToken);
            DailyStats = await LoadDailyStatsAsync(device, cancellationToken);
            DailyRiskPeople = await LoadDailyRiskPeopleAsync(device, DailyStats.FirstOrDefault()?.StatDate, cancellationToken);
            ShowRuntimeEnvironment = Project.CanManageEdgeDevices && configuration.GetValue("EdgeRuntime:ShowEnvironmentEditor", false);
            if (ShowRuntimeEnvironment)
            {
                EnvironmentInput = await ResolveEnvironmentInputAsync(device, cancellationToken);
            }

            StreetViewHeadingDegrees = ResolveStreetViewHeading(device.StreetViewThumbnailUrl);
            GoogleMapsApiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
            return Page();
        }
    }

    public async Task<IActionResult> OnGetStatusAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { authenticated = false });
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
        if (device is null)
        {
            return new JsonResult(new { authenticated = true, found = false });
        }

        return new JsonResult(new
        {
            authenticated = true,
            found = true,
            id = device.Id,
            isOnline = device.IsOnline,
            needsRemoteDeviceRepair = device.NeedsRemoteDeviceRepair,
            statusText = device.NeedsRemoteDeviceRepair ? "Remote device offline" : device.IsOnline ? "Online" : "Offline",
            lastHeartbeat = ProjectTimeZone.Format(
                device.LastHeartbeatAtUtc,
                (await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken))?.TimeZoneId,
                "yyyy-MM-dd HH:mm:ss",
                "No heartbeat received"),
            runtimeStatus = device.RuntimeStatus ?? "Unknown",
            deviceStatus = device.DeviceStatus ?? "Unknown"
        });
    }

    public async Task<IActionResult> OnGetBindingQrAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        var bindingCode = await EnsureBindingCodeAsync(device, cancellationToken);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(bindingCode, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        var png = qrCode.GetGraphic(12);
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(png, "image/png");
    }

    public async Task<IActionResult> OnPostStartRuntimeAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { success = false, message = "Authentication required." }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
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

    public async Task<IActionResult> OnPostRestartRuntimeAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { success = false, message = "Authentication required." }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
        if (device is null)
        {
            return new JsonResult(new { success = false, message = "Edge device not found." }) { StatusCode = StatusCodes.Status404NotFound };
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return new JsonResult(new { success = false, message = "This project role cannot restart edge device runtimes." }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        var result = await edgeRuntimeService.StartAsync(device, cancellationToken);
        return new JsonResult(new { success = result.Success, message = result.Message })
        {
            StatusCode = result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest
        };
    }

    public async Task<IActionResult> OnPostSaveEnvironmentAsync(int id, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var device = await edgeDeviceService.FindByAdminAsync(adminId.Value, id, cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        if (!await CanManageEdgeDevicesAsync(adminId.Value, cancellationToken))
        {
            return Forbid();
        }

        var values = EnvironmentInput
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var result = await edgeRuntimeService.SaveEditableEnvironmentAsync(device, values, cancellationToken);
        TempData["EdgeDeviceStatus"] = result.Success ? "Environment variables saved." : result.Message;
        TempData["EdgeDeviceStatusError"] = !result.Success;
        return RedirectToPage(new { id });
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    private async Task<bool> CanManageEdgeDevicesAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
        return project?.CanManageEdgeDevices == true;
    }

    private async Task<string?> ResolveLiveStreamUrlAsync(EdgeDevice device, CancellationToken cancellationToken)
    {
        if (!device.IsOnline || string.IsNullOrWhiteSpace(device.ServerResourceInstanceName))
        {
            return null;
        }

        var resources = await serverResourceService.ListAsync(0, cancellationToken);
        var resource = resources.FirstOrDefault(item =>
            string.Equals(item.InstanceName, device.ServerResourceInstanceName, StringComparison.OrdinalIgnoreCase));
        var host = string.IsNullOrWhiteSpace(resource?.PublicDomain)
            ? resource?.PublicIpAddress
            : resource.PublicDomain;

        return string.IsNullOrWhiteSpace(host)
            ? null
            : $"https://{host}/instances/{Uri.EscapeDataString(device.DeviceCode)}/video/index.m3u8";
    }

    private async Task<string> EnsureBindingCodeAsync(
        EdgeDevice device,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string selectSql = """
            SELECT BindingCode
            FROM bee_EdgeDevice
            WHERE id = @EdgeDeviceId
                AND ProjectId = @ProjectId
            LIMIT 1;
            """;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using (var selectCommand = new MySqlCommand(selectSql, connection))
            {
                selectCommand.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Id;
                selectCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = device.ProjectId;
                var existing = await selectCommand.ExecuteScalarAsync(cancellationToken) as string;
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            var bindingCode = GenerateBindingCode();
            const string updateSql = """
                UPDATE bee_EdgeDevice
                SET BindingCode = @BindingCode,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @EdgeDeviceId
                    AND ProjectId = @ProjectId
                    AND BindingCode IS NULL;
                """;
            await using var updateCommand = new MySqlCommand(updateSql, connection);
            updateCommand.Parameters.Add("@BindingCode", MySqlDbType.VarChar, 16).Value = bindingCode;
            updateCommand.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Id;
            updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = device.ProjectId;
            try
            {
                if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    return bindingCode;
                }
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                // A rare duplicate code collision; retry with a new short code.
            }
        }

        throw new InvalidOperationException("Unable to generate a unique device binding code.");
    }

    private static string GenerateBindingCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[10];
        chars[0] = 'S';
        chars[1] = 'B';
        for (var index = 0; index < bytes.Length; index++)
        {
            chars[index + 2] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(chars);
    }

    private async Task<IReadOnlyList<BoundAppUser>> LoadBoundUsersAsync(
        EdgeDevice device,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT user.PhoneNumber, user.Email, user.DisplayName, binding.BoundAtUtc
            FROM bee_EdgeDeviceUserBinding AS binding
            INNER JOIN bee_AppUser AS user ON user.id = binding.AppUserId
            WHERE binding.EdgeDeviceId = @EdgeDeviceId
            ORDER BY binding.BoundAtUtc DESC;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Id;
        var users = new List<BoundAppUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new BoundAppUser(
                reader["PhoneNumber"] as string ?? string.Empty,
                reader["Email"] as string ?? string.Empty,
                reader["DisplayName"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("BoundAtUtc"))));
        }

        return users;
    }

    private async Task<WeatherForecastSummary> ResolveWeatherAsync(EdgeDevice device, CancellationToken cancellationToken)
    {
        if (!device.Latitude.HasValue || !device.Longitude.HasValue)
        {
            return new WeatherForecastSummary
            {
                LocationName = device.Name,
                Message = "Device coordinates are not available."
            };
        }

        return await weatherForecastService.GetNext24HoursAsync(
            device.Latitude.Value,
            device.Longitude.Value,
            device.Name,
            cancellationToken);
    }

    private async Task<List<EnvironmentVariableInput>> ResolveEnvironmentInputAsync(
        EdgeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            var variables = await edgeRuntimeService.GetEditableEnvironmentAsync(device, cancellationToken);
            return variables.Select(variable => new EnvironmentVariableInput
            {
                Name = variable.Name,
                Value = variable.Value,
                IsSecret = variable.IsSecret,
                Source = variable.Source
            }).ToList();
        }
        catch (Exception exception)
        {
            StatusMessage ??= $"Unable to load runtime environment: {exception.Message}";
            StatusIsError = true;
            return [];
        }
    }

    private async Task<IReadOnlyList<EdgeDeviceDailyStatView>> LoadDailyStatsAsync(
        EdgeDevice device,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT StatDate, PeopleCount, BraceletCount, MachineryVehicleCount, PpeComplianceRate,
                RiskEventCount, RiskPersonCount, TopRiskSubjectKey, TopRiskSubjectRiskCount,
                LastHeartbeatAtUtc, LastEventAtUtc
            FROM bee_EdgeDeviceDailyStat
            WHERE EdgeDeviceId = @EdgeDeviceId
            ORDER BY StatDate DESC
            LIMIT 14;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Id;
        var stats = new List<EdgeDeviceDailyStatView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stats.Add(new EdgeDeviceDailyStatView(
                DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("StatDate"))),
                reader.GetInt32(reader.GetOrdinal("PeopleCount")),
                reader.GetInt32(reader.GetOrdinal("BraceletCount")),
                reader.GetInt32(reader.GetOrdinal("MachineryVehicleCount")),
                reader["PpeComplianceRate"] is DBNull ? null : reader.GetDecimal(reader.GetOrdinal("PpeComplianceRate")),
                reader.GetInt32(reader.GetOrdinal("RiskEventCount")),
                reader.GetInt32(reader.GetOrdinal("RiskPersonCount")),
                reader["TopRiskSubjectKey"] as string,
                reader.GetInt32(reader.GetOrdinal("TopRiskSubjectRiskCount")),
                reader["LastHeartbeatAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("LastHeartbeatAtUtc")),
                reader["LastEventAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("LastEventAtUtc"))));
        }

        return stats;
    }

    private async Task<IReadOnlyList<EdgeDeviceDailyRiskPersonView>> LoadDailyRiskPeopleAsync(
        EdgeDevice device,
        DateOnly? statDate,
        CancellationToken cancellationToken)
    {
        if (statDate is null)
        {
            return [];
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT PersonGroupKey, DisplayLabel, RepresentativeSubjectId, RepresentativeCropImageUrl, RepresentativePreviewImageUrl,
                RiskEventCount, RiskSubjectCount, FirstEventAtUtc, LastEventAtUtc
            FROM bee_EdgeDeviceDailyRiskPerson
            WHERE EdgeDeviceId = @EdgeDeviceId
                AND StatDate = @StatDate
            ORDER BY RiskEventCount DESC, RiskSubjectCount DESC, PersonGroupKey
            LIMIT 10;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Id;
        command.Parameters.Add("@StatDate", MySqlDbType.Date).Value = statDate.Value.ToDateTime(TimeOnly.MinValue);
        var people = new List<EdgeDeviceDailyRiskPersonView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            people.Add(new EdgeDeviceDailyRiskPersonView(
                reader["PersonGroupKey"] as string ?? string.Empty,
                reader["DisplayLabel"] as string,
                reader["RepresentativeSubjectId"] is DBNull ? null : reader.GetInt64(reader.GetOrdinal("RepresentativeSubjectId")),
                reader["RepresentativeCropImageUrl"] as string,
                reader["RepresentativePreviewImageUrl"] as string,
                reader.GetInt32(reader.GetOrdinal("RiskEventCount")),
                reader.GetInt32(reader.GetOrdinal("RiskSubjectCount")),
                reader["FirstEventAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("FirstEventAtUtc")),
                reader["LastEventAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("LastEventAtUtc"))));
        }

        return people;
    }

    private static decimal ResolveStreetViewHeading(string? streetViewUrl)
    {
        if (string.IsNullOrWhiteSpace(streetViewUrl) ||
            !Uri.TryCreate(streetViewUrl, UriKind.Absolute, out var uri))
        {
            return 0;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("heading", out var heading) &&
            decimal.TryParse(heading.FirstOrDefault(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public sealed class EditDeviceInput
    {
        [Required]
        [StringLength(150)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(80)]
        [Display(Name = "Server Resource")]
        public string ServerResourceInstanceName { get; set; } = string.Empty;

        [StringLength(2000)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        public List<EndpointInput> Endpoints { get; set; } = [];
    }

    public sealed class EndpointInput
    {
        public int? CatalogDeviceId { get; set; }

        [StringLength(150)]
        public string DeviceName { get; set; } = string.Empty;

        [StringLength(500)]
        public string AccessUrl { get; set; } = string.Empty;
    }

    public sealed class EnvironmentVariableInput
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }

        public bool IsSecret { get; set; }

        public string Source { get; set; } = string.Empty;
    }

    public sealed record BoundAppUser(string PhoneNumber, string Email, string DisplayName, DateTime BoundAtUtc);

    public sealed record EdgeDeviceDailyStatView(
        DateOnly StatDate,
        int PeopleCount,
        int BraceletCount,
        int MachineryVehicleCount,
        decimal? PpeComplianceRate,
        int RiskEventCount,
        int RiskPersonCount,
        string? TopRiskSubjectKey,
        int TopRiskSubjectRiskCount,
        DateTime? LastHeartbeatAtUtc,
        DateTime? LastEventAtUtc);

    public sealed record EdgeDeviceDailyRiskPersonView(
        string PersonGroupKey,
        string? DisplayLabel,
        long? RepresentativeSubjectId,
        string? RepresentativeCropImageUrl,
        string? RepresentativePreviewImageUrl,
        int RiskEventCount,
        int RiskSubjectCount,
        DateTime? FirstEventAtUtc,
        DateTime? LastEventAtUtc);
}
