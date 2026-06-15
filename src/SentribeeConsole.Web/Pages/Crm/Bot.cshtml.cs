using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Crm;

public class BotModel(
    IConfiguration configuration,
    IFileStorageService storageService) : CrmMerchantPageModel(configuration)
{
    private const long MaxAvatarLength = 3 * 1024 * 1024;

    public CrmMerchantSession Merchant { get; private set; } = null!;

    [BindProperty]
    public BotInput Input { get; set; } = new();

    public string? StatusMessage { get; private set; }

    public string PublicChatUrl => BuildChatUrl(Input.PublicChatPath);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmBotStatus"] as string;

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var bot = await EnsureBotAsync(connection, cancellationToken);
        Input = new BotInput
        {
            Id = bot.Id,
            BotName = bot.BotName,
            PublicChatPath = bot.PublicChatPath,
            ModelName = bot.ModelName,
            SystemPrompt = bot.SystemPrompt,
            WelcomeMessage = bot.WelcomeMessage,
            Status = bot.Status,
            AvatarUrl = bot.AvatarUrl
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? avatar, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var avatarUrl = Input.AvatarUrl;
        if (avatar is { Length: > 0 })
        {
            if (avatar.Length > MaxAvatarLength || !avatar.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(avatar), "Upload an image under 3 MB.");
                return Page();
            }

            var extension = Path.GetExtension(avatar.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = avatar.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            }

            await using var stream = avatar.OpenReadStream();
            var stored = await storageService.UploadAsync(
                stream,
                avatar.ContentType,
                extension,
                $"crm/{Merchant.ProjectId}/{Merchant.CorpId}/bot-avatar",
                cancellationToken);
            avatarUrl = stored.PublicUrl;
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var publicChatPath = NormalizeCorpId(Input.PublicChatPath);
        const string sql = """
            UPDATE bee_CrmChatbot
            SET BotName = @BotName,
                AvatarUrl = @AvatarUrl,
                PublicChatPath = @PublicChatPath,
                ModelName = @ModelName,
                SystemPrompt = @SystemPrompt,
                WelcomeMessage = @WelcomeMessage,
                Status = @Status,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @BotId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@BotId", MySqlDbType.Int64).Value = Input.Id;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@BotName", MySqlDbType.VarChar, 140).Value = Input.BotName.Trim();
        command.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 800).Value = (object?)avatarUrl ?? DBNull.Value;
        command.Parameters.Add("@PublicChatPath", MySqlDbType.VarChar, 160).Value = publicChatPath;
        command.Parameters.Add("@ModelName", MySqlDbType.VarChar, 80).Value = Input.ModelName.Trim();
        command.Parameters.Add("@SystemPrompt", MySqlDbType.Text).Value = (object?)Input.SystemPrompt?.Trim() ?? DBNull.Value;
        command.Parameters.Add("@WelcomeMessage", MySqlDbType.VarChar, 500).Value = (object?)Input.WelcomeMessage?.Trim() ?? DBNull.Value;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = Input.Status;
        await command.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmBotStatus"] = "Bot settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetChatQrAsync(bool download = false, CancellationToken cancellationToken = default)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var bot = await EnsureBotAsync(connection, cancellationToken);
        return GenerateChatQrCodeFile(bot.PublicChatPath, download);
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Bot";
        ViewData["PageTitle"] = "Bot";
        ViewData["ActiveMenu"] = "Bot";
    }

    private async Task<CrmBotRecord> EnsureBotAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT id, BotName, AvatarUrl, PublicChatPath, ModelName, SystemPrompt, WelcomeMessage, Status
            FROM bee_CrmChatbot
            WHERE MerchantId = @MerchantId
            ORDER BY id
            LIMIT 1;
            """;
        await using (var findCommand = new MySqlCommand(findSql, connection))
        {
            findCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            await using var reader = await findCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new CrmBotRecord(
                    reader.GetInt64(reader.GetOrdinal("id")),
                    reader["BotName"] as string ?? string.Empty,
                    reader["AvatarUrl"] as string,
                    reader["PublicChatPath"] as string ?? Merchant.CorpId,
                    reader["ModelName"] as string ?? "gpt-5.4-mini",
                    reader["SystemPrompt"] as string,
                    reader["WelcomeMessage"] as string,
                    reader["Status"] as string ?? "Active");
            }
        }

        const string insertSql = """
            INSERT INTO bee_CrmChatbot
                (ProjectId, MerchantId, BotName, PublicChatPath, ModelName, SystemPrompt, WelcomeMessage, Status)
            VALUES
                (@ProjectId, @MerchantId, @BotName, @PublicChatPath, 'gpt-5.4-mini', @SystemPrompt, @WelcomeMessage, 'Active');
            """;
        await using var insertCommand = new MySqlCommand(insertSql, connection);
        var defaultBotName = CrmDefaultBotExperience.BuildDefaultBotName(Merchant.BusinessName);
        var defaultSystemPrompt = $"""
            You are the customer service assistant for {Merchant.BusinessName}.
            {CrmDefaultBotExperience.BuildHumanServiceRules()}

            Answer from the merchant knowledge base whenever possible. If the customer asks about uploaded menus, services, products, prices, policies, or cases, give the known facts first.
            If information is missing, say what is missing briefly and ask one clarifying question only when it is needed for the next practical step.
            """;
        var defaultWelcomeMessage = CrmDefaultBotExperience.BuildDefaultWelcomeMessage();
        insertCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        insertCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        insertCommand.Parameters.Add("@BotName", MySqlDbType.VarChar, 140).Value = defaultBotName;
        insertCommand.Parameters.Add("@PublicChatPath", MySqlDbType.VarChar, 160).Value = Merchant.CorpId;
        insertCommand.Parameters.Add("@SystemPrompt", MySqlDbType.Text).Value = defaultSystemPrompt;
        insertCommand.Parameters.Add("@WelcomeMessage", MySqlDbType.VarChar, 500).Value = defaultWelcomeMessage;
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

        return new CrmBotRecord(
            insertCommand.LastInsertedId,
            defaultBotName,
            null,
            Merchant.CorpId,
            "gpt-5.4-mini",
            defaultSystemPrompt,
            defaultWelcomeMessage,
            "Active");
    }

    public sealed class BotInput
    {
        public long Id { get; set; }

        public string? AvatarUrl { get; set; }

        [Required]
        [StringLength(140)]
        public string BotName { get; set; } = string.Empty;

        [Required]
        [StringLength(160)]
        public string PublicChatPath { get; set; } = string.Empty;

        [Required]
        [StringLength(80)]
        public string ModelName { get; set; } = "gpt-5.4-mini";

        [StringLength(4000)]
        public string? SystemPrompt { get; set; }

        [StringLength(500)]
        public string? WelcomeMessage { get; set; }

        [Required]
        public string Status { get; set; } = "Active";
    }
}

public sealed record CrmBotRecord(
    long Id,
    string BotName,
    string? AvatarUrl,
    string PublicChatPath,
    string ModelName,
    string? SystemPrompt,
    string? WelcomeMessage,
    string Status);
