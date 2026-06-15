using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

public class ReceiptsModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<SpendBeeReceiptRow> Receipts { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "SpendBee Receipts";
        ViewData["PageTitle"] = "SpendBee Receipts";
        ViewData["ActiveMenu"] = "SpendBeeReceipts";
        Project = await LoadCurrentProjectAsync(cancellationToken);
        var pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_SpendBeeReceipt WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
                receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
                receipt.PurchasedAtUtc, receipt.Currency, receipt.Subtotal, receipt.Tax,
                receipt.DeliveryFee, receipt.ServiceFee, receipt.PlatformDiscount, receipt.Total,
                receipt.OverallConfidence, receipt.EstimatedErrorRate, receipt.CreatedAtUtc,
                user.DisplayName, user.Email,
                platform.Name AS PlatformName, platform.DisplayName AS PlatformDisplayName, platform.PlatformType,
                COUNT(DISTINCT image.id) AS ImageCount,
                MIN(image.id) AS FirstImageId,
                COUNT(DISTINCT line.id) AS LineItemCount
            FROM bee_SpendBeeReceipt AS receipt
            INNER JOIN bee_AppUser AS user ON user.id = receipt.AppUserId
            LEFT JOIN bee_SpendBeePlatform AS platform ON platform.id = receipt.PlatformId
            LEFT JOIN bee_SpendBeeReceiptImage AS image ON image.ReceiptId = receipt.id
            LEFT JOIN bee_SpendBeeReceiptLineItem AS line ON line.ReceiptId = receipt.id
            WHERE receipt.ProjectId = @ProjectId
            GROUP BY receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
                receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
                receipt.PurchasedAtUtc, receipt.Currency, receipt.Subtotal, receipt.Tax,
                receipt.DeliveryFee, receipt.ServiceFee, receipt.PlatformDiscount, receipt.Total,
                receipt.OverallConfidence, receipt.EstimatedErrorRate, receipt.CreatedAtUtc,
                user.DisplayName, user.Email, platform.Name, platform.DisplayName, platform.PlatformType
            ORDER BY receipt.CreatedAtUtc DESC, receipt.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeReceiptRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeReceiptRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["DisplayName"] as string ?? string.Empty,
                reader["Email"] as string,
                reader["Status"] as string ?? string.Empty,
                reader["ReceiptType"] as string,
                reader["FulfillmentType"] as string,
                reader["MerchantName"] as string,
                reader["MerchantAddress"] as string,
                reader["PlatformName"] as string,
                reader["PlatformDisplayName"] as string,
                reader["PlatformType"] as string,
                reader["PlatformOrderNumber"] as string,
                reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")),
                reader["Currency"] as string,
                reader.IsDBNull(reader.GetOrdinal("Subtotal")) ? null : reader.GetDecimal(reader.GetOrdinal("Subtotal")),
                reader.IsDBNull(reader.GetOrdinal("Tax")) ? null : reader.GetDecimal(reader.GetOrdinal("Tax")),
                reader.IsDBNull(reader.GetOrdinal("DeliveryFee")) ? null : reader.GetDecimal(reader.GetOrdinal("DeliveryFee")),
                reader.IsDBNull(reader.GetOrdinal("ServiceFee")) ? null : reader.GetDecimal(reader.GetOrdinal("ServiceFee")),
                reader.IsDBNull(reader.GetOrdinal("PlatformDiscount")) ? null : reader.GetDecimal(reader.GetOrdinal("PlatformDiscount")),
                reader.IsDBNull(reader.GetOrdinal("Total")) ? null : reader.GetDecimal(reader.GetOrdinal("Total")),
                reader.IsDBNull(reader.GetOrdinal("OverallConfidence")) ? null : reader.GetDecimal(reader.GetOrdinal("OverallConfidence")),
                reader.IsDBNull(reader.GetOrdinal("EstimatedErrorRate")) ? null : reader.GetDecimal(reader.GetOrdinal("EstimatedErrorRate")),
                Convert.ToInt32(reader["ImageCount"]),
                reader.IsDBNull(reader.GetOrdinal("FirstImageId")) ? null : reader.GetInt64(reader.GetOrdinal("FirstImageId")),
                Convert.ToInt32(reader["LineItemCount"]),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        Receipts = new PagedResult<SpendBeeReceiptRow>
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

public sealed record SpendBeeReceiptRow(
    long Id,
    string DisplayName,
    string? Email,
    string Status,
    string? ReceiptType,
    string? FulfillmentType,
    string? MerchantName,
    string? MerchantAddress,
    string? PlatformName,
    string? PlatformDisplayName,
    string? PlatformType,
    string? PlatformOrderNumber,
    DateTime? PurchasedAtUtc,
    string? Currency,
    decimal? Subtotal,
    decimal? Tax,
    decimal? DeliveryFee,
    decimal? ServiceFee,
    decimal? PlatformDiscount,
    decimal? Total,
    decimal? OverallConfidence,
    decimal? EstimatedErrorRate,
    int ImageCount,
    long? FirstImageId,
    int LineItemCount,
    DateTime CreatedAtUtc);
