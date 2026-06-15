using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.Crm;

[Authorize]
public class UsageModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<CrmUsageRow> UsageRows { get; private set; } = new();

    public CrmUsageTotals Totals { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "Sentribee OA Usage";
        ViewData["PageTitle"] = "Sentribee OA Usage";
        ViewData["ActiveMenu"] = "CrmUsage";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        const int pageSize = 30;
        pageNumber = Math.Max(1, pageNumber);

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string totalSql = """
            SELECT COUNT(*) AS UsageLineCount,
                COALESCE(SUM(PromptTokens), 0) AS PromptTokens,
                COALESCE(SUM(CompletionTokens), 0) AS CompletionTokens,
                COALESCE(SUM(ImageCount), 0) AS ImageCount,
                COALESCE(SUM(ConversationCount), 0) AS ConversationCount,
                COALESCE(SUM(MessageCount), 0) AS MessageCount,
                COALESCE(SUM(EstimatedCostUsd), 0) AS EstimatedCostUsd
            FROM bee_CrmUsageDaily
            WHERE ProjectId = @ProjectId;
            """;
        await using (var totalCommand = new MySqlCommand(totalSql, connection))
        {
            totalCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
            await using var totalReader = await totalCommand.ExecuteReaderAsync(cancellationToken);
            if (await totalReader.ReadAsync(cancellationToken))
            {
                Totals = new CrmUsageTotals(
                    Convert.ToInt32(totalReader["UsageLineCount"]),
                    Convert.ToInt64(totalReader["PromptTokens"]),
                    Convert.ToInt64(totalReader["CompletionTokens"]),
                    Convert.ToInt64(totalReader["ImageCount"]),
                    Convert.ToInt64(totalReader["ConversationCount"]),
                    Convert.ToInt64(totalReader["MessageCount"]),
                    totalReader.GetDecimal(totalReader.GetOrdinal("EstimatedCostUsd")));
            }
        }

        const string countSql = "SELECT COUNT(*) FROM bee_CrmUsageDaily WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT usageRow.id, usageRow.UsageDate, usageRow.ModelName,
                usageRow.PromptTokens, usageRow.CompletionTokens, usageRow.ImageCount,
                usageRow.ConversationCount, usageRow.MessageCount, usageRow.EstimatedCostUsd,
                merchant.BusinessName, merchant.CorpId
            FROM bee_CrmUsageDaily AS usageRow
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = usageRow.MerchantId
            WHERE usageRow.ProjectId = @ProjectId
            ORDER BY usageRow.UsageDate DESC, usageRow.UpdatedAtUtc DESC, usageRow.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<CrmUsageRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmUsageRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetDateTime(reader.GetOrdinal("UsageDate")),
                reader["BusinessName"] as string ?? string.Empty,
                reader["CorpId"] as string ?? string.Empty,
                reader["ModelName"] as string ?? string.Empty,
                reader.GetInt64(reader.GetOrdinal("PromptTokens")),
                reader.GetInt64(reader.GetOrdinal("CompletionTokens")),
                reader.GetInt32(reader.GetOrdinal("ImageCount")),
                reader.GetInt32(reader.GetOrdinal("ConversationCount")),
                reader.GetInt32(reader.GetOrdinal("MessageCount")),
                reader.GetDecimal(reader.GetOrdinal("EstimatedCostUsd"))));
        }

        UsageRows = new PagedResult<CrmUsageRow>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
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

public sealed record CrmUsageRow(
    long Id,
    DateTime UsageDate,
    string BusinessName,
    string CorpId,
    string ModelName,
    long PromptTokens,
    long CompletionTokens,
    int ImageCount,
    int ConversationCount,
    int MessageCount,
    decimal EstimatedCostUsd)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record CrmUsageTotals(
    int UsageLineCount = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long ImageCount = 0,
    long ConversationCount = 0,
    long MessageCount = 0,
    decimal EstimatedCostUsd = 0)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}
