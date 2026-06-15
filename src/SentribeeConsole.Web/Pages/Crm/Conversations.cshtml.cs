using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Crm;

public class ConversationsModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public PagedResult<CrmConversationRow> Conversations { get; private set; } = new();

    public IReadOnlyList<CrmConversationMessageRow> Messages { get; private set; } = [];

    public long? SelectedConversationId { get; private set; }

    public async Task<IActionResult> OnGetAsync(long? conversationId, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Conversations";
        ViewData["PageTitle"] = "Conversations";
        ViewData["ActiveMenu"] = "Conversations";
        SelectedConversationId = conversationId;

        await LoadConversationsAsync(pageNumber, cancellationToken);
        if (conversationId.HasValue)
        {
            await LoadMessagesAsync(conversationId.Value, cancellationToken);
        }

        return Page();
    }

    private async Task LoadConversationsAsync(int pageNumber, CancellationToken cancellationToken)
    {
        const int pageSize = 15;
        pageNumber = Math.Max(1, pageNumber);
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_CrmConversation WHERE MerchantId = @MerchantId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT conversation.id, conversation.VisitorLabel, conversation.Channel, conversation.Status,
                conversation.MessageCount, conversation.ImageMessageCount, conversation.StartedAtUtc,
                conversation.LastMessageAtUtc, bot.BotName
            FROM bee_CrmConversation AS conversation
            LEFT JOIN bee_CrmChatbot AS bot ON bot.id = conversation.ChatbotId
            WHERE conversation.MerchantId = @MerchantId
            ORDER BY COALESCE(conversation.LastMessageAtUtc, conversation.StartedAtUtc) DESC, conversation.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmConversationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmConversationRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["VisitorLabel"] as string,
                reader["Channel"] as string ?? string.Empty,
                reader["Status"] as string ?? string.Empty,
                reader.GetInt32(reader.GetOrdinal("MessageCount")),
                reader.GetInt32(reader.GetOrdinal("ImageMessageCount")),
                reader.GetDateTime(reader.GetOrdinal("StartedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastMessageAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastMessageAtUtc")),
                reader["BotName"] as string));
        }

        Conversations = new PagedResult<CrmConversationRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private async Task LoadMessagesAsync(long conversationId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT message.id, message.SenderRole, message.Body, message.ImageUrl,
                message.ModelName, message.PromptTokens, message.CompletionTokens, message.CreatedAtUtc
            FROM bee_CrmConversationMessage AS message
            INNER JOIN bee_CrmConversation AS conversation ON conversation.id = message.ConversationId
            WHERE message.ConversationId = @ConversationId
              AND conversation.MerchantId = @MerchantId
            ORDER BY message.CreatedAtUtc, message.id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmConversationMessageRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmConversationMessageRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["SenderRole"] as string ?? string.Empty,
                reader["Body"] as string,
                reader["ImageUrl"] as string,
                reader["ModelName"] as string,
                reader.GetInt32(reader.GetOrdinal("PromptTokens")),
                reader.GetInt32(reader.GetOrdinal("CompletionTokens")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        Messages = rows;
    }
}

public sealed record CrmConversationRow(
    long Id,
    string? VisitorLabel,
    string Channel,
    string Status,
    int MessageCount,
    int ImageMessageCount,
    DateTime StartedAtUtc,
    DateTime? LastMessageAtUtc,
    string? BotName);

public sealed record CrmConversationMessageRow(
    long Id,
    string SenderRole,
    string? Body,
    string? ImageUrl,
    string? ModelName,
    int PromptTokens,
    int CompletionTokens,
    DateTime CreatedAtUtc);
