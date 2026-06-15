using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Crm;

public class ProfilesModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public CrmProfileSummary Summary { get; private set; } = new();

    public PagedResult<CrmCustomerProfileRow> Profiles { get; private set; } = new();

    public CrmCustomerProfileDetail? SelectedProfile { get; private set; }

    public IReadOnlyList<CrmProfileBreakdownRow> LifecycleBreakdown { get; private set; } = [];

    public IReadOnlyList<CrmProfileBreakdownRow> SentimentBreakdown { get; private set; } = [];

    public IReadOnlyList<CrmProfileBreakdownRow> InterestBreakdown { get; private set; } = [];

    public long? SelectedProfileId { get; private set; }

    public async Task<IActionResult> OnGetAsync(long? profileId, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Customer Profiles";
        ViewData["PageTitle"] = "Customer Profiles";
        ViewData["ActiveMenu"] = "Profiles";

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await LoadSummaryAsync(connection, cancellationToken);
        await LoadBreakdownsAsync(connection, cancellationToken);
        await LoadProfilesAsync(connection, pageNumber, cancellationToken);

        SelectedProfileId = profileId ?? Profiles.Items.FirstOrDefault()?.Id;
        if (SelectedProfileId.HasValue)
        {
            SelectedProfile = await LoadProfileDetailAsync(connection, SelectedProfileId.Value, cancellationToken);
        }

        return Page();
    }

    private async Task LoadSummaryAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalProfiles,
                COALESCE(AVG(ProfileCompleteness), 0) AS AverageCompleteness,
                COALESCE(SUM(CASE WHEN ProfileCompleteness >= 70 THEN 1 ELSE 0 END), 0) AS DetailedProfiles,
                COALESCE(SUM(CASE WHEN Email IS NOT NULL OR Phone IS NOT NULL THEN 1 ELSE 0 END), 0) AS ContactCaptured,
                COALESCE(SUM(CASE WHEN PriorityScore >= 70 THEN 1 ELSE 0 END), 0) AS HighPriorityProfiles
            FROM bee_CrmCustomerProfile
            WHERE MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Summary = new CrmProfileSummary(
            Convert.ToInt32(reader["TotalProfiles"]),
            Convert.ToDecimal(reader["AverageCompleteness"]),
            Convert.ToInt32(reader["DetailedProfiles"]),
            Convert.ToInt32(reader["ContactCaptured"]),
            Convert.ToInt32(reader["HighPriorityProfiles"]));
    }

    private async Task LoadBreakdownsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        LifecycleBreakdown = await LoadBreakdownAsync(connection, "LifecycleStage", cancellationToken);
        SentimentBreakdown = await LoadBreakdownAsync(connection, "Sentiment", cancellationToken);
        InterestBreakdown = await LoadBreakdownAsync(connection, "ProductInterest", cancellationToken);
    }

    private async Task<IReadOnlyList<CrmProfileBreakdownRow>> LoadBreakdownAsync(
        MySqlConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COALESCE(NULLIF({columnName}, ''), 'Unknown') AS Label, COUNT(*) AS CountValue
            FROM bee_CrmCustomerProfile
            WHERE MerchantId = @MerchantId
            GROUP BY COALESCE(NULLIF({columnName}, ''), 'Unknown')
            ORDER BY CountValue DESC, Label
            LIMIT 6;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmProfileBreakdownRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmProfileBreakdownRow(
                reader["Label"] as string ?? "Unknown",
                Convert.ToInt32(reader["CountValue"])));
        }

        return rows;
    }

    private async Task LoadProfilesAsync(MySqlConnection connection, int pageNumber, CancellationToken cancellationToken)
    {
        const int pageSize = 15;
        pageNumber = Math.Max(1, pageNumber);

        const string countSql = "SELECT COUNT(*) FROM bee_CrmCustomerProfile WHERE MerchantId = @MerchantId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT id, ConversationId, VisitorLabel, DisplayName, Email, Phone, CompanyName,
                IntentSummary, ProductInterest, LifecycleStage, Sentiment, PriorityScore,
                ProfileCompleteness, LastExtractedAtUtc, UpdatedAtUtc
            FROM bee_CrmCustomerProfile
            WHERE MerchantId = @MerchantId
            ORDER BY UpdatedAtUtc DESC, id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmCustomerProfileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmCustomerProfileRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetInt64(reader.GetOrdinal("ConversationId")),
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
                reader.IsDBNull(reader.GetOrdinal("LastExtractedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastExtractedAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))));
        }

        Profiles = new PagedResult<CrmCustomerProfileRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private async Task<CrmCustomerProfileDetail?> LoadProfileDetailAsync(
        MySqlConnection connection,
        long profileId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT profile.id, profile.ConversationId, profile.VisitorLabel, profile.DisplayName,
                profile.Email, profile.Phone, profile.CompanyName, profile.JobTitle, profile.Location,
                profile.Language, profile.CustomerType, profile.LifecycleStage, profile.IntentSummary,
                profile.NeedSummary, profile.ProductInterest, profile.IndustrySegment, profile.BudgetRange,
                profile.Timeline, profile.DecisionRole, profile.PainPoints, profile.Objections,
                profile.Preferences, profile.Sentiment, profile.PriorityScore, profile.ProfileCompleteness,
                profile.ProfileJson, profile.LastExtractedAtUtc, profile.UpdatedAtUtc,
                conversation.MessageCount, conversation.ImageMessageCount
            FROM bee_CrmCustomerProfile AS profile
            LEFT JOIN bee_CrmConversation AS conversation ON conversation.id = profile.ConversationId
            WHERE profile.MerchantId = @MerchantId
              AND profile.id = @ProfileId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@ProfileId", MySqlDbType.Int64).Value = profileId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmCustomerProfileDetail(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetInt64(reader.GetOrdinal("ConversationId")),
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
            reader.IsDBNull(reader.GetOrdinal("ImageMessageCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("ImageMessageCount")));
    }
}

public sealed record CrmProfileSummary(
    int TotalProfiles = 0,
    decimal AverageCompleteness = 0,
    int DetailedProfiles = 0,
    int ContactCaptured = 0,
    int HighPriorityProfiles = 0);

public sealed record CrmProfileBreakdownRow(string Label, int Count);

public sealed record CrmCustomerProfileRow(
    long Id,
    long? ConversationId,
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
    DateTime? LastExtractedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CrmCustomerProfileDetail(
    long Id,
    long? ConversationId,
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
    int ImageMessageCount);
