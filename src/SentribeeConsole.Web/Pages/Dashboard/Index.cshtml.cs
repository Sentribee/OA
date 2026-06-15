using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Dashboard;

[Authorize]
public class IndexModel(
    IConfiguration configuration,
    IProjectService projectService,
    IEdgeDeviceService edgeDeviceService,
    IYoloModelService yoloModelService,
    IEdgeAiService edgeAiService,
    IWeatherForecastService weatherForecastService) : PageModel
{
    public ProjectRequirementSummary Requirements { get; private set; } = new();

    public PagedResult<EdgeDevice> DevicePage { get; private set; } = new();

    public PagedResult<EdgeEvent> EventPage { get; private set; } = new();

    public YoloModelDashboard Yolo { get; private set; } = new();

    public EdgeAiDashboard EdgeAi { get; private set; } = new();

    public int OnlineDeviceCount { get; private set; }

    public int ConfirmedEventCount { get; private set; }

    public EdgeEventStatusCounts EventStatusCounts { get; private set; } = new();

    public int CodeVersionCount { get; private set; }

    public string CurrentCodeVersion { get; private set; } = "-";

    public int BoundInstanceCount { get; private set; }

    public WeatherForecastSummary Weather { get; private set; } = new();

    public Project Project { get; private set; } = new();

    public bool IsCrmProject { get; private set; }

    public CrmDashboardSummary CrmSummary { get; private set; } = new();

    public bool IsSpendBeeProject { get; private set; }

    public SpendBeeDashboardSummary SpendBeeSummary { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (project is not null)
        {
            Project = project;
            IsCrmProject = project.IsSentribeeCrmProject;
            IsSpendBeeProject = project.IsSpendBeeProject;
            if (IsCrmProject)
            {
                Requirements = new ProjectRequirementSummary
                {
                    OtherRequirements = project.Rules
                        .Where(rule => string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(rule.ChangeType, "Added", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                };
                CrmSummary = await LoadCrmDashboardSummaryAsync(project.Id, cancellationToken);
                return Page();
            }

            if (IsSpendBeeProject)
            {
                SpendBeeSummary = await LoadSpendBeeDashboardSummaryAsync(project.Id, cancellationToken);
                return Page();
            }

            DevicePage = await edgeDeviceService.ListByAdminAsync(adminId.Value, 1, 1, cancellationToken);
            var deviceCountPage = await edgeDeviceService.ListByAdminAsync(adminId.Value, 1, 100, cancellationToken);
            EventPage = await edgeDeviceService.ListEventsByAdminAsync(adminId.Value, new EdgeEventFilters(), 1, 5, cancellationToken);
            EventStatusCounts = await edgeDeviceService.GetEventStatusCountsAsync(adminId.Value, new EdgeEventFilters(), cancellationToken);
            Yolo = await yoloModelService.GetDashboardAsync(adminId.Value, new EdgeEventFilters(), 1, 1, 1, 1, cancellationToken);
            EdgeAi = await edgeAiService.GetDashboardAsync(adminId.Value, cancellationToken);
            Requirements = EdgeAi.Requirements;
            var allInstances = EdgeAi.Logics.SelectMany(logic => logic.Instances).ToList();
            OnlineDeviceCount = deviceCountPage.Items.Count(device => device.IsOnline);
            ConfirmedEventCount = EventStatusCounts.RealRisk;
            CodeVersionCount = EdgeAi.Logics.SelectMany(logic => logic.Versions).Count();
            CurrentCodeVersion = EdgeAi.Logics
                .SelectMany(logic => logic.Versions)
                .FirstOrDefault(version => version.IsCurrent)
                ?.VersionName ?? "-";
            BoundInstanceCount = allInstances.Count;

            var weatherDevice = deviceCountPage.Items.FirstOrDefault(item => item.Latitude.HasValue && item.Longitude.HasValue);
            var latitude = weatherDevice?.Latitude ?? -36.8485m;
            var longitude = weatherDevice?.Longitude ?? 174.7633m;
            var locationName = weatherDevice?.Name ?? "Auckland";
            Weather = await weatherForecastService.GetNext24HoursAsync(latitude, longitude, locationName, cancellationToken);
        }

        return Page();
    }

    private async Task<CrmDashboardSummary> LoadCrmDashboardSummaryAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM bee_CrmIndustry WHERE ProjectId = @ProjectId) AS IndustryCount,
                (SELECT COUNT(*) FROM bee_CrmMerchant WHERE ProjectId = @ProjectId) AS MerchantCount,
                (SELECT COUNT(*) FROM bee_CrmMerchant WHERE ProjectId = @ProjectId AND Status = 'Active') AS ActiveMerchantCount,
                (SELECT COUNT(*) FROM bee_CrmChatbot WHERE ProjectId = @ProjectId) AS ChatbotCount,
                (SELECT COUNT(*) FROM bee_CrmKnowledgeDocument WHERE ProjectId = @ProjectId) AS KnowledgeDocumentCount,
                (SELECT COUNT(*) FROM bee_CrmConversation WHERE ProjectId = @ProjectId) AS ConversationCount,
                (SELECT COALESCE(SUM(MessageCount), 0) FROM bee_CrmConversation WHERE ProjectId = @ProjectId) AS ConversationMessageCount,
                (SELECT COALESCE(SUM(ImageMessageCount), 0) FROM bee_CrmConversation WHERE ProjectId = @ProjectId) AS ConversationImageCount,
                (SELECT COALESCE(SUM(PromptTokens + CompletionTokens), 0) FROM bee_CrmUsageDaily WHERE ProjectId = @ProjectId) AS TokenCount,
                (SELECT COALESCE(SUM(MessageCount), 0) FROM bee_CrmUsageDaily WHERE ProjectId = @ProjectId) AS UsageMessageCount,
                (SELECT COALESCE(SUM(ImageCount), 0) FROM bee_CrmUsageDaily WHERE ProjectId = @ProjectId) AS UsageImageCount,
                (SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM bee_CrmUsageDaily WHERE ProjectId = @ProjectId) AS EstimatedCostUsd;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CrmDashboardSummary();
        }

        return new CrmDashboardSummary(
            Convert.ToInt32(reader["IndustryCount"]),
            Convert.ToInt32(reader["MerchantCount"]),
            Convert.ToInt32(reader["ActiveMerchantCount"]),
            Convert.ToInt32(reader["ChatbotCount"]),
            Convert.ToInt32(reader["KnowledgeDocumentCount"]),
            Convert.ToInt32(reader["ConversationCount"]),
            Convert.ToInt64(reader["ConversationMessageCount"]),
            Convert.ToInt64(reader["ConversationImageCount"]),
            Convert.ToInt64(reader["TokenCount"]),
            Convert.ToInt64(reader["UsageMessageCount"]),
            Convert.ToInt64(reader["UsageImageCount"]),
            reader.GetDecimal(reader.GetOrdinal("EstimatedCostUsd")));
    }

    private async Task<SpendBeeDashboardSummary> LoadSpendBeeDashboardSummaryAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM bee_AppUser WHERE ProjectId = @ProjectId) AS UserCount,
                (SELECT COUNT(*) FROM bee_AppUser WHERE ProjectId = @ProjectId AND Status = 'Active') AS ActiveUserCount,
                (SELECT COUNT(*) FROM bee_AppUserDevice WHERE ProjectId = @ProjectId) AS DeviceCount,
                (SELECT COUNT(*) FROM bee_AppUserDevice WHERE ProjectId = @ProjectId AND LastLoginAtUtc >= UTC_TIMESTAMP(6) - INTERVAL 30 DAY) AS RecentDeviceCount,
                (SELECT COUNT(*) FROM bee_SpendBeeMerchant WHERE ProjectId = @ProjectId) AS MerchantCount,
                (SELECT COUNT(*) FROM bee_SpendBeeMerchant WHERE ProjectId = @ProjectId AND SyncStatus = 'Synced') AS SyncedMerchantCount,
                (SELECT COUNT(*) FROM bee_SpendBeeReceipt WHERE ProjectId = @ProjectId) AS ReceiptCount,
                (SELECT COUNT(*) FROM bee_SpendBeeReceipt WHERE ProjectId = @ProjectId AND Status = 'Completed') AS CompletedReceiptCount,
                (SELECT COUNT(*) FROM bee_SpendBeeReceipt WHERE ProjectId = @ProjectId AND Status = 'RecognitionFailed') AS FailedReceiptCount,
                (SELECT COALESCE(SUM(Total), 0) FROM bee_SpendBeeReceipt WHERE ProjectId = @ProjectId AND Status = 'Completed') AS ReceiptTotal,
                (SELECT COUNT(*) FROM bee_SpendBeeReceiptImage AS image INNER JOIN bee_SpendBeeReceipt AS receipt ON receipt.id = image.ReceiptId WHERE receipt.ProjectId = @ProjectId) AS ReceiptImageCount,
                (SELECT COUNT(*) FROM bee_SpendBeeReceiptLineItem AS item INNER JOIN bee_SpendBeeReceipt AS receipt ON receipt.id = item.ReceiptId WHERE receipt.ProjectId = @ProjectId) AS ReceiptLineItemCount,
                (SELECT COUNT(*) FROM bee_SpendBeeMerchantPhoto WHERE ProjectId = @ProjectId AND Status <> 'Deleted') AS MerchantPhotoCount,
                (SELECT COUNT(*) FROM bee_SpendBeeMerchantPhoto WHERE ProjectId = @ProjectId AND Status = 'Ready') AS ReadyMerchantPhotoCount,
                (SELECT COUNT(*) FROM bee_SpendBeeMerchantPhoto WHERE ProjectId = @ProjectId AND Status = 'ProcessingFailed') AS FailedMerchantPhotoCount;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SpendBeeDashboardSummary();
        }

        return new SpendBeeDashboardSummary(
            Convert.ToInt32(reader["UserCount"]),
            Convert.ToInt32(reader["ActiveUserCount"]),
            Convert.ToInt32(reader["DeviceCount"]),
            Convert.ToInt32(reader["RecentDeviceCount"]),
            Convert.ToInt32(reader["MerchantCount"]),
            Convert.ToInt32(reader["SyncedMerchantCount"]),
            Convert.ToInt32(reader["ReceiptCount"]),
            Convert.ToInt32(reader["CompletedReceiptCount"]),
            Convert.ToInt32(reader["FailedReceiptCount"]),
            reader.GetDecimal(reader.GetOrdinal("ReceiptTotal")),
            Convert.ToInt32(reader["ReceiptImageCount"]),
            Convert.ToInt32(reader["ReceiptLineItemCount"]),
            Convert.ToInt32(reader["MerchantPhotoCount"]),
            Convert.ToInt32(reader["ReadyMerchantPhotoCount"]),
            Convert.ToInt32(reader["FailedMerchantPhotoCount"]));
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

}

public sealed record CrmDashboardSummary(
    int IndustryCount = 0,
    int MerchantCount = 0,
    int ActiveMerchantCount = 0,
    int ChatbotCount = 0,
    int KnowledgeDocumentCount = 0,
    int ConversationCount = 0,
    long ConversationMessageCount = 0,
    long ConversationImageCount = 0,
    long TokenCount = 0,
    long UsageMessageCount = 0,
    long UsageImageCount = 0,
    decimal EstimatedCostUsd = 0);

public sealed record SpendBeeDashboardSummary(
    int UserCount = 0,
    int ActiveUserCount = 0,
    int DeviceCount = 0,
    int RecentDeviceCount = 0,
    int MerchantCount = 0,
    int SyncedMerchantCount = 0,
    int ReceiptCount = 0,
    int CompletedReceiptCount = 0,
    int FailedReceiptCount = 0,
    decimal ReceiptTotal = 0,
    int ReceiptImageCount = 0,
    int ReceiptLineItemCount = 0,
    int MerchantPhotoCount = 0,
    int ReadyMerchantPhotoCount = 0,
    int FailedMerchantPhotoCount = 0);
