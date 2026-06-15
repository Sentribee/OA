using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class DashboardModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public CrmMerchantDashboard Dashboard { get; private set; } = new();

    public string PublicChatPath { get; private set; } = string.Empty;

    public string PublicChatUrl => BuildChatUrl(PublicChatPath);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Dashboard";
        ViewData["PageTitle"] = "Dashboard";
        ViewData["ActiveMenu"] = "Dashboard";

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        PublicChatPath = await LoadPrimaryChatPathAsync(connection, cancellationToken) ?? Merchant.CorpId;
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM bee_CrmChatbot WHERE MerchantId = @MerchantId) AS ChatbotCount,
                (SELECT COUNT(*) FROM bee_CrmKnowledgeDocument WHERE MerchantId = @MerchantId) AS KnowledgeDocumentCount,
                (SELECT COUNT(*) FROM bee_CrmConversation WHERE MerchantId = @MerchantId) AS ConversationCount,
                (SELECT COUNT(*) FROM bee_CrmCustomerProfile WHERE MerchantId = @MerchantId) AS CustomerProfileCount,
                (SELECT COUNT(*) FROM bee_CrmEmployee WHERE MerchantId = @MerchantId AND Status = 'Active') AS EmployeeCount,
                (SELECT COUNT(*) FROM bee_CrmOfficeAddress WHERE MerchantId = @MerchantId AND Status = 'Active') AS OfficeCount,
                (SELECT COUNT(*) FROM bee_CrmEmployeeAttendance WHERE MerchantId = @MerchantId AND AttendanceDate = @TodayLocalDate) AS TodayAttendanceCount,
                (SELECT COALESCE(SUM(MessageCount), 0) FROM bee_CrmUsageDaily WHERE MerchantId = @MerchantId) AS MessageCount,
                (SELECT COALESCE(SUM(PromptTokens + CompletionTokens), 0) FROM bee_CrmUsageDaily WHERE MerchantId = @MerchantId) AS TokenCount,
                (SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM bee_CrmUsageDaily WHERE MerchantId = @MerchantId) AS EstimatedCostUsd;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@TodayLocalDate", MySqlDbType.Date).Value = GetLocalDate(Merchant.TimeZoneId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            Dashboard = new CrmMerchantDashboard(
                Convert.ToInt32(reader["ChatbotCount"]),
                Convert.ToInt32(reader["KnowledgeDocumentCount"]),
                Convert.ToInt32(reader["ConversationCount"]),
                Convert.ToInt32(reader["CustomerProfileCount"]),
                Convert.ToInt32(reader["EmployeeCount"]),
                Convert.ToInt32(reader["OfficeCount"]),
                Convert.ToInt32(reader["TodayAttendanceCount"]),
                Convert.ToInt64(reader["MessageCount"]),
                Convert.ToInt64(reader["TokenCount"]),
                reader.GetDecimal(reader.GetOrdinal("EstimatedCostUsd")));
        }

        return Page();
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
        var publicChatPath = await LoadPrimaryChatPathAsync(connection, cancellationToken) ?? Merchant.CorpId;
        return GenerateChatQrCodeFile(publicChatPath, download);
    }

    private async Task<string?> LoadPrimaryChatPathAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT PublicChatPath
            FROM bee_CrmChatbot
            WHERE MerchantId = @MerchantId
            ORDER BY Status = 'Active' DESC, id
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static DateTime GetLocalDate(string timeZoneId)
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone).Date;
        }
        catch
        {
            return DateTime.UtcNow.Date;
        }
    }
}

public sealed record CrmMerchantDashboard(
    int ChatbotCount = 0,
    int KnowledgeDocumentCount = 0,
    int ConversationCount = 0,
    int CustomerProfileCount = 0,
    int EmployeeCount = 0,
    int OfficeCount = 0,
    int TodayAttendanceCount = 0,
    long MessageCount = 0,
    long TokenCount = 0,
    decimal EstimatedCostUsd = 0);
