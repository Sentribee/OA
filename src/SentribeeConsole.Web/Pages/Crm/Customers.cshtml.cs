using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Crm;

public class CustomersModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public PagedResult<CrmCustomerDirectoryRow> Customers { get; private set; } = new();

    public CrmCustomerDirectoryDetail? SelectedCustomer { get; private set; }

    public IReadOnlyList<CrmCustomerConversationRow> RelatedConversations { get; private set; } = [];

    public IReadOnlyList<CrmCustomerMessageRow> Messages { get; private set; } = [];

    public long? SelectedProfileId { get; private set; }

    public long? SelectedConversationId { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long? profileId,
        long? conversationId,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Customers";
        ViewData["PageTitle"] = "Customers";
        ViewData["ActiveMenu"] = "Customers";

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await LoadCustomersAsync(connection, pageNumber, cancellationToken);

        var first = Customers.Items.FirstOrDefault();
        SelectedProfileId = profileId ?? first?.ProfileId;
        SelectedConversationId = conversationId ?? first?.ConversationId;
        if (SelectedProfileId.HasValue || SelectedConversationId.HasValue)
        {
            SelectedCustomer = await LoadCustomerDetailAsync(connection, SelectedProfileId, SelectedConversationId, cancellationToken);
            if (SelectedCustomer is not null)
            {
                SelectedConversationId ??= SelectedCustomer.ConversationId;
                RelatedConversations = await LoadRelatedConversationsAsync(connection, SelectedCustomer, cancellationToken);
                SelectedConversationId = RelatedConversations.Any(item => item.Id == SelectedConversationId)
                    ? SelectedConversationId
                    : RelatedConversations.FirstOrDefault()?.Id ?? SelectedConversationId;
                if (SelectedConversationId.HasValue)
                {
                    Messages = await LoadMessagesAsync(connection, SelectedConversationId.Value, cancellationToken);
                }
            }
        }

        return Page();
    }

    public string BuildContinueChatUrl(CrmCustomerDirectoryDetail customer)
    {
        if (!customer.ConversationId.HasValue || string.IsNullOrWhiteSpace(customer.PublicChatPath))
        {
            return BuildChatUrl(Merchant.CorpId);
        }

        var path = CrmChatResumeLink.NormalizePublicChatPath(customer.PublicChatPath);
        var token = CrmChatResumeLink.CreateToken(ConnectionString, path, customer.ConversationId.Value);
        return $"{BuildChatUrl(path)}?conversationId={customer.ConversationId.Value}&resume={Uri.EscapeDataString(token)}";
    }

    private async Task LoadCustomersAsync(MySqlConnection connection, int pageNumber, CancellationToken cancellationToken)
    {
        const int pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);
        const string countSql = """
            SELECT COUNT(*)
            FROM (
                SELECT profile.id
                FROM bee_CrmCustomerProfile AS profile
                WHERE profile.MerchantId = @MerchantId
                UNION ALL
                SELECT conversation.id
                FROM bee_CrmConversation AS conversation
                LEFT JOIN bee_CrmCustomerProfile AS profile
                  ON profile.ConversationId = conversation.id
                  OR (conversation.VisitorKey IS NOT NULL AND profile.VisitorKey = conversation.VisitorKey)
                WHERE conversation.MerchantId = @MerchantId
                  AND profile.id IS NULL
            ) AS customer_count;
            """;
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT *
            FROM (
                SELECT
                    profile.id AS ProfileId,
                    profile.ConversationId,
                    profile.VisitorKey,
                    profile.VisitorIp,
                    profile.UserAgent,
                    profile.VisitorLabel,
                    profile.DisplayName,
                    profile.Email,
                    profile.Phone,
                    profile.CompanyName,
                    profile.IntentSummary,
                    profile.ProductInterest,
                    profile.LifecycleStage,
                    profile.Sentiment,
                    profile.PriorityScore,
                    profile.ProfileCompleteness,
                    profile.UpdatedAtUtc,
                    conversation.MessageCount,
                    conversation.ImageMessageCount,
                    conversation.StartedAtUtc,
                    conversation.LastMessageAtUtc,
                    bot.BotName,
                    bot.PublicChatPath
                FROM bee_CrmCustomerProfile AS profile
                LEFT JOIN bee_CrmConversation AS conversation ON conversation.id = profile.ConversationId
                LEFT JOIN bee_CrmChatbot AS bot ON bot.id = profile.ChatbotId
                WHERE profile.MerchantId = @MerchantId
                UNION ALL
                SELECT
                    NULL AS ProfileId,
                    conversation.id AS ConversationId,
                    conversation.VisitorKey,
                    conversation.VisitorIp,
                    conversation.UserAgent,
                    conversation.VisitorLabel,
                    NULL AS DisplayName,
                    NULL AS Email,
                    NULL AS Phone,
                    NULL AS CompanyName,
                    NULL AS IntentSummary,
                    NULL AS ProductInterest,
                    NULL AS LifecycleStage,
                    NULL AS Sentiment,
                    0 AS PriorityScore,
                    0 AS ProfileCompleteness,
                    conversation.UpdatedAtUtc,
                    conversation.MessageCount,
                    conversation.ImageMessageCount,
                    conversation.StartedAtUtc,
                    conversation.LastMessageAtUtc,
                    bot.BotName,
                    bot.PublicChatPath
                FROM bee_CrmConversation AS conversation
                LEFT JOIN bee_CrmCustomerProfile AS profile
                  ON profile.ConversationId = conversation.id
                  OR (conversation.VisitorKey IS NOT NULL AND profile.VisitorKey = conversation.VisitorKey)
                LEFT JOIN bee_CrmChatbot AS bot ON bot.id = conversation.ChatbotId
                WHERE conversation.MerchantId = @MerchantId
                  AND profile.id IS NULL
            ) AS customer
            ORDER BY COALESCE(customer.LastMessageAtUtc, customer.UpdatedAtUtc, customer.StartedAtUtc) DESC, customer.ConversationId DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmCustomerDirectoryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmCustomerDirectoryRow(
                reader.IsDBNull(reader.GetOrdinal("ProfileId")) ? null : reader.GetInt64(reader.GetOrdinal("ProfileId")),
                reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetInt64(reader.GetOrdinal("ConversationId")),
                reader["VisitorKey"] as string,
                reader["VisitorIp"] as string,
                reader["UserAgent"] as string,
                reader["VisitorLabel"] as string,
                reader["DisplayName"] as string,
                reader["Email"] as string,
                reader["Phone"] as string,
                reader["CompanyName"] as string,
                reader["IntentSummary"] as string,
                reader["ProductInterest"] as string,
                reader["LifecycleStage"] as string,
                reader["Sentiment"] as string,
                Convert.ToInt32(reader["PriorityScore"]),
                Convert.ToInt32(reader["ProfileCompleteness"]),
                reader.IsDBNull(reader.GetOrdinal("MessageCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("MessageCount")),
                reader.IsDBNull(reader.GetOrdinal("ImageMessageCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("ImageMessageCount")),
                reader.IsDBNull(reader.GetOrdinal("LastMessageAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastMessageAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
                reader["BotName"] as string,
                reader["PublicChatPath"] as string));
        }

        Customers = new PagedResult<CrmCustomerDirectoryRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private async Task<CrmCustomerDirectoryDetail?> LoadCustomerDetailAsync(
        MySqlConnection connection,
        long? profileId,
        long? conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                profile.id AS ProfileId,
                COALESCE(profile.ConversationId, conversation.id) AS ConversationId,
                COALESCE(profile.VisitorKey, conversation.VisitorKey) AS VisitorKey,
                COALESCE(profile.VisitorIp, conversation.VisitorIp) AS VisitorIp,
                COALESCE(profile.UserAgent, conversation.UserAgent) AS UserAgent,
                COALESCE(profile.VisitorLabel, conversation.VisitorLabel) AS VisitorLabel,
                profile.DisplayName,
                profile.Email,
                profile.Phone,
                profile.CompanyName,
                profile.JobTitle,
                profile.Location,
                profile.Language,
                profile.CustomerType,
                profile.LifecycleStage,
                profile.IntentSummary,
                profile.NeedSummary,
                profile.ProductInterest,
                profile.IndustrySegment,
                profile.BudgetRange,
                profile.Timeline,
                profile.DecisionRole,
                profile.PainPoints,
                profile.Objections,
                profile.Preferences,
                profile.Sentiment,
                COALESCE(profile.PriorityScore, 0) AS PriorityScore,
                COALESCE(profile.ProfileCompleteness, 0) AS ProfileCompleteness,
                profile.ProfileJson,
                profile.LastExtractedAtUtc,
                COALESCE(profile.UpdatedAtUtc, conversation.UpdatedAtUtc) AS UpdatedAtUtc,
                conversation.MessageCount,
                conversation.ImageMessageCount,
                conversation.StartedAtUtc,
                conversation.LastMessageAtUtc,
                bot.BotName,
                bot.PublicChatPath
            FROM bee_CrmConversation AS conversation
            LEFT JOIN bee_CrmCustomerProfile AS profile
              ON profile.ConversationId = conversation.id
              OR (conversation.VisitorKey IS NOT NULL AND profile.VisitorKey = conversation.VisitorKey)
            LEFT JOIN bee_CrmChatbot AS bot ON bot.id = conversation.ChatbotId
            WHERE conversation.MerchantId = @MerchantId
              AND ((@ProfileId IS NOT NULL AND profile.id = @ProfileId)
                   OR (@ProfileId IS NULL AND conversation.id = @ConversationId))
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@ProfileId", MySqlDbType.Int64).Value = (object?)profileId ?? DBNull.Value;
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = (object?)conversationId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmCustomerDirectoryDetail(
            reader.IsDBNull(reader.GetOrdinal("ProfileId")) ? null : reader.GetInt64(reader.GetOrdinal("ProfileId")),
            reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetInt64(reader.GetOrdinal("ConversationId")),
            reader["VisitorKey"] as string,
            reader["VisitorIp"] as string,
            reader["UserAgent"] as string,
            reader["VisitorLabel"] as string,
            reader["DisplayName"] as string,
            reader["Email"] as string,
            reader["Phone"] as string,
            reader["CompanyName"] as string,
            reader["JobTitle"] as string,
            reader["Location"] as string,
            reader["Language"] as string,
            reader["CustomerType"] as string,
            reader["LifecycleStage"] as string,
            reader["IntentSummary"] as string,
            reader["NeedSummary"] as string,
            reader["ProductInterest"] as string,
            reader["IndustrySegment"] as string,
            reader["BudgetRange"] as string,
            reader["Timeline"] as string,
            reader["DecisionRole"] as string,
            reader["PainPoints"] as string,
            reader["Objections"] as string,
            reader["Preferences"] as string,
            reader["Sentiment"] as string,
            Convert.ToInt32(reader["PriorityScore"]),
            Convert.ToInt32(reader["ProfileCompleteness"]),
            reader["ProfileJson"] as string,
            reader.IsDBNull(reader.GetOrdinal("LastExtractedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastExtractedAtUtc")),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("MessageCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("MessageCount")),
            reader.IsDBNull(reader.GetOrdinal("ImageMessageCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("ImageMessageCount")),
            reader.GetDateTime(reader.GetOrdinal("StartedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("LastMessageAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastMessageAtUtc")),
            reader["BotName"] as string,
            reader["PublicChatPath"] as string);
    }

    private async Task<IReadOnlyList<CrmCustomerConversationRow>> LoadRelatedConversationsAsync(
        MySqlConnection connection,
        CrmCustomerDirectoryDetail customer,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT conversation.id, conversation.VisitorLabel, conversation.Status,
                conversation.MessageCount, conversation.ImageMessageCount,
                conversation.StartedAtUtc, conversation.LastMessageAtUtc, bot.BotName
            FROM bee_CrmConversation AS conversation
            LEFT JOIN bee_CrmCustomerProfile AS profile ON profile.ConversationId = conversation.id
            LEFT JOIN bee_CrmChatbot AS bot ON bot.id = conversation.ChatbotId
            WHERE conversation.MerchantId = @MerchantId
              AND (
                    conversation.id = @ConversationId
                    OR (@VisitorKey IS NOT NULL AND conversation.VisitorKey = @VisitorKey)
                    OR (@Email IS NOT NULL AND profile.Email = @Email)
                    OR (@Phone IS NOT NULL AND profile.Phone = @Phone)
                    OR (@VisitorIp IS NOT NULL AND @UserAgent IS NOT NULL
                        AND conversation.VisitorIp = @VisitorIp
                        AND conversation.UserAgent = @UserAgent
                        AND conversation.StartedAtUtc >= UTC_TIMESTAMP(6) - INTERVAL 30 DAY)
                  )
            ORDER BY COALESCE(conversation.LastMessageAtUtc, conversation.StartedAtUtc) DESC, conversation.id DESC
            LIMIT 20;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = (object?)customer.ConversationId ?? DBNull.Value;
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)customer.VisitorKey ?? DBNull.Value;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = (object?)customer.Email ?? DBNull.Value;
        command.Parameters.Add("@Phone", MySqlDbType.VarChar, 80).Value = (object?)customer.Phone ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)customer.VisitorIp ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)customer.UserAgent ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmCustomerConversationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmCustomerConversationRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["VisitorLabel"] as string,
                reader["Status"] as string ?? string.Empty,
                reader.GetInt32(reader.GetOrdinal("MessageCount")),
                reader.GetInt32(reader.GetOrdinal("ImageMessageCount")),
                reader.GetDateTime(reader.GetOrdinal("StartedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastMessageAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastMessageAtUtc")),
                reader["BotName"] as string));
        }

        return rows;
    }

    private async Task<IReadOnlyList<CrmCustomerMessageRow>> LoadMessagesAsync(
        MySqlConnection connection,
        long conversationId,
        CancellationToken cancellationToken)
    {
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
        var rows = new List<CrmCustomerMessageRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmCustomerMessageRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["SenderRole"] as string ?? string.Empty,
                reader["Body"] as string,
                reader["ImageUrl"] as string,
                reader["ModelName"] as string,
                reader.GetInt32(reader.GetOrdinal("PromptTokens")),
                reader.GetInt32(reader.GetOrdinal("CompletionTokens")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        return rows;
    }
}

public sealed record CrmCustomerDirectoryRow(
    long? ProfileId,
    long? ConversationId,
    string? VisitorKey,
    string? VisitorIp,
    string? UserAgent,
    string? VisitorLabel,
    string? DisplayName,
    string? Email,
    string? Phone,
    string? CompanyName,
    string? IntentSummary,
    string? ProductInterest,
    string? LifecycleStage,
    string? Sentiment,
    int PriorityScore,
    int ProfileCompleteness,
    int MessageCount,
    int ImageMessageCount,
    DateTime? LastMessageAtUtc,
    DateTime UpdatedAtUtc,
    string? BotName,
    string? PublicChatPath);

public sealed record CrmCustomerDirectoryDetail(
    long? ProfileId,
    long? ConversationId,
    string? VisitorKey,
    string? VisitorIp,
    string? UserAgent,
    string? VisitorLabel,
    string? DisplayName,
    string? Email,
    string? Phone,
    string? CompanyName,
    string? JobTitle,
    string? Location,
    string? Language,
    string? CustomerType,
    string? LifecycleStage,
    string? IntentSummary,
    string? NeedSummary,
    string? ProductInterest,
    string? IndustrySegment,
    string? BudgetRange,
    string? Timeline,
    string? DecisionRole,
    string? PainPoints,
    string? Objections,
    string? Preferences,
    string? Sentiment,
    int PriorityScore,
    int ProfileCompleteness,
    string? ProfileJson,
    DateTime? LastExtractedAtUtc,
    DateTime UpdatedAtUtc,
    int MessageCount,
    int ImageMessageCount,
    DateTime StartedAtUtc,
    DateTime? LastMessageAtUtc,
    string? BotName,
    string? PublicChatPath);

public sealed record CrmCustomerConversationRow(
    long Id,
    string? VisitorLabel,
    string Status,
    int MessageCount,
    int ImageMessageCount,
    DateTime StartedAtUtc,
    DateTime? LastMessageAtUtc,
    string? BotName);

public sealed record CrmCustomerMessageRow(
    long Id,
    string SenderRole,
    string? Body,
    string? ImageUrl,
    string? ModelName,
    int PromptTokens,
    int CompletionTokens,
    DateTime CreatedAtUtc);
