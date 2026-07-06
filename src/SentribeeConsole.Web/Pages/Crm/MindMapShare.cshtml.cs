using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class MindMapShareModel(IConfiguration configuration) : PageModel
{
    private string ConnectionString =>
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    public SharedMindMap? Map { get; private set; }

    public async Task<IActionResult> OnGetAsync(string shareToken, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTableAsync(connection, cancellationToken);
        Map = await LoadMapAsync(connection, shareToken, cancellationToken);
        return Map is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnGetDataAsync(string shareToken, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTableAsync(connection, cancellationToken);
        var map = await LoadMapAsync(connection, shareToken, cancellationToken);
        if (map is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        return new JsonResult(new
        {
            success = true,
            mapJson = map.MapJson,
            updatedAtUtc = map.UpdatedAtUtc.ToString("O")
        });
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string shareToken,
        string? mapJson,
        CancellationToken cancellationToken)
    {
        var normalizedJson = MindMapsModel.NormalizeMapJson(mapJson, "Product brainstorm");
        if (normalizedJson is null)
        {
            return new JsonResult(new { success = false, message = "Mind map data is not valid JSON." }) { StatusCode = 400 };
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTableAsync(connection, cancellationToken);
        const string sql = """
            UPDATE bee_CrmMindMap
            SET MapJson = @MapJson,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE ShareToken = @ShareToken AND Status = 'Active';
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = normalizedJson;
        command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = shareToken;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        return new JsonResult(new
        {
            success = true,
            savedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            updatedAtUtc = DateTime.UtcNow.ToString("O")
        });
    }

    private static async Task<SharedMindMap?> LoadMapAsync(
        MySqlConnection connection,
        string shareToken,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT map.Title, map.MapJson, map.UpdatedAtUtc, merchant.BusinessName
            FROM bee_CrmMindMap AS map
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = map.MerchantId
            WHERE map.ShareToken = @ShareToken AND map.Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = shareToken;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SharedMindMap(
            reader["Title"] as string ?? string.Empty,
            reader["MapJson"] as string ?? JsonSerializer.Serialize(new { }),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
            reader["BusinessName"] as string ?? "Sentribee OA");
    }
}

public sealed record SharedMindMap(
    string Title,
    string MapJson,
    DateTime UpdatedAtUtc,
    string BusinessName);
