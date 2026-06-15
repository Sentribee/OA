using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.SpendBee;

[Authorize]
public class ReceiptDetailsModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public SpendBeeReceiptDetail? Receipt { get; private set; }

    public IReadOnlyList<SpendBeeReceiptDetailImage> Images { get; private set; } = [];

    public IReadOnlyList<SpendBeeReceiptDetailLineItem> LineItems { get; private set; } = [];

    public IReadOnlyList<string> FailedChecks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = $"SpendBee Receipt #{id}";
        ViewData["PageTitle"] = $"Receipt #{id}";
        ViewData["ActiveMenu"] = "SpendBeeReceipts";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string receiptSql = """
            SELECT receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
                receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
                receipt.PurchasedAtUtc, receipt.OrderedAtUtc, receipt.PickupAtUtc, receipt.DeliveredAtUtc,
                receipt.Currency, receipt.Subtotal, receipt.Tax, receipt.DeliveryFee,
                receipt.ServiceFee, receipt.PlatformDiscount, receipt.Total,
                receipt.OverallConfidence, receipt.EstimatedErrorRate, receipt.FailedChecksJson,
                receipt.ReceiptImageSetHash, receipt.ReceiptCanonicalHash, receipt.CreatedAtUtc, receipt.UpdatedAtUtc,
                user.DisplayName, user.Email,
                merchant.id AS MerchantId, merchant.Name AS MerchantDisplayName, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
                platform.id AS PlatformId, platform.Name AS PlatformName, platform.DisplayName AS PlatformDisplayName,
                platform.PlatformType, platform.WebsiteUrl AS PlatformWebsiteUrl
            FROM bee_SpendBeeReceipt AS receipt
            INNER JOIN bee_AppUser AS user ON user.id = receipt.AppUserId
            LEFT JOIN bee_SpendBeeMerchant AS merchant ON merchant.id = receipt.MerchantId
            LEFT JOIN bee_SpendBeePlatform AS platform ON platform.id = receipt.PlatformId
            WHERE receipt.id = @ReceiptId
                AND receipt.ProjectId = @ProjectId
            LIMIT 1;
            """;
        await using var receiptCommand = new MySqlCommand(receiptSql, connection);
        receiptCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = id;
        receiptCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        await using var reader = await receiptCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return NotFound();
        }

        Receipt = new SpendBeeReceiptDetail(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["DisplayName"] as string ?? string.Empty,
            reader["Email"] as string,
            reader["Status"] as string ?? string.Empty,
            reader["ReceiptType"] as string,
            reader["FulfillmentType"] as string,
            reader["MerchantName"] as string,
            reader["MerchantAddress"] as string,
            reader.IsDBNull(reader.GetOrdinal("MerchantId")) ? null : reader.GetInt64(reader.GetOrdinal("MerchantId")),
            reader["MerchantDisplayName"] as string,
            !reader.IsDBNull(reader.GetOrdinal("AiCoverImageUrl")) || !reader.IsDBNull(reader.GetOrdinal("GooglePhotoUri")),
            reader.IsDBNull(reader.GetOrdinal("PlatformId")) ? null : reader.GetInt64(reader.GetOrdinal("PlatformId")),
            reader["PlatformName"] as string,
            reader["PlatformDisplayName"] as string,
            reader["PlatformType"] as string,
            reader["PlatformWebsiteUrl"] as string,
            reader["PlatformOrderNumber"] as string,
            reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("OrderedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("OrderedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("PickupAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PickupAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("DeliveredAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("DeliveredAtUtc")),
            reader["Currency"] as string,
            reader.IsDBNull(reader.GetOrdinal("Subtotal")) ? null : reader.GetDecimal(reader.GetOrdinal("Subtotal")),
            reader.IsDBNull(reader.GetOrdinal("Tax")) ? null : reader.GetDecimal(reader.GetOrdinal("Tax")),
            reader.IsDBNull(reader.GetOrdinal("DeliveryFee")) ? null : reader.GetDecimal(reader.GetOrdinal("DeliveryFee")),
            reader.IsDBNull(reader.GetOrdinal("ServiceFee")) ? null : reader.GetDecimal(reader.GetOrdinal("ServiceFee")),
            reader.IsDBNull(reader.GetOrdinal("PlatformDiscount")) ? null : reader.GetDecimal(reader.GetOrdinal("PlatformDiscount")),
            reader.IsDBNull(reader.GetOrdinal("Total")) ? null : reader.GetDecimal(reader.GetOrdinal("Total")),
            reader.IsDBNull(reader.GetOrdinal("OverallConfidence")) ? null : reader.GetDecimal(reader.GetOrdinal("OverallConfidence")),
            reader.IsDBNull(reader.GetOrdinal("EstimatedErrorRate")) ? null : reader.GetDecimal(reader.GetOrdinal("EstimatedErrorRate")),
            reader["ReceiptImageSetHash"] as string,
            reader["ReceiptCanonicalHash"] as string,
            reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")));

        FailedChecks = ParseFailedChecks(reader["FailedChecksJson"] as string);
        await reader.CloseAsync();

        Images = await LoadImagesAsync(connection, id, cancellationToken);
        LineItems = await LoadLineItemsAsync(connection, id, cancellationToken);
        return Page();
    }

    private static async Task<IReadOnlyList<SpendBeeReceiptDetailImage>> LoadImagesAsync(
        MySqlConnection connection,
        long receiptId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ContentType, SortOrder, CreatedAtUtc
            FROM bee_SpendBeeReceiptImage
            WHERE ReceiptId = @ReceiptId
            ORDER BY SortOrder, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeReceiptDetailImage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeReceiptDetailImage(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["ContentType"] as string ?? string.Empty,
                reader.GetInt32(reader.GetOrdinal("SortOrder")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SpendBeeReceiptDetailLineItem>> LoadLineItemsAsync(
        MySqlConnection connection,
        long receiptId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ItemName, Quantity, UnitPrice, Amount, Category, Confidence, SortOrder
            FROM bee_SpendBeeReceiptLineItem
            WHERE ReceiptId = @ReceiptId
            ORDER BY SortOrder, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SpendBeeReceiptDetailLineItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SpendBeeReceiptDetailLineItem(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["ItemName"] as string ?? string.Empty,
                reader.IsDBNull(reader.GetOrdinal("Quantity")) ? null : reader.GetDecimal(reader.GetOrdinal("Quantity")),
                reader.IsDBNull(reader.GetOrdinal("UnitPrice")) ? null : reader.GetDecimal(reader.GetOrdinal("UnitPrice")),
                reader.IsDBNull(reader.GetOrdinal("Amount")) ? null : reader.GetDecimal(reader.GetOrdinal("Amount")),
                reader["Category"] as string,
                reader.IsDBNull(reader.GetOrdinal("Confidence")) ? null : reader.GetDecimal(reader.GetOrdinal("Confidence")),
                reader.GetInt32(reader.GetOrdinal("SortOrder"))));
        }

        return rows;
    }

    private static IReadOnlyList<string> ParseFailedChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [json];
        }
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

public sealed record SpendBeeReceiptDetail(
    long Id,
    string DisplayName,
    string? Email,
    string Status,
    string? ReceiptType,
    string? FulfillmentType,
    string? MerchantName,
    string? MerchantAddress,
    long? MerchantId,
    string? MerchantDisplayName,
    bool HasMerchantCover,
    long? PlatformId,
    string? PlatformName,
    string? PlatformDisplayName,
    string? PlatformType,
    string? PlatformWebsiteUrl,
    string? PlatformOrderNumber,
    DateTime? PurchasedAtUtc,
    DateTime? OrderedAtUtc,
    DateTime? PickupAtUtc,
    DateTime? DeliveredAtUtc,
    string? Currency,
    decimal? Subtotal,
    decimal? Tax,
    decimal? DeliveryFee,
    decimal? ServiceFee,
    decimal? PlatformDiscount,
    decimal? Total,
    decimal? OverallConfidence,
    decimal? EstimatedErrorRate,
    string? ReceiptImageSetHash,
    string? ReceiptCanonicalHash,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SpendBeeReceiptDetailImage(
    long Id,
    string ContentType,
    int SortOrder,
    DateTime CreatedAtUtc);

public sealed record SpendBeeReceiptDetailLineItem(
    long Id,
    string Name,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    string? Category,
    decimal? Confidence,
    int SortOrder);
