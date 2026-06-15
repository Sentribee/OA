using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

public class DevicesModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<SpendBeeDeviceRow> Devices { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "SpendBee Devices";
        ViewData["PageTitle"] = "SpendBee Devices";
        ViewData["ActiveMenu"] = "SpendBeeDevices";
        Project = await LoadCurrentProjectAsync(cancellationToken);
        var pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_AppUserDevice WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT device.id, device.DeviceIdentifier, device.DeviceKeyHash, device.DeviceType, device.Platform,
                device.OsVersion, device.AppVersion, device.PushProvider, device.PushToken,
                device.LastLoginAtUtc, device.CreatedAtUtc, user.DisplayName, user.Email
            FROM bee_AppUserDevice AS device
            INNER JOIN bee_AppUser AS user ON user.id = device.AppUserId
            WHERE device.ProjectId = @ProjectId
            ORDER BY device.LastLoginAtUtc DESC, device.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeDeviceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeDeviceRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["DisplayName"] as string ?? string.Empty,
                reader["Email"] as string,
                reader["DeviceIdentifier"] as string ?? string.Empty,
                reader["DeviceKeyHash"] as string,
                reader["DeviceType"] as string,
                reader["Platform"] as string,
                reader["OsVersion"] as string,
                reader["AppVersion"] as string,
                reader["PushProvider"] as string,
                reader["PushToken"] as string,
                reader.GetDateTime(reader.GetOrdinal("LastLoginAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        Devices = new PagedResult<SpendBeeDeviceRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private async Task<Project> LoadCurrentProjectAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
        {
            throw new InvalidOperationException("Administrator is not signed in.");
        }

        return await projectService.GetByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Project not found.");
    }
}

public sealed record SpendBeeDeviceRow(
    long Id,
    string DisplayName,
    string? Email,
    string DeviceIdentifier,
    string? DeviceKeyHash,
    string? DeviceType,
    string? Platform,
    string? OsVersion,
    string? AppVersion,
    string? PushProvider,
    string? PushToken,
    DateTime LastLoginAtUtc,
    DateTime CreatedAtUtc);
