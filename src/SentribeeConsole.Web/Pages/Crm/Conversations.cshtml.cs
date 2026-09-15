using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Domain.Entities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentribeeConsole.Web.Pages.Crm;

public class ConversationsModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public PagedResult<CrmConversationRow> Conversations { get; private set; } = new();

    public IReadOnlyList<CrmConversationMessageRow> Messages { get; private set; } = [];

    public long? SelectedConversationId { get; private set; }
    public string? ConversationLabel { get; private set; }
    public bool IsWeComConversation { get; private set; }
    public int MessagePage { get; private set; }
    public bool HasMoreMessages { get; private set; }

    public async Task<IActionResult> OnGetAsync(long? conversationId, int pageNumber = 1, int messagePage = 1, CancellationToken cancellationToken = default)
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
        MessagePage = Math.Clamp(messagePage, 1, 100000);

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
        await using (var selected = new MySqlCommand("SELECT VisitorLabel, Channel FROM bee_CrmConversation WHERE id=@id AND MerchantId=@merchant", connection))
        {
            selected.Parameters.AddWithValue("@id", conversationId);
            selected.Parameters.AddWithValue("@merchant", Merchant.Id);
            await using var selection = await selected.ExecuteReaderAsync(cancellationToken);
            if (!await selection.ReadAsync(cancellationToken)) return;
            ConversationLabel = selection["VisitorLabel"] as string;
            IsWeComConversation = selection.GetString("Channel") is "WeCom" or "WeChatKf";
        }
        const string sql = """
            SELECT message.id, message.SenderRole, message.Body, message.ImageUrl,
                message.ModelName, message.PromptTokens, message.CompletionTokens, message.CreatedAtUtc,
                wecom.MessageType, wecom.UserId, wecom.SenderName, wecom.SenderAvatarUrl,
                wecom.ChatName, wecom.ChatId, wecom.NormalizedJson
            FROM bee_CrmConversationMessage AS message
            INNER JOIN bee_CrmConversation AS conversation ON conversation.id = message.ConversationId
            LEFT JOIN bee_WeComAibotMessage AS wecom ON wecom.CrmMessageId=message.id AND wecom.MerchantId=conversation.MerchantId
            WHERE message.ConversationId = @ConversationId
              AND conversation.MerchantId = @MerchantId
            ORDER BY message.CreatedAtUtc DESC, message.id DESC
            LIMIT 101 OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.AddWithValue("@Offset", (MessagePage - 1) * 100);
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
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader["MessageType"] is string type ? new WeComConversationInfo(type,
                    reader["UserId"] as string ?? "", reader["SenderName"] as string,
                    SafeAvatar(reader["SenderAvatarUrl"] as string), reader["ChatName"] as string,
                    reader["ChatId"] as string ?? "", SafeDetails(reader["NormalizedJson"] as string)) : null));
        }

        HasMoreMessages = rows.Count > 100;
        Messages = rows.Take(100).Reverse().ToList();
    }

    public async Task<IActionResult> OnPostLabelAsync(long conversationId, string? label, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null) return RedirectToPage("/Crm/Login");
        label = label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 140) return BadRequest();
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("UPDATE bee_CrmConversation SET VisitorLabel=@label WHERE id=@id AND MerchantId=@merchant AND Channel IN ('WeCom','WeChatKf')", connection);
        command.Parameters.AddWithValue("@label", label);
        command.Parameters.AddWithValue("@id", conversationId);
        command.Parameters.AddWithValue("@merchant", merchant.Id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return NotFound();
        return RedirectToPage(new { conversationId });
    }

    private static string? SafeAvatar(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? value : null;
    private static string SafeDetails(string? value)
    {
        var data = JsonNode.Parse(value ?? "{}");
        // Callback signatures and temporary reply URLs are not needed in the OA view.
        var details = new JsonObject { ["attachments"] = data?["attachments"]?.DeepClone(), ["quote"] = data?["quote"]?.DeepClone(), ["event"] = data?["rawMessage"]?["event"]?.DeepClone(), ["msgid"] = data?["msgid"]?.DeepClone() };
        if (data?["source"]?.GetValue<string>() == "wecom_kf")
        {
            var type=data["msgtype"]?.GetValue<string>() ?? "unknown";
            details["source"]=JsonValue.Create("wecom_kf");
            details["content"]=data["rawMessage"]?[type]?.DeepClone();
            // Event response codes are capabilities, not display fields.
            foreach (var item in new[] { details["event"],details["content"] }.OfType<JsonObject>())
            { item.Remove("welcome_code"); item.Remove("msg_code"); }
        }
        return details.ToJsonString(new JsonSerializerOptions { WriteIndented=true });
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
    DateTime CreatedAtUtc,
    WeComConversationInfo? WeCom = null);

public sealed record WeComConversationInfo(string MessageType, string UserId, string? Name, string? AvatarUrl, string? ChatName, string ChatId, string Details);
