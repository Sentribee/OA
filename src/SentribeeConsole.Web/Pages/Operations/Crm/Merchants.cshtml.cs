using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using QRCoder;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.Crm;

[Authorize]
public class MerchantsModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<CrmMerchantRow> Merchants { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "Sentribee OA Merchants";
        ViewData["PageTitle"] = "Sentribee OA Merchants";
        ViewData["ActiveMenu"] = "CrmMerchants";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        const int pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_CrmMerchant WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT merchant.id, merchant.BusinessName, merchant.CorpId, merchant.ContactName,
                merchant.Email, merchant.WebsiteUrl, merchant.Status, merchant.PlanName,
                merchant.TimeZoneId, merchant.RegisteredAtUtc, merchant.LastLoginAtUtc,
                industry.Name AS IndustryName,
                (SELECT COUNT(*) FROM bee_CrmChatbot AS bot WHERE bot.MerchantId = merchant.id) AS ChatbotCount,
                (SELECT COUNT(*) FROM bee_CrmKnowledgeDocument AS doc WHERE doc.MerchantId = merchant.id) AS KnowledgeDocumentCount,
                (SELECT COUNT(*) FROM bee_CrmConversation AS conversation WHERE conversation.MerchantId = merchant.id) AS ConversationCount,
                (SELECT MAX(conversation.LastMessageAtUtc) FROM bee_CrmConversation AS conversation WHERE conversation.MerchantId = merchant.id) AS LastMessageAtUtc,
                (SELECT COALESCE(SUM(usageRow.PromptTokens + usageRow.CompletionTokens), 0) FROM bee_CrmUsageDaily AS usageRow WHERE usageRow.MerchantId = merchant.id) AS TotalTokens,
                (SELECT COALESCE(SUM(usageRow.MessageCount), 0) FROM bee_CrmUsageDaily AS usageRow WHERE usageRow.MerchantId = merchant.id) AS TotalMessages,
                (SELECT COALESCE(SUM(usageRow.EstimatedCostUsd), 0) FROM bee_CrmUsageDaily AS usageRow WHERE usageRow.MerchantId = merchant.id) AS EstimatedCostUsd
            FROM bee_CrmMerchant AS merchant
            LEFT JOIN bee_CrmIndustry AS industry ON industry.id = merchant.IndustryId
            WHERE merchant.ProjectId = @ProjectId
            ORDER BY merchant.RegisteredAtUtc DESC, merchant.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<CrmMerchantRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmMerchantRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["BusinessName"] as string ?? string.Empty,
                reader["CorpId"] as string ?? string.Empty,
                reader["IndustryName"] as string,
                reader["ContactName"] as string,
                reader["Email"] as string ?? string.Empty,
                reader["WebsiteUrl"] as string,
                reader["Status"] as string ?? string.Empty,
                reader["PlanName"] as string ?? string.Empty,
                reader["TimeZoneId"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("RegisteredAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastLoginAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastLoginAtUtc")),
                Convert.ToInt32(reader["ChatbotCount"]),
                Convert.ToInt32(reader["KnowledgeDocumentCount"]),
                Convert.ToInt32(reader["ConversationCount"]),
                reader.IsDBNull(reader.GetOrdinal("LastMessageAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastMessageAtUtc")),
                Convert.ToInt64(reader["TotalTokens"]),
                Convert.ToInt64(reader["TotalMessages"]),
                reader.GetDecimal(reader.GetOrdinal("EstimatedCostUsd"))));
        }

        Merchants = new PagedResult<CrmMerchantRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<IActionResult> OnGetChatQrAsync(long merchantId, bool download = false, CancellationToken cancellationToken = default)
    {
        Project = await LoadCurrentProjectAsync(cancellationToken);
        const string sql = """
            SELECT COALESCE(
                (SELECT bot.PublicChatPath
                 FROM bee_CrmChatbot AS bot
                 WHERE bot.MerchantId = merchant.id
                 ORDER BY bot.Status = 'Active' DESC, bot.id
                 LIMIT 1),
                merchant.CorpId
            ) AS PublicChatPath
            FROM bee_CrmMerchant AS merchant
            WHERE merchant.ProjectId = @ProjectId
              AND merchant.id = @MerchantId
            LIMIT 1;
            """;
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        var publicChatPath = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(publicChatPath))
        {
            return NotFound();
        }

        var normalized = publicChatPath.Trim().Trim('/');
        var chatUrl = $"https://chat.sentribee.ai/{normalized}";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(chatUrl, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        var png = qrCode.GetGraphic(12);
        Response.Headers.CacheControl = "no-store";
        return download
            ? File(png, "image/png", $"sentribee-chat-{normalized}.png")
            : File(png, "image/png");
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

public sealed record CrmMerchantRow(
    long Id,
    string BusinessName,
    string CorpId,
    string? IndustryName,
    string? ContactName,
    string Email,
    string? WebsiteUrl,
    string Status,
    string PlanName,
    string TimeZoneId,
    DateTime RegisteredAtUtc,
    DateTime? LastLoginAtUtc,
    int ChatbotCount,
    int KnowledgeDocumentCount,
    int ConversationCount,
    DateTime? LastMessageAtUtc,
    long TotalTokens,
    long TotalMessages,
    decimal EstimatedCostUsd);
