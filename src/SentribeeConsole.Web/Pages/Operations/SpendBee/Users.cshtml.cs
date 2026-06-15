using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

public class UsersModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<SpendBeeUserRow> Users { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "SpendBee Users";
        ViewData["PageTitle"] = "SpendBee Users";
        ViewData["ActiveMenu"] = "SpendBeeUsers";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        var pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = """
            SELECT COUNT(*)
            FROM bee_AppUser
            WHERE ProjectId = @ProjectId;
            """;
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT user.id, user.Email, user.DisplayName, user.Gender, user.AvatarUrl, user.Bio,
                user.Status, user.ActivatedAtUtc, user.CreatedAtUtc, user.UpdatedAtUtc,
                COUNT(device.id) AS DeviceCount,
                MAX(device.LastLoginAtUtc) AS LastDeviceLoginAtUtc
            FROM bee_AppUser AS user
            LEFT JOIN bee_AppUserDevice AS device ON device.AppUserId = user.id
            WHERE user.ProjectId = @ProjectId
            GROUP BY user.id, user.Email, user.DisplayName, user.Gender, user.AvatarUrl, user.Bio,
                user.Status, user.ActivatedAtUtc, user.CreatedAtUtc, user.UpdatedAtUtc
            ORDER BY user.CreatedAtUtc DESC, user.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeUserRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeUserRow(
                reader.GetInt32(reader.GetOrdinal("id")),
                reader["Email"] as string,
                reader["DisplayName"] as string ?? string.Empty,
                reader["Gender"] as string,
                reader["AvatarUrl"] as string,
                reader["Bio"] as string,
                reader["Status"] as string ?? string.Empty,
                reader.IsDBNull(reader.GetOrdinal("ActivatedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ActivatedAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
                Convert.ToInt32(reader["DeviceCount"]),
                reader.IsDBNull(reader.GetOrdinal("LastDeviceLoginAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastDeviceLoginAtUtc"))));
        }

        Users = new PagedResult<SpendBeeUserRow>
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

public sealed record SpendBeeUserRow(
    int Id,
    string? Email,
    string DisplayName,
    string? Gender,
    string? AvatarUrl,
    string? Bio,
    string Status,
    DateTime? ActivatedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int DeviceCount,
    DateTime? LastDeviceLoginAtUtc);
