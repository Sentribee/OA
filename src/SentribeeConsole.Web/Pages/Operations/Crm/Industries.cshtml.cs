using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.Crm;

[Authorize]
public class IndustriesModel(IConfiguration configuration, IProjectService projectService) : PageModel
{
    public Project Project { get; private set; } = new();

    public PagedResult<CrmIndustryRow> Industries { get; private set; } = new();

    public async Task OnGetAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ViewData["Title"] = "Sentribee OA Industries";
        ViewData["PageTitle"] = "Sentribee OA Industries";
        ViewData["ActiveMenu"] = "CrmIndustries";

        Project = await LoadCurrentProjectAsync(cancellationToken);
        const int pageSize = 20;
        pageNumber = Math.Max(1, pageNumber);

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string countSql = "SELECT COUNT(*) FROM bee_CrmIndustry WHERE ProjectId = @ProjectId;";
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT industry.id, industry.Name, industry.Slug, industry.Description,
                industry.SortOrder, industry.IsActive, industry.CreatedAtUtc, industry.UpdatedAtUtc,
                COUNT(merchant.id) AS MerchantCount,
                SUM(CASE WHEN merchant.Status = 'Active' THEN 1 ELSE 0 END) AS ActiveMerchantCount
            FROM bee_CrmIndustry AS industry
            LEFT JOIN bee_CrmMerchant AS merchant ON merchant.IndustryId = industry.id
            WHERE industry.ProjectId = @ProjectId
            GROUP BY industry.id, industry.Name, industry.Slug, industry.Description,
                industry.SortOrder, industry.IsActive, industry.CreatedAtUtc, industry.UpdatedAtUtc
            ORDER BY industry.SortOrder, industry.Name
            LIMIT @PageSize OFFSET @Offset;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Project.Id;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<CrmIndustryRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmIndustryRow(
                reader.GetInt32(reader.GetOrdinal("id")),
                reader["Name"] as string ?? string.Empty,
                reader["Slug"] as string ?? string.Empty,
                reader["Description"] as string,
                reader.GetInt32(reader.GetOrdinal("SortOrder")),
                reader.GetBoolean(reader.GetOrdinal("IsActive")),
                Convert.ToInt32(reader["MerchantCount"]),
                Convert.ToInt32(reader["ActiveMerchantCount"]),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))));
        }

        Industries = new PagedResult<CrmIndustryRow>
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

public sealed record CrmIndustryRow(
    int Id,
    string Name,
    string Slug,
    string? Description,
    int SortOrder,
    bool IsActive,
    int MerchantCount,
    int ActiveMerchantCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
