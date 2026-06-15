using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class RegisterModel(
    IConfiguration configuration,
    IConsoleEmailService emailService) : CrmMerchantPageModel(configuration)
{
    public IReadOnlyList<CrmIndustryOption> Industries { get; private set; } = [];

    public string? AlertMessage { get; private set; }

    public string? CodeMessage { get; private set; }

    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadIndustriesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSendCodeAsync(CancellationToken cancellationToken)
    {
        await LoadIndustriesAsync(cancellationToken);
        foreach (var key in ModelState.Keys.Where(key => !string.Equals(key, "Input.Email", StringComparison.Ordinal)).ToList())
        {
            ModelState.Remove(key);
        }

        var email = NormalizeEmail(Input.Email);
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
        {
            AlertMessage = "Enter a valid email before requesting a verification code.";
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var projectId = await GetCrmProjectIdAsync(connection, cancellationToken);
        if (await MerchantEmailExistsAsync(connection, projectId, email, cancellationToken))
        {
            AlertMessage = "This email is already registered. Please login instead.";
            return Page();
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        const string insertSql = """
            INSERT INTO bee_AppUserVerificationCode
                (ProjectId, PhoneNumber, Email, Purpose, CodeHash, ExpiresAtUtc)
            VALUES
                (@ProjectId, NULL, @Email, 'Register', @CodeHash, @ExpiresAtUtc);
            """;
        await using var command = new MySqlCommand(insertSql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{projectId}:{email}:Register:{code}");
        command.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var verificationCodeId = command.LastInsertedId;

        var emailResult = await emailService.SendVerificationCodeAsync(email, code, cancellationToken);
        await SaveEmailDeliveryAsync(connection, projectId, verificationCodeId, email, "Register", emailResult, cancellationToken);
        if (!emailResult.Success)
        {
            AlertMessage = emailResult.Message;
            return Page();
        }

        Input.Email = email;
        CodeMessage = "Verification code sent. It expires in 10 minutes.";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadIndustriesAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            AlertMessage = "Review the highlighted fields and try again.";
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var projectId = await GetCrmProjectIdAsync(connection, cancellationToken);
        var email = NormalizeEmail(Input.Email);
        var industry = await LoadIndustryAsync(connection, projectId, Input.IndustryId!.Value, cancellationToken);
        if (industry is null)
        {
            AlertMessage = "Select a valid industry.";
            return Page();
        }

        if (await MerchantEmailExistsAsync(connection, projectId, email, cancellationToken))
        {
            AlertMessage = "This email is already registered. Please login instead.";
            return Page();
        }

        var corpId = string.IsNullOrWhiteSpace(Input.CorpId)
            ? BuildCorpIdFromName(Input.BusinessName)
            : NormalizeCorpId(Input.CorpId);

        var passwordUser = new CrmMerchantPasswordUser(0, email);
        var passwordHash = CreatePasswordHasher().HashPassword(passwordUser, Input.Password);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var codeId = await FindValidVerificationCodeAsync(
                connection,
                (MySqlTransaction)transaction,
                projectId,
                email,
                Input.VerificationCode.Trim(),
                cancellationToken);
            if (codeId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                AlertMessage = "Verification code is invalid or expired.";
                return Page();
            }

            const string insertMerchantSql = """
                INSERT INTO bee_CrmMerchant
                    (ProjectId, IndustryId, BusinessName, CorpId, ContactName, Email, PasswordHash, WebsiteUrl, Status, PlanName, ProfileGuidanceInstructions, ProfileDimensionFocus)
                VALUES
                    (@ProjectId, @IndustryId, @BusinessName, @CorpId, @ContactName, @Email, @PasswordHash, @WebsiteUrl, 'Active', 'Starter', @ProfileGuidanceInstructions, @ProfileDimensionFocus);
                """;
            await using var merchantCommand = new MySqlCommand(insertMerchantSql, connection, transaction);
            merchantCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            merchantCommand.Parameters.Add("@IndustryId", MySqlDbType.Int32).Value = industry.Id;
            merchantCommand.Parameters.Add("@BusinessName", MySqlDbType.VarChar, 180).Value = Input.BusinessName.Trim();
            merchantCommand.Parameters.Add("@CorpId", MySqlDbType.VarChar, 80).Value = corpId;
            merchantCommand.Parameters.Add("@ContactName", MySqlDbType.VarChar, 120).Value = (object?)Input.ContactName?.Trim() ?? DBNull.Value;
            merchantCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
            merchantCommand.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
            merchantCommand.Parameters.Add("@WebsiteUrl", MySqlDbType.VarChar, 500).Value = (object?)Input.WebsiteUrl?.Trim() ?? DBNull.Value;
            merchantCommand.Parameters.Add("@ProfileGuidanceInstructions", MySqlDbType.Text).Value = BuildDefaultProfileGuidance(industry);
            merchantCommand.Parameters.Add("@ProfileDimensionFocus", MySqlDbType.Text).Value = BuildDefaultProfileDimensions(industry);
            await merchantCommand.ExecuteNonQueryAsync(cancellationToken);
            var merchantId = merchantCommand.LastInsertedId;

            const string insertBotSql = """
                INSERT INTO bee_CrmChatbot
                    (ProjectId, MerchantId, BotName, PublicChatPath, SystemPrompt, WelcomeMessage, Status)
                VALUES
                    (@ProjectId, @MerchantId, @BotName, @PublicChatPath, @SystemPrompt, @WelcomeMessage, 'Active');
                """;
            await using var botCommand = new MySqlCommand(insertBotSql, connection, transaction);
            botCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            botCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
            botCommand.Parameters.Add("@BotName", MySqlDbType.VarChar, 140).Value = CrmDefaultBotExperience.BuildDefaultBotName(Input.BusinessName);
            botCommand.Parameters.Add("@PublicChatPath", MySqlDbType.VarChar, 160).Value = corpId;
            botCommand.Parameters.Add("@SystemPrompt", MySqlDbType.Text).Value = BuildDefaultBotPrompt(industry);
            botCommand.Parameters.Add("@WelcomeMessage", MySqlDbType.VarChar, 500).Value = CrmDefaultBotExperience.BuildDefaultWelcomeMessage();
            await botCommand.ExecuteNonQueryAsync(cancellationToken);

            const string consumeSql = "UPDATE bee_AppUserVerificationCode SET ConsumedAtUtc = UTC_TIMESTAMP(6) WHERE id = @CodeId;";
            await using var consumeCommand = new MySqlCommand(consumeSql, connection, transaction);
            consumeCommand.Parameters.Add("@CodeId", MySqlDbType.Int64).Value = codeId.Value;
            await consumeCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            SignInMerchant(merchantId);
            return RedirectToPage("/Crm/Dashboard");
        }
        catch (MySqlException exception) when (exception.Number is 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            AlertMessage = "This email or corp id is already registered.";
            return Page();
        }
    }

    private async Task LoadIndustriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var projectId = await GetCrmProjectIdAsync(connection, cancellationToken);
        const string sql = """
            SELECT id, Name, Description, ChatGuidance, ProfileDimensionTemplate
            FROM bee_CrmIndustry
            WHERE ProjectId = @ProjectId AND IsActive = 1
            ORDER BY SortOrder, Name;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmIndustryOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmIndustryOption(
                reader.GetInt32(reader.GetOrdinal("id")),
                reader["Name"] as string ?? string.Empty,
                reader["Description"] as string,
                reader["ChatGuidance"] as string,
                reader["ProfileDimensionTemplate"] as string));
        }

        Industries = rows;
    }

    private static async Task<bool> MerchantEmailExistsAsync(
        MySqlConnection connection,
        int projectId,
        string email,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM bee_CrmMerchant WHERE ProjectId = @ProjectId AND Email = @Email LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<CrmIndustryOption?> LoadIndustryAsync(
        MySqlConnection connection,
        int projectId,
        int industryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, Name, Description, ChatGuidance, ProfileDimensionTemplate
            FROM bee_CrmIndustry
            WHERE ProjectId = @ProjectId
              AND id = @IndustryId
              AND IsActive = 1
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@IndustryId", MySqlDbType.Int32).Value = industryId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CrmIndustryOption(
                reader.GetInt32(reader.GetOrdinal("id")),
                reader["Name"] as string ?? string.Empty,
                reader["Description"] as string,
                reader["ChatGuidance"] as string,
                reader["ProfileDimensionTemplate"] as string)
            : null;
    }

    private static async Task<long?> FindValidVerificationCodeAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
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
              AND Purpose = 'Register'
              AND CodeHash = @CodeHash
              AND ConsumedAtUtc IS NULL
              AND ExpiresAtUtc > UTC_TIMESTAMP(6)
            ORDER BY id DESC
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{projectId}:{email}:Register:{verificationCode}");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
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

    private static string BuildDefaultBotPrompt(CrmIndustryOption industry)
    {
        return $"""
            You are a focused customer service assistant for a New Zealand Chinese business in this industry: {industry.Name}.
            {CrmDefaultBotExperience.BuildHumanServiceRules()}

            Answer from the merchant knowledge base when possible. Use the industry guidance below to shape the conversation.
            If the customer asks about uploaded menus, products, services, cases, prices, or policies, answer with the known facts first. Do not ask them to choose a file or page when the knowledge base already contains extracted text.
            If only partial information is available, give the closest useful answer first, then mention the missing detail briefly.
            If the customer asks unrelated questions, briefly acknowledge and guide the conversation back to the service, product, booking, quote, order, or next step relevant to this industry.
            Do not invent prices, policies, legal, medical, financial, immigration, or availability claims.

            Industry guidance:
            {industry.ChatGuidance ?? industry.Description}
            """;
    }

    private static string BuildDefaultProfileGuidance(CrmIndustryOption industry)
    {
        return $"""
            Naturally learn the customer profile during chat for a New Zealand Chinese business in {industry.Name}.
            Keep the answer helpful first, then guide the customer toward a booking, quote, order, consultation, viewing, or next action. Ask only one useful follow-up question when it is genuinely needed.
            If the customer drifts to unrelated topics, briefly answer only if safe, then bring the conversation back to this industry.

            Industry chat tendency:
            {industry.ChatGuidance ?? industry.Description}
            """;
    }

    private static string BuildDefaultProfileDimensions(CrmIndustryOption industry)
    {
        return $"""
            Core dimensions: name, contact method, language preference, location/suburb, customer type, intent, urgency, budget, timeline, decision role, objections, preferences, sentiment and next step.

            Industry-specific dimensions for {industry.Name}:
            {industry.ProfileDimensionTemplate ?? industry.Description}
            """;
    }

    private static string NormalizeEmail(string? email)
    {
        return email?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string HashSecret(string secret)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
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

    public sealed class RegisterInput
    {
        [Required]
        [StringLength(180)]
        public string BusinessName { get; set; } = string.Empty;

        [StringLength(80)]
        [RegularExpression("^[a-zA-Z0-9-]*$", ErrorMessage = "Use letters, numbers, and hyphens only.")]
        public string? CorpId { get; set; }

        [StringLength(120)]
        public string? ContactName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(150)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(8)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Url]
        [StringLength(500)]
        public string? WebsiteUrl { get; set; }

        [Required]
        public int? IndustryId { get; set; }

        [Required]
        [StringLength(6, MinimumLength = 6)]
        [RegularExpression("^[0-9]{6}$", ErrorMessage = "Enter the 6-digit verification code.")]
        public string VerificationCode { get; set; } = string.Empty;
    }
}

public sealed record CrmIndustryOption(
    int Id,
    string Name,
    string? Description,
    string? ChatGuidance,
    string? ProfileDimensionTemplate);
