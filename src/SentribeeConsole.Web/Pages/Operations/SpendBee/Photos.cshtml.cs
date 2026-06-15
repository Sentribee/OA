using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

[Authorize]
public class PhotosModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<SpendBeePhotoRow> Photos { get; private set; } = new();

    public SpendBeePhotoSummary Summary { get; private set; } = new();

    public IReadOnlyList<SpendBeePhotoDuplicateGroup> DuplicateGroups { get; private set; } = [];

    public async Task OnGetAsync(
        int pageNumber = 1,
        string? status = null,
        long? merchantId = null,
        int? appUserId = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "SpendBee Merchant Photos";
        ViewData["PageTitle"] = "SpendBee Merchant Photos";
        ViewData["ActiveMenu"] = "SpendBeePhotos";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        var pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);

        var normalizedStatus = NormalizeFilter(status);
        var normalizedCategory = NormalizeFilter(category);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        Summary = await LoadSummaryAsync(connection, Project.Id, cancellationToken);
        DuplicateGroups = await LoadDuplicateGroupsAsync(connection, Project.Id, cancellationToken);

        var where = """
            photo.ProjectId = @ProjectId
            AND photo.Status <> 'Deleted'
            AND LOWER(COALESCE(photo.Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
            AND (@Status IS NULL OR photo.Status = @Status)
            AND (@MerchantId IS NULL OR photo.MerchantId = @MerchantId)
            AND (@AppUserId IS NULL OR photo.AppUserId = @AppUserId)
            AND (@Category IS NULL OR LOWER(COALESCE(photo.Category, 'group')) = LOWER(@Category))
            """;

        var countSql = $"SELECT COUNT(*) FROM bee_SpendBeeMerchantPhoto AS photo WHERE {where};";
        await using var countCommand = new MySqlCommand(countSql, connection);
        AddFilterParameters(countCommand, Project.Id, normalizedStatus, merchantId, appUserId, normalizedCategory);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var sql = $"""
            SELECT photo.id, photo.MerchantId, merchant.Name AS MerchantName, merchant.Address AS MerchantAddress,
                photo.AppUserId, user.DisplayName, user.Email, user.Gender,
                photo.UploadId, photo.Category, photo.Caption, photo.OriginalImageUrl, photo.DisplayImageUrl,
                photo.Status, photo.ProcessingError, photo.CreatedAtUtc, photo.UpdatedAtUtc,
                COUNT(DISTINCT likeRow.AppUserId) AS LikeCount,
                COUNT(DISTINCT comment.id) AS CommentCount
            FROM bee_SpendBeeMerchantPhoto AS photo
            LEFT JOIN bee_SpendBeeMerchant AS merchant ON merchant.id = photo.MerchantId
            LEFT JOIN bee_AppUser AS user ON user.id = photo.AppUserId
            LEFT JOIN bee_SpendBeeMerchantPhotoLike AS likeRow ON likeRow.PhotoId = photo.id
            LEFT JOIN bee_SpendBeeMerchantPhotoComment AS comment ON comment.PhotoId = photo.id AND comment.Status = 'Visible'
            WHERE {where}
            GROUP BY photo.id, photo.MerchantId, merchant.Name, merchant.Address, photo.AppUserId, user.DisplayName,
                user.Email, user.Gender, photo.UploadId, photo.Category, photo.Caption, photo.OriginalImageUrl,
                photo.DisplayImageUrl, photo.Status, photo.ProcessingError, photo.CreatedAtUtc, photo.UpdatedAtUtc
            ORDER BY photo.CreatedAtUtc DESC, photo.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        AddFilterParameters(command, Project.Id, normalizedStatus, merchantId, appUserId, normalizedCategory);
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;

        var rows = new List<SpendBeePhotoRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeePhotoRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("MerchantId")),
                reader["MerchantName"] as string ?? $"Merchant {reader.GetInt64(reader.GetOrdinal("MerchantId"))}",
                reader["MerchantAddress"] as string,
                reader.GetInt32(reader.GetOrdinal("AppUserId")),
                reader["DisplayName"] as string ?? $"User {reader.GetInt32(reader.GetOrdinal("AppUserId"))}",
                reader["Email"] as string,
                reader["Gender"] as string,
                reader.GetInt64(reader.GetOrdinal("UploadId")),
                reader["Category"] as string,
                reader["Caption"] as string,
                reader["OriginalImageUrl"] as string,
                reader["DisplayImageUrl"] as string,
                reader["Status"] as string ?? string.Empty,
                reader["ProcessingError"] as string,
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("UpdatedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
                Convert.ToInt32(reader["LikeCount"]),
                Convert.ToInt32(reader["CommentCount"])));
        }

        Photos = new PagedResult<SpendBeePhotoRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private static async Task<SpendBeePhotoSummary> LoadSummaryAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) AS TotalCount,
                SUM(CASE WHEN Status = 'Ready' THEN 1 ELSE 0 END) AS ReadyCount,
                SUM(CASE WHEN Status = 'ProcessingFailed' THEN 1 ELSE 0 END) AS FailedCount,
                SUM(CASE WHEN Status = 'Processing' THEN 1 ELSE 0 END) AS ProcessingCount,
                COUNT(DISTINCT MerchantId) AS MerchantCount,
                COUNT(DISTINCT AppUserId) AS UserCount
            FROM bee_SpendBeeMerchantPhoto
            WHERE ProjectId = @ProjectId
                AND Status <> 'Deleted'
                AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo');
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SpendBeePhotoSummary();
        }

        return new SpendBeePhotoSummary(
            Convert.ToInt32(reader["TotalCount"]),
            Convert.ToInt32(reader["ReadyCount"]),
            Convert.ToInt32(reader["FailedCount"]),
            Convert.ToInt32(reader["ProcessingCount"]),
            Convert.ToInt32(reader["MerchantCount"]),
            Convert.ToInt32(reader["UserCount"]));
    }

    private static async Task<IReadOnlyList<SpendBeePhotoDuplicateGroup>> LoadDuplicateGroupsAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DuplicateType, DuplicateKey, COUNT(*) AS PhotoCount, GROUP_CONCAT(id ORDER BY id SEPARATOR ',') AS PhotoIds
            FROM (
                SELECT id, 'Original URL' AS DuplicateType, OriginalImageUrl AS DuplicateKey
                FROM bee_SpendBeeMerchantPhoto
                WHERE ProjectId = @ProjectId
                    AND Status <> 'Deleted'
                    AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
                    AND OriginalImageUrl IS NOT NULL
                UNION ALL
                SELECT id, 'Display URL' AS DuplicateType, DisplayImageUrl AS DuplicateKey
                FROM bee_SpendBeeMerchantPhoto
                WHERE ProjectId = @ProjectId
                    AND Status <> 'Deleted'
                    AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
                    AND DisplayImageUrl IS NOT NULL
                UNION ALL
                SELECT id, 'Same user, merchant, category, minute' AS DuplicateType,
                    CONCAT(AppUserId, ':', MerchantId, ':', COALESCE(Category, 'group'), ':', DATE_FORMAT(CreatedAtUtc, '%Y-%m-%d %H:%i')) AS DuplicateKey
                FROM bee_SpendBeeMerchantPhoto
                WHERE ProjectId = @ProjectId
                    AND Status <> 'Deleted'
                    AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
            ) AS candidates
            GROUP BY DuplicateType, DuplicateKey
            HAVING PhotoCount > 1
            ORDER BY PhotoCount DESC, DuplicateType
            LIMIT 20;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<SpendBeePhotoDuplicateGroup>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new SpendBeePhotoDuplicateGroup(
                reader["DuplicateType"] as string ?? string.Empty,
                reader["DuplicateKey"] as string ?? string.Empty,
                Convert.ToInt32(reader["PhotoCount"]),
                reader["PhotoIds"] as string ?? string.Empty));
        }

        return groups;
    }

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddFilterParameters(
        MySqlCommand command,
        int projectId,
        string? status,
        long? merchantId,
        int? appUserId,
        string? category)
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = (object?)merchantId ?? DBNull.Value;
        command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = (object?)appUserId ?? DBNull.Value;
        command.Parameters.Add("@Category", MySqlDbType.VarChar, 80).Value = (object?)category ?? DBNull.Value;
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

public sealed record SpendBeePhotoSummary(
    int TotalCount = 0,
    int ReadyCount = 0,
    int FailedCount = 0,
    int ProcessingCount = 0,
    int MerchantCount = 0,
    int UserCount = 0);

public sealed record SpendBeePhotoDuplicateGroup(
    string DuplicateType,
    string DuplicateKey,
    int PhotoCount,
    string PhotoIds);

public sealed record SpendBeePhotoRow(
    long Id,
    long MerchantId,
    string MerchantName,
    string? MerchantAddress,
    int AppUserId,
    string DisplayName,
    string? Email,
    string? Gender,
    long UploadId,
    string? Category,
    string? Caption,
    string? OriginalImageUrl,
    string? DisplayImageUrl,
    string Status,
    string? ProcessingError,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    int LikeCount,
    int CommentCount);
