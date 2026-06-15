using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

[Authorize]
public class MerchantsModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<SpendBeeMerchantRow> Merchants { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "SpendBee Merchants";
        ViewData["PageTitle"] = "SpendBee Merchants";
        ViewData["ActiveMenu"] = "SpendBeeMerchants";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        var pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_SpendBeeMerchant WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType, merchant.BusinessStatus,
                merchant.Rating, merchant.UserRatingCount, merchant.GooglePlaceId, merchant.GoogleMapsUri,
                merchant.GooglePhotoUri, merchant.AiCoverImageUrl, merchant.SyncStatus,
                merchant.LastGoogleSyncAtUtc, merchant.LastAiCoverGeneratedAtUtc, merchant.UpdatedAtUtc,
                CASE
                    WHEN COALESCE(NULLIF(merchant.AiCoverImageUrl, ''), NULLIF(merchant.GooglePhotoUri, '')) IS NOT NULL THEN 1
                    WHEN EXISTS (
                        SELECT 1
                        FROM bee_SpendBeeMerchantPhoto AS fallbackPhoto
                        WHERE fallbackPhoto.ProjectId = merchant.ProjectId
                            AND fallbackPhoto.MerchantId = merchant.id
                            AND fallbackPhoto.Status = 'Ready'
                            AND fallbackPhoto.DisplayImageUrl IS NOT NULL
                            AND fallbackPhoto.DisplayImageUrl <> ''
                            AND LOWER(COALESCE(fallbackPhoto.Category, 'group')) = 'group'
                        LIMIT 1
                    ) THEN 1
                    ELSE 0
                END AS HasCover,
                COUNT(receipt.id) AS ReceiptCount,
                MAX(COALESCE(receipt.PurchasedAtUtc, receipt.CreatedAtUtc)) AS LastReceiptAtUtc,
                SUM(COALESCE(receipt.Total, 0)) AS TotalSpent
            FROM bee_SpendBeeMerchant AS merchant
            LEFT JOIN bee_SpendBeeReceipt AS receipt ON receipt.MerchantId = merchant.id
            WHERE merchant.ProjectId = @ProjectId
            GROUP BY merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType, merchant.BusinessStatus,
                merchant.Rating, merchant.UserRatingCount, merchant.GooglePlaceId, merchant.GoogleMapsUri,
                merchant.GooglePhotoUri, merchant.AiCoverImageUrl, merchant.SyncStatus,
                merchant.LastGoogleSyncAtUtc, merchant.LastAiCoverGeneratedAtUtc, merchant.UpdatedAtUtc
            ORDER BY LastReceiptAtUtc DESC, merchant.UpdatedAtUtc DESC, merchant.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeMerchantRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeMerchantRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["Name"] as string ?? string.Empty,
                reader["Address"] as string,
                reader["PrimaryType"] as string,
                reader["BusinessStatus"] as string,
                reader.IsDBNull(reader.GetOrdinal("Rating")) ? null : reader.GetDecimal(reader.GetOrdinal("Rating")),
                reader.IsDBNull(reader.GetOrdinal("UserRatingCount")) ? null : reader.GetInt32(reader.GetOrdinal("UserRatingCount")),
                reader["GooglePlaceId"] as string,
                reader["GoogleMapsUri"] as string,
                reader["GooglePhotoUri"] as string,
                reader["AiCoverImageUrl"] as string,
                Convert.ToInt32(reader["HasCover"]) == 1,
                reader["SyncStatus"] as string ?? string.Empty,
                reader.IsDBNull(reader.GetOrdinal("LastGoogleSyncAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastGoogleSyncAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastAiCoverGeneratedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastAiCoverGeneratedAtUtc")),
                Convert.ToInt32(reader["ReceiptCount"]),
                reader.IsDBNull(reader.GetOrdinal("LastReceiptAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastReceiptAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("TotalSpent")) ? 0m : reader.GetDecimal(reader.GetOrdinal("TotalSpent"))));
        }

        Merchants = new PagedResult<SpendBeeMerchantRow>
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

public sealed record SpendBeeMerchantRow(
    long Id,
    string Name,
    string? Address,
    string? PrimaryType,
    string? BusinessStatus,
    decimal? Rating,
    int? UserRatingCount,
    string? GooglePlaceId,
    string? GoogleMapsUri,
    string? GooglePhotoUri,
    string? AiCoverImageUrl,
    bool HasCover,
    string SyncStatus,
    DateTime? LastGoogleSyncAtUtc,
    DateTime? LastAiCoverGeneratedAtUtc,
    int ReceiptCount,
    DateTime? LastReceiptAtUtc,
    decimal TotalSpent);
