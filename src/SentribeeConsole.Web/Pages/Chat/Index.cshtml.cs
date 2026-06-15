using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Infrastructure.OpenAI;
using SentribeeConsole.Web.Pages.Crm;

namespace SentribeeConsole.Web.Pages.Chat;

public class IndexModel(
    IConfiguration configuration,
    IFileStorageService storageService,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions,
    IConsoleEmailService emailService) : PageModel
{
    private const long MaxImageLength = 8 * 1024 * 1024;
    private const string VisitorCookieName = "sentribee_chat_visitor";
    private const string VerifiedEmailCookieName = "sentribee_chat_email";
    private const string ChatVerificationPurpose = "Chat";
    private readonly OpenAIOptions _openAIOptions = openAIOptions.Value;

    public string CorpId { get; private set; } = string.Empty;

    public PublicChatBot? Bot { get; private set; }

    public bool IsUnavailable => Bot is null;

    public bool RequiresEmailVerification { get; private set; }

    public string? VerifiedEmail { get; private set; }

    public string? AlertMessage { get; private set; }

    public string? CodeMessage { get; private set; }

    public long? InitialConversationId { get; private set; }

    public IReadOnlyList<PublicChatMessage> InitialMessages { get; private set; } = [];

    [BindProperty]
    public ChatEmailInput EmailInput { get; set; } = new();

    private string ConnectionString =>
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    public async Task<IActionResult> OnGetAsync(
        string corpId,
        long? conversationId,
        string? resume,
        CancellationToken cancellationToken)
    {
        CorpId = corpId;
        Bot = await LoadBotAsync(corpId, cancellationToken);
        if (Bot is not null)
        {
            var visitor = GetOrCreateVisitorIdentity(Bot.PublicChatPath);
            VerifiedEmail = visitor.VerifiedEmail;
            if (string.IsNullOrWhiteSpace(VerifiedEmail))
            {
                RequiresEmailVerification = true;
                EmailInput.ConversationId = conversationId;
                EmailInput.Resume = resume;
                Response.Headers.CacheControl = "no-store";
                return Page();
            }

            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            if (conversationId.HasValue &&
                CrmChatResumeLink.IsValidToken(ConnectionString, Bot.PublicChatPath, conversationId.Value, resume) &&
                await ValidateConversationAsync(connection, Bot, conversationId.Value, cancellationToken) is not null)
            {
                InitialConversationId = conversationId.Value;
            }
            else
            {
                InitialConversationId = await FindRecentConversationAsync(connection, Bot, visitor, cancellationToken);
            }

            if (InitialConversationId.HasValue)
            {
                await TouchConversationVisitorAsync(connection, InitialConversationId.Value, visitor, cancellationToken);
                InitialMessages = await LoadPublicConversationMessagesAsync(connection, Bot.PublicChatPath, InitialConversationId.Value, cancellationToken);
            }
        }

        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(
        string corpId,
        long? conversationId,
        string? message,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        var bot = await LoadBotAsync(corpId, cancellationToken);
        if (bot is null)
        {
            return new JsonResult(new { success = false, message = "请先验证邮箱，再继续聊天。" })
            {
                StatusCode = StatusCodes.Status404NotFound
            };
        }

        var visitor = GetOrCreateVisitorIdentity(bot.PublicChatPath);
        if (string.IsNullOrWhiteSpace(visitor.VerifiedEmail))
        {
            return new JsonResult(new { success = false, message = "请先验证邮箱，再继续聊天。" })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
        }

        if (string.IsNullOrWhiteSpace(message) && image is null)
        {
            return new JsonResult(new { success = false, message = "请先验证邮箱，再继续聊天。" })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        string? imageUrl = null;
        string? imageInputUrl = null;
        if (image is { Length: > 0 })
        {
            if (image.Length > MaxImageLength || !image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonResult(new { success = false, message = "Upload an image under 8 MB." })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }

            var extension = Path.GetExtension(image.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = image.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            }

            var imageContentType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType;
            await using var memory = new MemoryStream();
            await image.CopyToAsync(memory, cancellationToken);
            var imageBytes = memory.ToArray();
            imageInputUrl = $"data:{imageContentType};base64,{Convert.ToBase64String(imageBytes)}";

            await using var stream = new MemoryStream(imageBytes);
            var stored = await storageService.UploadAsync(
                stream,
                imageContentType,
                extension,
                $"crm/{bot.ProjectId}/{bot.CorpId}/chat-images",
                cancellationToken);
            imageUrl = stored.PublicUrl;
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var activeConversationId = conversationId.HasValue
            ? await ValidateConversationAsync(connection, bot, conversationId.Value, cancellationToken)
            : null;
        activeConversationId ??= await FindRecentConversationAsync(connection, bot, visitor, cancellationToken);
        if (activeConversationId is null)
        {
            activeConversationId = await CreateConversationAsync(connection, bot, visitor, cancellationToken);
        }

        var userMessageId = await InsertMessageAsync(
            connection,
            bot,
            activeConversationId.Value,
            "User",
            message,
            imageUrl,
            null,
            0,
            0,
            cancellationToken);

        var recentMessages = await LoadRecentMessagesAsync(connection, activeConversationId.Value, cancellationToken);
        var knowledge = await LoadKnowledgeAsync(connection, bot, message, recentMessages, cancellationToken);
        var visitorContext = await LoadVisitorContextAsync(connection, bot, visitor, activeConversationId.Value, cancellationToken);
        OpenAIChatReply reply;
        try
        {
            reply = await GenerateReplyAsync(bot, knowledge, visitorContext, recentMessages, message, imageInputUrl, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new JsonResult(new
            {
                success = false,
                message = imageInputUrl is null
                    ? "刚刚没发出去。你稍等一下，再发我一次。"
                    : "这张图片我刚刚没读出来。你可以换一张，或者先用文字说一下图片里的重点。"
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }

        await InsertMessageAsync(
            connection,
            bot,
            activeConversationId.Value,
            "Assistant",
            reply.Text,
            null,
            bot.ModelName,
            reply.PromptTokens,
            reply.CompletionTokens,
            cancellationToken);

        await UpdateConversationAsync(connection, activeConversationId.Value, visitor, imageUrl is not null, cancellationToken);
        await UpsertUsageAsync(connection, bot, reply, imageUrl is not null, cancellationToken);
        try
        {
            var profileMessages = recentMessages
                .Concat([
                    new ChatHistoryMessage("User", string.IsNullOrWhiteSpace(message) ? "[Customer uploaded an image]" : message.Trim()),
                    new ChatHistoryMessage("Assistant", reply.Text)
                ])
                .ToList();
            await ExtractAndUpsertCustomerProfileAsync(connection, bot, visitor, activeConversationId.Value, visitorContext, profileMessages, cancellationToken);
        }
        catch
        {
            // Profile extraction is best-effort and must not block the live support chat.
        }

        return new JsonResult(new
        {
            success = true,
            conversationId = activeConversationId,
            reply = reply.Text,
            imageUrl = imageUrl is null ? null : BuildPublicMessageImageUrl(bot.PublicChatPath, userMessageId)
        });
    }

    public async Task<IActionResult> OnPostSendCodeAsync(
        string corpId,
        CancellationToken cancellationToken)
    {
        CorpId = corpId;
        Bot = await LoadBotAsync(corpId, cancellationToken);
        if (Bot is null)
        {
            return Page();
        }

        var visitor = GetOrCreateVisitorIdentity(Bot.PublicChatPath);
        VerifiedEmail = visitor.VerifiedEmail;
        if (!string.IsNullOrWhiteSpace(VerifiedEmail))
        {
            return RedirectToChat(Bot.PublicChatPath, EmailInput.ConversationId, EmailInput.Resume);
        }

        RequiresEmailVerification = true;
        var email = NormalizeEmail(EmailInput.Email);
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
        {
            AlertMessage = "请输入可用邮箱。";
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        const string insertSql = """
            INSERT INTO bee_AppUserVerificationCode
                (ProjectId, PhoneNumber, Email, Purpose, CodeHash, ExpiresAtUtc)
            VALUES
                (@ProjectId, NULL, @Email, @Purpose, @CodeHash, @ExpiresAtUtc);
            """;
        await using var command = new MySqlCommand(insertSql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Bot.ProjectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = ChatVerificationPurpose;
        command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{Bot.ProjectId}:{email}:{ChatVerificationPurpose}:{code}");
        command.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var verificationCodeId = command.LastInsertedId;

        var emailResult = await emailService.SendVerificationCodeAsync(email, code, cancellationToken);
        await SaveEmailDeliveryAsync(connection, Bot.ProjectId, verificationCodeId, email, ChatVerificationPurpose, emailResult, cancellationToken);
        if (!emailResult.Success)
        {
            AlertMessage = emailResult.Message;
            EmailInput.Email = email;
            return Page();
        }

        EmailInput.Email = email;
        CodeMessage = "验证码已发送，10分钟内有效。";
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(
        string corpId,
        CancellationToken cancellationToken)
    {
        CorpId = corpId;
        Bot = await LoadBotAsync(corpId, cancellationToken);
        if (Bot is null)
        {
            return Page();
        }

        var visitor = GetOrCreateVisitorIdentity(Bot.PublicChatPath);
        RequiresEmailVerification = true;
        var email = NormalizeEmail(EmailInput.Email);
        var code = EmailInput.Code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email) || code.Length != 6 || !code.All(char.IsDigit))
        {
            AlertMessage = "邮箱或验证码不对。";
            EmailInput.Email = email;
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var codeId = await FindValidChatVerificationCodeAsync(connection, Bot.ProjectId, email, code, cancellationToken);
        if (codeId is null)
        {
            AlertMessage = "验证码无效或已过期。";
            EmailInput.Email = email;
            return Page();
        }

        const string consumeSql = "UPDATE bee_AppUserVerificationCode SET ConsumedAtUtc = UTC_TIMESTAMP(6) WHERE id = @CodeId;";
        await using var consumeCommand = new MySqlCommand(consumeSql, connection);
        consumeCommand.Parameters.Add("@CodeId", MySqlDbType.Int64).Value = codeId.Value;
        await consumeCommand.ExecuteNonQueryAsync(cancellationToken);

        SetVerifiedEmailCookie(Bot.PublicChatPath, visitor.Key!, email);
        return RedirectToChat(Bot.PublicChatPath, EmailInput.ConversationId, EmailInput.Resume);
    }

    private async Task<PublicChatBot?> LoadBotAsync(string corpId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT bot.id AS BotId, bot.ProjectId, bot.MerchantId, bot.BotName, bot.AvatarUrl,
                bot.PublicChatPath, bot.ModelName, bot.SystemPrompt, bot.WelcomeMessage,
                merchant.BusinessName, merchant.CorpId, merchant.ContextInstructions,
                merchant.ProfileGuidanceInstructions, merchant.ProfileDimensionFocus,
                industry.Name AS IndustryName, industry.ChatGuidance AS IndustryChatGuidance,
                industry.ProfileDimensionTemplate AS IndustryProfileDimensionTemplate
            FROM bee_CrmChatbot AS bot
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = bot.MerchantId
            LEFT JOIN bee_CrmIndustry AS industry ON industry.id = merchant.IndustryId
            WHERE bot.PublicChatPath = @CorpId
              AND bot.Status = 'Active'
              AND merchant.Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@CorpId", MySqlDbType.VarChar, 160).Value = corpId.Trim().ToLowerInvariant();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublicChatBot(
            reader.GetInt64(reader.GetOrdinal("BotId")),
            reader.GetInt32(reader.GetOrdinal("ProjectId")),
            reader.GetInt64(reader.GetOrdinal("MerchantId")),
            reader["BusinessName"] as string ?? string.Empty,
            reader["CorpId"] as string ?? string.Empty,
            reader["BotName"] as string ?? string.Empty,
            reader["AvatarUrl"] as string,
            reader["PublicChatPath"] as string ?? string.Empty,
            reader["ModelName"] as string ?? _openAIOptions.Model,
            reader["SystemPrompt"] as string,
            reader["WelcomeMessage"] as string,
            reader["ContextInstructions"] as string,
            reader["ProfileGuidanceInstructions"] as string,
            reader["ProfileDimensionFocus"] as string,
            reader["IndustryName"] as string,
            reader["IndustryChatGuidance"] as string,
            reader["IndustryProfileDimensionTemplate"] as string);
    }

    private static async Task<long?> ValidateConversationAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        long conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_CrmConversation
            WHERE id = @ConversationId
              AND MerchantId = @MerchantId
              AND ProjectId = @ProjectId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private static async Task<long?> FindRecentConversationAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        ChatVisitorIdentity visitor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_CrmConversation
            WHERE MerchantId = @MerchantId
              AND ProjectId = @ProjectId
              AND (ChatbotId = @ChatbotId OR ChatbotId IS NULL)
              AND Status <> 'Closed'
              AND (
                    (@VisitorEmail IS NOT NULL AND VisitorEmail = @VisitorEmail)
                    OR (@VisitorKey IS NOT NULL AND VisitorKey = @VisitorKey)
                    OR (@VisitorIp IS NOT NULL AND @UserAgent IS NOT NULL
                        AND VisitorIp = @VisitorIp
                        AND UserAgent = @UserAgent
                        AND StartedAtUtc >= UTC_TIMESTAMP(6) - INTERVAL 30 DAY)
                  )
            ORDER BY COALESCE(LastSeenAtUtc, LastMessageAtUtc, StartedAtUtc) DESC, id DESC
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        command.Parameters.Add("@ChatbotId", MySqlDbType.Int64).Value = bot.Id;
        command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private static async Task<long> CreateConversationAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        ChatVisitorIdentity visitor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmConversation
                (ProjectId, MerchantId, ChatbotId, VisitorLabel, VisitorEmail, EmailVerifiedAtUtc, VisitorKey, VisitorIp, UserAgent, Referrer, Channel, Status, StartedAtUtc, LastSeenAtUtc)
            VALUES
                (@ProjectId, @MerchantId, @ChatbotId, @VisitorLabel, @VisitorEmail, @EmailVerifiedAtUtc, @VisitorKey, @VisitorIp, @UserAgent, @Referrer, 'Web', 'Open', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ChatbotId", MySqlDbType.Int64).Value = bot.Id;
        command.Parameters.Add("@VisitorLabel", MySqlDbType.VarChar, 140).Value = BuildVisitorLabel(visitor);
        command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
        command.Parameters.Add("@EmailVerifiedAtUtc", MySqlDbType.DateTime).Value = string.IsNullOrWhiteSpace(visitor.VerifiedEmail) ? DBNull.Value : (object)DateTime.UtcNow;
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
        command.Parameters.Add("@Referrer", MySqlDbType.VarChar, 1000).Value = (object?)visitor.Referrer ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    private static async Task<long> InsertMessageAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        long conversationId,
        string senderRole,
        string? body,
        string? imageUrl,
        string? modelName,
        int promptTokens,
        int completionTokens,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmConversationMessage
                (ProjectId, ConversationId, MerchantId, ChatbotId, SenderRole, Body, ImageUrl, ModelName, PromptTokens, CompletionTokens)
            VALUES
                (@ProjectId, @ConversationId, @MerchantId, @ChatbotId, @SenderRole, @Body, @ImageUrl, @ModelName, @PromptTokens, @CompletionTokens);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ChatbotId", MySqlDbType.Int64).Value = bot.Id;
        command.Parameters.Add("@SenderRole", MySqlDbType.VarChar, 40).Value = senderRole;
        command.Parameters.Add("@Body", MySqlDbType.MediumText).Value = (object?)body?.Trim() ?? DBNull.Value;
        command.Parameters.Add("@ImageUrl", MySqlDbType.VarChar, 1000).Value = (object?)imageUrl ?? DBNull.Value;
        command.Parameters.Add("@ModelName", MySqlDbType.VarChar, 80).Value = (object?)modelName ?? DBNull.Value;
        command.Parameters.Add("@PromptTokens", MySqlDbType.Int32).Value = promptTokens;
        command.Parameters.Add("@CompletionTokens", MySqlDbType.Int32).Value = completionTokens;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    private static async Task UpdateConversationAsync(
        MySqlConnection connection,
        long conversationId,
        ChatVisitorIdentity visitor,
        bool hasImage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_CrmConversation
            SET MessageCount = MessageCount + 2,
                ImageMessageCount = ImageMessageCount + @ImageIncrement,
                VisitorEmail = COALESCE(VisitorEmail, @VisitorEmail),
                EmailVerifiedAtUtc = COALESCE(EmailVerifiedAtUtc, @EmailVerifiedAtUtc),
                VisitorKey = COALESCE(VisitorKey, @VisitorKey),
                VisitorIp = COALESCE(@VisitorIp, VisitorIp),
                UserAgent = COALESCE(@UserAgent, UserAgent),
                Referrer = COALESCE(@Referrer, Referrer),
                LastSeenAtUtc = UTC_TIMESTAMP(6),
                LastMessageAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ConversationId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@ImageIncrement", MySqlDbType.Int32).Value = hasImage ? 1 : 0;
        command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
        command.Parameters.Add("@EmailVerifiedAtUtc", MySqlDbType.DateTime).Value = string.IsNullOrWhiteSpace(visitor.VerifiedEmail) ? DBNull.Value : (object)DateTime.UtcNow;
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
        command.Parameters.Add("@Referrer", MySqlDbType.VarChar, 1000).Value = (object?)visitor.Referrer ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchConversationVisitorAsync(
        MySqlConnection connection,
        long conversationId,
        ChatVisitorIdentity visitor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_CrmConversation
            SET VisitorEmail = COALESCE(VisitorEmail, @VisitorEmail),
                EmailVerifiedAtUtc = COALESCE(EmailVerifiedAtUtc, @EmailVerifiedAtUtc),
                VisitorKey = COALESCE(VisitorKey, @VisitorKey),
                VisitorIp = COALESCE(@VisitorIp, VisitorIp),
                UserAgent = COALESCE(@UserAgent, UserAgent),
                Referrer = COALESCE(@Referrer, Referrer),
                LastSeenAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ConversationId;
        """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
        command.Parameters.Add("@EmailVerifiedAtUtc", MySqlDbType.DateTime).Value = string.IsNullOrWhiteSpace(visitor.VerifiedEmail) ? DBNull.Value : (object)DateTime.UtcNow;
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
        command.Parameters.Add("@Referrer", MySqlDbType.VarChar, 1000).Value = (object?)visitor.Referrer ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> LoadKnowledgeAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        string? currentMessage,
        IReadOnlyList<ChatHistoryMessage> recentMessages,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FileName, ExtractedText, UploadedAtUtc
            FROM bee_CrmKnowledgeDocument
            WHERE MerchantId = @MerchantId
              AND Status = 'Ready'
              AND ExtractedText IS NOT NULL
              AND ExtractedText <> ''
            ORDER BY UploadedAtUtc DESC, id DESC
            LIMIT 40;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<KnowledgeDocumentText>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader["FileName"] as string ?? "Knowledge";
            var text = reader["ExtractedText"] as string ?? string.Empty;
            documents.Add(new KnowledgeDocumentText(fileName, text));
        }

        if (documents.Count == 0)
        {
            return string.Empty;
        }

        var queryText = new StringBuilder();
        queryText.AppendLine(currentMessage);
        foreach (var item in recentMessages.TakeLast(4))
        {
            queryText.AppendLine(item.Body);
        }

        var terms = ExtractKnowledgeSearchTerms(queryText.ToString());
        var sections = new List<string>();
        var totalLength = 0;
        foreach (var document in documents)
        {
            if (totalLength >= 28000)
            {
                break;
            }

            var snippet = BuildRelevantKnowledgeSnippet(document.Text, terms);
            if (string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var section = $"[{document.FileName}]\n{snippet}";
            if (section.Length > 6500)
            {
                section = section[..6500];
            }

            sections.Add(section);
            totalLength += section.Length;
        }

        return string.Join("\n\n", sections);
    }

    private static IReadOnlyList<string> ExtractKnowledgeSearchTerms(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || IsCjk(c))
            {
                token.Append(c);
                continue;
            }

            AddKnowledgeToken(terms, token.ToString());
            token.Clear();
        }

        AddKnowledgeToken(terms, token.ToString());
        return terms
            .Where(term => term.Length >= 2 && !IsLowValueKnowledgeTerm(term))
            .OrderByDescending(term => term.Length)
            .Take(30)
            .ToList();
    }

    private static void AddKnowledgeToken(HashSet<string> terms, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        terms.Add(value);
        if (!value.Any(IsCjk) || value.Length < 3)
        {
            return;
        }

        for (var length = Math.Min(6, value.Length); length >= 2; length--)
        {
            for (var index = 0; index <= value.Length - length; index++)
            {
                terms.Add(value.Substring(index, length));
            }
        }
    }

    private static string BuildRelevantKnowledgeSnippet(string text, IReadOnlyList<string> terms)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(2500)
            .ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var scored = lines
            .Select((line, index) => new
            {
                Line = line,
                Index = index,
                Score = terms.Count == 0 ? 0 : terms.Sum(term => line.Contains(term, StringComparison.OrdinalIgnoreCase) ? Math.Min(term.Length, 8) : 0)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(18)
            .ToList();

        if (scored.Count == 0)
        {
            return string.Join("\n", lines.Take(80));
        }

        var selectedIndexes = new SortedSet<int>();
        foreach (var item in scored)
        {
            for (var index = Math.Max(0, item.Index - 1); index <= Math.Min(lines.Count - 1, item.Index + 1); index++)
            {
                selectedIndexes.Add(index);
            }
        }

        var builder = new StringBuilder();
        var previous = -2;
        foreach (var index in selectedIndexes)
        {
            if (index > previous + 1 && builder.Length > 0)
            {
                builder.AppendLine("...");
            }

            builder.AppendLine(lines[index]);
            previous = index;
            if (builder.Length >= 6200)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsCjk(char value)
    {
        return value is >= '\u3400' and <= '\u9fff';
    }

    private static bool IsLowValueKnowledgeTerm(string term)
    {
        return term is "什么" or "多少" or "有没有" or "有吗" or "请问" or "你好" or "我们" or "客户" or "问题" or "咨询";
    }

    private static async Task<IReadOnlyList<ChatHistoryMessage>> LoadRecentMessagesAsync(
        MySqlConnection connection,
        long conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT SenderRole, Body
            FROM bee_CrmConversationMessage
            WHERE ConversationId = @ConversationId
              AND Body IS NOT NULL
              AND Body <> ''
            ORDER BY CreatedAtUtc DESC, id DESC
            LIMIT 10;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ChatHistoryMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChatHistoryMessage(
                reader["SenderRole"] as string ?? string.Empty,
                reader["Body"] as string ?? string.Empty));
        }

        rows.Reverse();
        return rows;
    }

    private static async Task<IReadOnlyList<PublicChatMessage>> LoadPublicConversationMessagesAsync(
        MySqlConnection connection,
        string publicChatPath,
        long conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, SenderRole, Body, ImageUrl, CreatedAtUtc
            FROM bee_CrmConversationMessage
            WHERE ConversationId = @ConversationId
            ORDER BY CreatedAtUtc, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PublicChatMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetInt64(reader.GetOrdinal("id"));
            var imageUrl = reader["ImageUrl"] as string;
            rows.Add(new PublicChatMessage(
                messageId,
                reader["SenderRole"] as string ?? string.Empty,
                reader["Body"] as string,
                string.IsNullOrWhiteSpace(imageUrl) ? null : BuildPublicMessageImageUrl(publicChatPath, messageId),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        return rows;
    }

    private static string BuildPublicMessageImageUrl(string publicChatPath, long messageId)
    {
        return $"/api/crm/public/chat/{Uri.EscapeDataString(publicChatPath)}/messages/{messageId}/image";
    }

    private static async Task<string> LoadVisitorContextAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        ChatVisitorIdentity visitor,
        long conversationId,
        CancellationToken cancellationToken)
    {
        var context = new StringBuilder();
        context.AppendLine("Visitor continuity:");
        context.AppendLine("- Treat the cookie visitor key as the strongest continuity signal.");
        context.AppendLine("- Treat IP plus browser as a weak fallback signal only. Do not claim it proves the same person.");
        context.AppendLine("- Do not mention cookie, IP, browser, or tracking to the customer unless they ask.");
        context.AppendLine($"- Current visitor key present: {!string.IsNullOrWhiteSpace(visitor.Key)}.");
        context.AppendLine($"- Current verified email: {(string.IsNullOrWhiteSpace(visitor.VerifiedEmail) ? "not available" : visitor.VerifiedEmail)}.");
        context.AppendLine($"- Current IP present: {!string.IsNullOrWhiteSpace(visitor.IpAddress)}.");

        const string profileSql = """
            SELECT DisplayName, Email, Phone, CompanyName, JobTitle, Location, Language,
                CustomerType, LifecycleStage, IntentSummary, NeedSummary, ProductInterest,
                BudgetRange, Timeline, DecisionRole, PainPoints, Objections, Preferences,
                Sentiment, PriorityScore, ProfileCompleteness, LastExtractedAtUtc
            FROM bee_CrmCustomerProfile
            WHERE MerchantId = @MerchantId
              AND (
                    (@VisitorEmail IS NOT NULL AND Email = @VisitorEmail)
                    OR (@VisitorKey IS NOT NULL AND VisitorKey = @VisitorKey)
                    OR ConversationId = @ConversationId
                    OR (@VisitorIp IS NOT NULL AND @UserAgent IS NOT NULL
                        AND VisitorIp = @VisitorIp
                        AND UserAgent = @UserAgent)
                  )
            ORDER BY LastExtractedAtUtc DESC, UpdatedAtUtc DESC
            LIMIT 1;
            """;
        await using (var command = new MySqlCommand(profileSql, connection))
        {
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
            command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
            command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
            command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
            command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
            command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                context.AppendLine();
                context.AppendLine("Known customer profile:");
                AppendContextValue(context, "Name", reader["DisplayName"] as string);
                AppendContextValue(context, "Email", reader["Email"] as string);
                AppendContextValue(context, "Phone", reader["Phone"] as string);
                AppendContextValue(context, "Company", reader["CompanyName"] as string);
                AppendContextValue(context, "Role", reader["JobTitle"] as string);
                AppendContextValue(context, "Location", reader["Location"] as string);
                AppendContextValue(context, "Language", reader["Language"] as string);
                AppendContextValue(context, "Customer type", reader["CustomerType"] as string);
                AppendContextValue(context, "Lifecycle", reader["LifecycleStage"] as string);
                AppendContextValue(context, "Intent", reader["IntentSummary"] as string);
                AppendContextValue(context, "Need", reader["NeedSummary"] as string);
                AppendContextValue(context, "Interest", reader["ProductInterest"] as string);
                AppendContextValue(context, "Budget", reader["BudgetRange"] as string);
                AppendContextValue(context, "Timeline", reader["Timeline"] as string);
                AppendContextValue(context, "Decision role", reader["DecisionRole"] as string);
                AppendContextValue(context, "Pain points", reader["PainPoints"] as string);
                AppendContextValue(context, "Objections", reader["Objections"] as string);
                AppendContextValue(context, "Preferences", reader["Preferences"] as string);
                AppendContextValue(context, "Sentiment", reader["Sentiment"] as string);
                context.AppendLine($"- Priority: {Convert.ToInt32(reader["PriorityScore"])}.");
                context.AppendLine($"- Completeness: {Convert.ToInt32(reader["ProfileCompleteness"])}.");
            }
        }

        const string messagesSql = """
            SELECT conversation.id AS ConversationId, message.SenderRole, message.Body, message.CreatedAtUtc
            FROM bee_CrmConversationMessage AS message
            INNER JOIN bee_CrmConversation AS conversation ON conversation.id = message.ConversationId
            WHERE conversation.MerchantId = @MerchantId
              AND conversation.ProjectId = @ProjectId
              AND message.Body IS NOT NULL
              AND message.Body <> ''
              AND (
                    conversation.id = @ConversationId
                    OR (@VisitorEmail IS NOT NULL AND conversation.VisitorEmail = @VisitorEmail)
                    OR (@VisitorKey IS NOT NULL AND conversation.VisitorKey = @VisitorKey)
                    OR (@VisitorIp IS NOT NULL AND @UserAgent IS NOT NULL
                        AND conversation.VisitorIp = @VisitorIp
                        AND conversation.UserAgent = @UserAgent
                        AND conversation.StartedAtUtc >= UTC_TIMESTAMP(6) - INTERVAL 30 DAY)
                  )
            ORDER BY message.CreatedAtUtc DESC, message.id DESC
            LIMIT 14;
            """;
        await using (var command = new MySqlCommand(messagesSql, connection))
        {
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
            command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
            command.Parameters.Add("@VisitorEmail", MySqlDbType.VarChar, 180).Value = (object?)visitor.VerifiedEmail ?? DBNull.Value;
            command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
            command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
            command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var lines = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var role = reader["SenderRole"] as string ?? string.Empty;
                var body = reader["Body"] as string ?? string.Empty;
                if (body.Length > 700)
                {
                    body = body[..700];
                }

                lines.Add($"Conversation {reader.GetInt64(reader.GetOrdinal("ConversationId"))}, {role}: {body}");
            }

            if (lines.Count > 0)
            {
                lines.Reverse();
                context.AppendLine();
                context.AppendLine("Recent messages from this visitor:");
                foreach (var line in lines)
                {
                    context.AppendLine(line);
                }
            }
        }

        return context.ToString();
    }

    private async Task<OpenAIChatReply> GenerateReplyAsync(
        PublicChatBot bot,
        string knowledge,
        string visitorContext,
        IReadOnlyList<ChatHistoryMessage> recentMessages,
        string? message,
        string? imageUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_openAIOptions.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        var model = string.IsNullOrWhiteSpace(bot.ModelName) ? _openAIOptions.Model : bot.ModelName;
        var developerPrompt = $"""
            You are {bot.BotName}, a customer service assistant for {bot.BusinessName}.
            Use the merchant knowledge base and conversation context below.
            Do not invent prices, policy, availability, or legal/medical/financial claims.

            Global response policy:
            - Use a warmer e-commerce customer service tone. Be friendly, quick, practical, and a bit more enthusiastic, like a good online shop support agent.
            - In Chinese, it is fine to naturally use short warm phrases such as "亲", "我帮你看一下", "这个我直接跟你说", "没问题", and "我先给你整理重点". Do not overuse them.
            - Answer the customer's current question first using the knowledge base. Do not start by interviewing the customer.
            - If the customer asks about menu items, products, services, prices, fees, documents, eligibility, cases, policies, opening hours, or availability, search the provided knowledge base and give the closest known facts immediately.
            - For fee or price questions, if an amount is in the knowledge base, state the amount directly. Say whether it is an official/government fee and mention likely excluded costs only when useful.
            - Never ask the customer to choose "menu 1 or menu 2", a file, a page, or a screenshot if the relevant knowledge text is already provided. Use the text and answer.
            - If multiple matching items exist, give the most relevant options in a short natural reply.
            - If the exact answer is missing but related knowledge exists, say what is known first, then mention what is missing.
            - Ask a follow-up only when it is necessary to avoid a wrong answer or to complete the next business step. Keep it to one short question.
            - For immigration, legal, medical, financial, safety, or contractual topics, still provide useful initial direction from the knowledge base or similar cases, then suggest formal review by the business. Do not just ask for more facts.
            - Keep replies human and concise. No markdown tables, no headings, no asterisks, no long scripts.

            Merchant context:
            {bot.ContextInstructions}

            Bot instructions:
            {bot.SystemPrompt}

            Customer profiling strategy:
            {bot.ProfileGuidanceInstructions}

            Industry-specific profile dimensions:
            Industry: {bot.IndustryName ?? "General business"}
            {bot.ProfileDimensionFocus}

            Industry chat guidance:
            {bot.IndustryChatGuidance}

            Industry default profile dimensions:
            {bot.IndustryProfileDimensionTemplate}

            Visitor continuity context:
            {visitorContext}

            While helping, progressively learn the customer profile: name, contact preference, company, role, location, language, customer type, intent, product interest, pain points, urgency, budget, timeline, decision role, objections, preferences, sentiment, and next step. Ask at most one natural follow-up profile question at a time, only when it helps the customer move forward.
            If the customer asks about unrelated topics, answer briefly only when safe. Guide back to the industry-relevant service, order, quote, booking, consultation, viewing, or support task only when it helps the customer.

            Knowledge base:
            {knowledge}
            """;

        var historyText = string.Join("\n", recentMessages.Select(item => $"{item.Role}: {item.Body}"));
        var userText = $"""
            Recent conversation:
            {historyText}

            Customer message:
            {message}
            """;

        var userContent = imageUrl is null
            ? new object[] { new { type = "input_text", text = userText } }
            : [new { type = "input_text", text = userText }, new { type = "input_image", image_url = imageUrl }];

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri($"{_openAIOptions.BaseUrl.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(90);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAIOptions.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            input = new object[]
            {
                new { role = "developer", content = developerPrompt },
                new { role = "user", content = userContent }
            }
        });

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI chat failed with HTTP status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var text = ExtractOutputText(json.RootElement);
        var (promptTokens, completionTokens) = ExtractUsage(json.RootElement);
        return new OpenAIChatReply(text, promptTokens, completionTokens);
    }

    private ChatVisitorIdentity GetOrCreateVisitorIdentity(string publicChatPath)
    {
        var key = Request.Cookies[VisitorCookieName];
        if (!IsValidVisitorKey(key))
        {
            key = $"v_{Guid.NewGuid():N}";
        }

        var visitorKey = key!;
        Response.Cookies.Append(
            VisitorCookieName,
            visitorKey,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });

        var verifiedEmail = TryReadVerifiedEmail(publicChatPath, visitorKey);
        return new ChatVisitorIdentity(
            visitorKey,
            GetClientIp(),
            TrimHeader(Request.Headers.UserAgent.ToString(), 500),
            TrimHeader(Request.Headers.Referer.ToString(), 1000),
            verifiedEmail);
    }

    private IActionResult RedirectToChat(string publicChatPath, long? conversationId, string? resume)
    {
        var query = new List<string>();
        if (conversationId.HasValue)
        {
            query.Add($"conversationId={conversationId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(resume))
        {
            query.Add($"resume={Uri.EscapeDataString(resume)}");
        }

        var queryString = query.Count == 0 ? string.Empty : $"?{string.Join("&", query)}";
        return Redirect($"/chat/{Uri.EscapeDataString(publicChatPath)}{queryString}");
    }

    private async Task<long?> FindValidChatVerificationCodeAsync(
        MySqlConnection connection,
        int projectId,
        string email,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_AppUserVerificationCode
            WHERE ProjectId = @ProjectId
              AND Email = @Email
              AND Purpose = @Purpose
              AND CodeHash = @CodeHash
              AND ConsumedAtUtc IS NULL
              AND ExpiresAtUtc > UTC_TIMESTAMP(6)
            ORDER BY id DESC
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = ChatVerificationPurpose;
        command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{projectId}:{email}:{ChatVerificationPurpose}:{verificationCode}");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task SaveEmailDeliveryAsync(
        MySqlConnection connection,
        int projectId,
        long verificationCodeId,
        string email,
        string purpose,
        ConsoleEmailResult result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_AppEmailDelivery
                (ProjectId, VerificationCodeId, Email, Purpose, Provider, RequestStatus, ErrorText)
            VALUES
                (@ProjectId, @VerificationCodeId, @Email, @Purpose, @Provider, @RequestStatus, @ErrorText);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@VerificationCodeId", MySqlDbType.Int64).Value = verificationCodeId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
        command.Parameters.Add("@Provider", MySqlDbType.VarChar, 40).Value = result.Provider;
        command.Parameters.Add("@RequestStatus", MySqlDbType.VarChar, 40).Value = result.Success ? "Sent" : "Failed";
        command.Parameters.Add("@ErrorText", MySqlDbType.VarChar, 500).Value = (object?)TrimTo(result.ErrorText, 500) ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void SetVerifiedEmailCookie(string publicChatPath, string visitorKey, string email)
    {
        var token = CreateVerifiedEmailToken(publicChatPath, visitorKey, email, DateTimeOffset.UtcNow.AddYears(1));
        Response.Cookies.Append(
            VerifiedEmailCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
    }

    private string? TryReadVerifiedEmail(string publicChatPath, string visitorKey)
    {
        var token = Request.Cookies[VerifiedEmailCookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }

        var expectedSignature = SignPayload(payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSignature),
                Encoding.UTF8.GetBytes(parts[1])))
        {
            return null;
        }

        var payloadParts = payload.Split('|');
        if (payloadParts.Length != 4)
        {
            return null;
        }

        var normalizedPath = CrmChatResumeLink.NormalizePublicChatPath(publicChatPath);
        if (!string.Equals(payloadParts[0], normalizedPath, StringComparison.Ordinal)
            || !string.Equals(payloadParts[1], visitorKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (!long.TryParse(payloadParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var email = NormalizeEmail(payloadParts[2]);
        return new EmailAddressAttribute().IsValid(email) ? email : null;
    }

    private string CreateVerifiedEmailToken(string publicChatPath, string visitorKey, string email, DateTimeOffset expiresAt)
    {
        var normalizedPath = CrmChatResumeLink.NormalizePublicChatPath(publicChatPath);
        var normalizedEmail = NormalizeEmail(email);
        var payload = string.Join(
            '|',
            normalizedPath,
            visitorKey,
            normalizedEmail,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return $"{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.{SignPayload(payload)}";
    }

    private string SignPayload(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ConnectionString));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private string? GetClientIp()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return TrimHeader(forwardedFor.Split(',')[0].Trim(), 80);
        }

        var realIp = Request.Headers["X-Real-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(realIp))
        {
            return TrimHeader(realIp, 80);
        }

        return TrimHeader(HttpContext.Connection.RemoteIpAddress?.ToString(), 80);
    }

    private static bool IsValidVisitorKey(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length is >= 18 and <= 80
            && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    }

    private static string BuildVisitorLabel(ChatVisitorIdentity visitor)
    {
        if (!string.IsNullOrWhiteSpace(visitor.Key) && visitor.Key.Length >= 8)
        {
            return $"Visitor {visitor.Key[^8..]}";
        }

        return $"Visitor {DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    private static string NormalizeEmail(string? email)
    {
        return email?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string HashSecret(string secret)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? TrimHeader(string? value, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static void AppendContextValue(StringBuilder context, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context.AppendLine($"- {label}: {value}");
        }
    }

    private async Task ExtractAndUpsertCustomerProfileAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        ChatVisitorIdentity visitor,
        long conversationId,
        string visitorContext,
        IReadOnlyList<ChatHistoryMessage> messages,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_openAIOptions.ApiKey))
        {
            return;
        }

        var transcript = string.Join("\n", messages.Select(item => $"{item.Role}: {item.Body}"));
        var developerPrompt = """
            Extract the most detailed customer profile possible from the conversation.
            Return only one valid JSON object. Do not include markdown.
            Use null when unknown. Do not guess facts not supported by the conversation.
            Use visitor continuity only as background. IP and browser can support continuity, but they are not proof of identity.
            Fields:
            display_name, email, phone, company_name, job_title, location, language,
            customer_type, lifecycle_stage, intent_summary, need_summary, product_interest,
            industry_segment, budget_range, timeline, decision_role, pain_points, objections,
            preferences, sentiment, visitor_context_note, priority_score, profile_completeness, notes.
            priority_score and profile_completeness must be integers from 0 to 100.
            """;
        var userPrompt = $"""
            Merchant: {bot.BusinessName}
            Merchant industry: {bot.IndustryName ?? "General business"}
            Profile guidance:
            {bot.ProfileGuidanceInstructions}

            Industry dimensions:
            {bot.ProfileDimensionFocus}
            {bot.IndustryProfileDimensionTemplate}

            Visitor continuity:
            {visitorContext}

            Conversation:
            {transcript}
            """;

        var model = string.IsNullOrWhiteSpace(bot.ModelName) ? _openAIOptions.Model : bot.ModelName;
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri($"{_openAIOptions.BaseUrl.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(90);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAIOptions.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            input = new object[]
            {
                new { role = "developer", content = developerPrompt },
                new { role = "user", content = userPrompt }
            }
        });

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseJson = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var outputText = ExtractJsonObjectText(ExtractOutputText(responseJson.RootElement));
        using var profileJson = JsonDocument.Parse(outputText);
        var root = profileJson.RootElement;
        var displayName = GetJsonString(root, "display_name", 180);
        var extractedEmail = GetJsonString(root, "email", 180);
        var profileEmail = string.IsNullOrWhiteSpace(extractedEmail) ? visitor.VerifiedEmail : extractedEmail;
        var profileCompleteness = GetJsonInt(root, "profile_completeness", 0, 100);
        var priorityScore = GetJsonInt(root, "priority_score", 0, 100);

        const string sql = """
            INSERT INTO bee_CrmCustomerProfile
                (ProjectId, MerchantId, ChatbotId, ConversationId, VisitorLabel, VisitorKey, VisitorIp, UserAgent,
                 DisplayName, Email, Phone, CompanyName, JobTitle, Location, Language,
                 CustomerType, LifecycleStage, IntentSummary, NeedSummary, ProductInterest,
                 IndustrySegment, BudgetRange, Timeline, DecisionRole, PainPoints, Objections,
                 Preferences, Sentiment, PriorityScore, ProfileCompleteness, ProfileJson, LastExtractedAtUtc)
            VALUES
                (@ProjectId, @MerchantId, @ChatbotId, @ConversationId, @VisitorLabel, @VisitorKey, @VisitorIp, @UserAgent,
                 @DisplayName, @Email, @Phone, @CompanyName, @JobTitle, @Location, @Language,
                 @CustomerType, @LifecycleStage, @IntentSummary, @NeedSummary, @ProductInterest,
                 @IndustrySegment, @BudgetRange, @Timeline, @DecisionRole, @PainPoints, @Objections,
                 @Preferences, @Sentiment, @PriorityScore, @ProfileCompleteness, @ProfileJson, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                ChatbotId = VALUES(ChatbotId),
                ConversationId = VALUES(ConversationId),
                VisitorLabel = COALESCE(VALUES(VisitorLabel), VisitorLabel),
                VisitorKey = COALESCE(VisitorKey, VALUES(VisitorKey)),
                VisitorIp = COALESCE(VALUES(VisitorIp), VisitorIp),
                UserAgent = COALESCE(VALUES(UserAgent), UserAgent),
                DisplayName = COALESCE(VALUES(DisplayName), DisplayName),
                Email = COALESCE(VALUES(Email), Email),
                Phone = COALESCE(VALUES(Phone), Phone),
                CompanyName = COALESCE(VALUES(CompanyName), CompanyName),
                JobTitle = COALESCE(VALUES(JobTitle), JobTitle),
                Location = COALESCE(VALUES(Location), Location),
                Language = COALESCE(VALUES(Language), Language),
                CustomerType = COALESCE(VALUES(CustomerType), CustomerType),
                LifecycleStage = COALESCE(VALUES(LifecycleStage), LifecycleStage),
                IntentSummary = COALESCE(VALUES(IntentSummary), IntentSummary),
                NeedSummary = COALESCE(VALUES(NeedSummary), NeedSummary),
                ProductInterest = COALESCE(VALUES(ProductInterest), ProductInterest),
                IndustrySegment = COALESCE(VALUES(IndustrySegment), IndustrySegment),
                BudgetRange = COALESCE(VALUES(BudgetRange), BudgetRange),
                Timeline = COALESCE(VALUES(Timeline), Timeline),
                DecisionRole = COALESCE(VALUES(DecisionRole), DecisionRole),
                PainPoints = COALESCE(VALUES(PainPoints), PainPoints),
                Objections = COALESCE(VALUES(Objections), Objections),
                Preferences = COALESCE(VALUES(Preferences), Preferences),
                Sentiment = COALESCE(VALUES(Sentiment), Sentiment),
                PriorityScore = GREATEST(PriorityScore, VALUES(PriorityScore)),
                ProfileCompleteness = GREATEST(ProfileCompleteness, VALUES(ProfileCompleteness)),
                ProfileJson = VALUES(ProfileJson),
                LastExtractedAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ChatbotId", MySqlDbType.Int64).Value = bot.Id;
        command.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
        command.Parameters.Add("@VisitorLabel", MySqlDbType.VarChar, 140).Value = (object?)displayName ?? BuildVisitorLabel(visitor);
        command.Parameters.Add("@VisitorKey", MySqlDbType.VarChar, 80).Value = (object?)visitor.Key ?? DBNull.Value;
        command.Parameters.Add("@VisitorIp", MySqlDbType.VarChar, 80).Value = (object?)visitor.IpAddress ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = (object?)visitor.UserAgent ?? DBNull.Value;
        command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 180).Value = (object?)displayName ?? DBNull.Value;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = (object?)profileEmail ?? DBNull.Value;
        command.Parameters.Add("@Phone", MySqlDbType.VarChar, 80).Value = (object?)GetJsonString(root, "phone", 80) ?? DBNull.Value;
        command.Parameters.Add("@CompanyName", MySqlDbType.VarChar, 180).Value = (object?)GetJsonString(root, "company_name", 180) ?? DBNull.Value;
        command.Parameters.Add("@JobTitle", MySqlDbType.VarChar, 160).Value = (object?)GetJsonString(root, "job_title", 160) ?? DBNull.Value;
        command.Parameters.Add("@Location", MySqlDbType.VarChar, 180).Value = (object?)GetJsonString(root, "location", 180) ?? DBNull.Value;
        command.Parameters.Add("@Language", MySqlDbType.VarChar, 80).Value = (object?)GetJsonString(root, "language", 80) ?? DBNull.Value;
        command.Parameters.Add("@CustomerType", MySqlDbType.VarChar, 120).Value = (object?)GetJsonString(root, "customer_type", 120) ?? DBNull.Value;
        command.Parameters.Add("@LifecycleStage", MySqlDbType.VarChar, 80).Value = (object?)GetJsonString(root, "lifecycle_stage", 80) ?? DBNull.Value;
        command.Parameters.Add("@IntentSummary", MySqlDbType.VarChar, 500).Value = (object?)GetJsonString(root, "intent_summary", 500) ?? DBNull.Value;
        command.Parameters.Add("@NeedSummary", MySqlDbType.Text).Value = (object?)GetJsonString(root, "need_summary", 5000) ?? DBNull.Value;
        command.Parameters.Add("@ProductInterest", MySqlDbType.VarChar, 500).Value = (object?)GetJsonString(root, "product_interest", 500) ?? DBNull.Value;
        command.Parameters.Add("@IndustrySegment", MySqlDbType.VarChar, 180).Value = (object?)GetJsonString(root, "industry_segment", 180) ?? DBNull.Value;
        command.Parameters.Add("@BudgetRange", MySqlDbType.VarChar, 120).Value = (object?)GetJsonString(root, "budget_range", 120) ?? DBNull.Value;
        command.Parameters.Add("@Timeline", MySqlDbType.VarChar, 140).Value = (object?)GetJsonString(root, "timeline", 140) ?? DBNull.Value;
        command.Parameters.Add("@DecisionRole", MySqlDbType.VarChar, 160).Value = (object?)GetJsonString(root, "decision_role", 160) ?? DBNull.Value;
        command.Parameters.Add("@PainPoints", MySqlDbType.Text).Value = (object?)GetJsonString(root, "pain_points", 5000) ?? DBNull.Value;
        command.Parameters.Add("@Objections", MySqlDbType.Text).Value = (object?)GetJsonString(root, "objections", 5000) ?? DBNull.Value;
        command.Parameters.Add("@Preferences", MySqlDbType.Text).Value = (object?)GetJsonString(root, "preferences", 5000) ?? DBNull.Value;
        command.Parameters.Add("@Sentiment", MySqlDbType.VarChar, 80).Value = (object?)GetJsonString(root, "sentiment", 80) ?? DBNull.Value;
        command.Parameters.Add("@PriorityScore", MySqlDbType.Byte).Value = priorityScore;
        command.Parameters.Add("@ProfileCompleteness", MySqlDbType.Byte).Value = profileCompleteness;
        command.Parameters.Add("@ProfileJson", MySqlDbType.JSON).Value = root.GetRawText();
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            const string labelSql = "UPDATE bee_CrmConversation SET VisitorLabel = @VisitorLabel WHERE id = @ConversationId;";
            await using var labelCommand = new MySqlCommand(labelSql, connection);
            labelCommand.Parameters.Add("@ConversationId", MySqlDbType.Int64).Value = conversationId;
            labelCommand.Parameters.Add("@VisitorLabel", MySqlDbType.VarChar, 140).Value = displayName;
            await labelCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output))
        {
            return "I could not generate a response right now.";
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content))
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text" &&
                    contentItem.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        return "I could not generate a response right now.";
    }

    private static (int PromptTokens, int CompletionTokens) ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return (0, 0);
        }

        var inputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        return (inputTokens, outputTokens);
    }

    private static string ExtractJsonObjectText(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new JsonException("OpenAI profile extraction did not return a JSON object.");
        }

        return trimmed[start..(end + 1)];
    }

    private static string? GetJsonString(JsonElement root, string propertyName, int maxLength)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Array => string.Join(", ", property.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())),
            JsonValueKind.Object => property.GetRawText(),
            _ => property.ToString()
        };
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static int GetJsonInt(JsonElement root, string propertyName, int min, int max)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return min;
        }

        int value;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue))
        {
            value = numericValue;
        }
        else if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var stringValue))
        {
            value = stringValue;
        }
        else
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static async Task UpsertUsageAsync(
        MySqlConnection connection,
        PublicChatBot bot,
        OpenAIChatReply reply,
        bool hasImage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmUsageDaily
                (ProjectId, MerchantId, UsageDate, ModelName, PromptTokens, CompletionTokens, ImageCount, ConversationCount, MessageCount, EstimatedCostUsd)
            VALUES
                (@ProjectId, @MerchantId, UTC_DATE(), @ModelName, @PromptTokens, @CompletionTokens, @ImageCount, 1, 2, 0)
            ON DUPLICATE KEY UPDATE
                PromptTokens = PromptTokens + VALUES(PromptTokens),
                CompletionTokens = CompletionTokens + VALUES(CompletionTokens),
                ImageCount = ImageCount + VALUES(ImageCount),
                ConversationCount = ConversationCount + VALUES(ConversationCount),
                MessageCount = MessageCount + VALUES(MessageCount),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = bot.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = bot.MerchantId;
        command.Parameters.Add("@ModelName", MySqlDbType.VarChar, 80).Value = bot.ModelName;
        command.Parameters.Add("@PromptTokens", MySqlDbType.Int64).Value = reply.PromptTokens;
        command.Parameters.Add("@CompletionTokens", MySqlDbType.Int64).Value = reply.CompletionTokens;
        command.Parameters.Add("@ImageCount", MySqlDbType.Int32).Value = hasImage ? 1 : 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record PublicChatBot(
    long Id,
    int ProjectId,
    long MerchantId,
    string BusinessName,
    string CorpId,
    string BotName,
    string? AvatarUrl,
    string PublicChatPath,
    string ModelName,
    string? SystemPrompt,
    string? WelcomeMessage,
    string? ContextInstructions,
    string? ProfileGuidanceInstructions,
    string? ProfileDimensionFocus,
    string? IndustryName,
    string? IndustryChatGuidance,
    string? IndustryProfileDimensionTemplate);

public sealed record ChatHistoryMessage(string Role, string Body);

public sealed record KnowledgeDocumentText(string FileName, string Text);

public sealed record ChatVisitorIdentity(string? Key, string? IpAddress, string? UserAgent, string? Referrer, string? VerifiedEmail);

public sealed record PublicChatMessage(long Id, string SenderRole, string? Body, string? ImageUrl, DateTime CreatedAtUtc);

public sealed record OpenAIChatReply(string Text, int PromptTokens, int CompletionTokens);

public sealed class ChatEmailInput
{
    [EmailAddress]
    [StringLength(180)]
    public string Email { get; set; } = string.Empty;

    [StringLength(6, MinimumLength = 6)]
    [RegularExpression("^[0-9]{6}$")]
    public string Code { get; set; } = string.Empty;

    public long? ConversationId { get; set; }

    public string? Resume { get; set; }
}
