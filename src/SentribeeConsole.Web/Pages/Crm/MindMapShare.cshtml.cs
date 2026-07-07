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

    public IReadOnlyList<CrmMindMapActivity> Activities { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string shareToken, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        Map = await LoadMapAsync(connection, shareToken, cancellationToken);
        if (Map is null)
        {
            return NotFound();
        }

        await TouchParticipantAsync(connection, Map.ParticipantId, cancellationToken);
        Activities = await MindMapStore.LoadActivitiesAsync(connection, Map.Id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync(string shareToken, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await LoadMapAsync(connection, shareToken, cancellationToken);
        if (map is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        await TouchParticipantAsync(connection, map.ParticipantId, cancellationToken);
        var activities = await MindMapStore.LoadActivitiesAsync(connection, map.Id, cancellationToken);
        return new JsonResult(new
        {
            success = true,
            mapJson = map.MapJson,
            mapStatus = map.MapStatus,
            updatedAtUtc = map.UpdatedAtUtc.ToString("O"),
            activities = activities.Select(MindMapEditorModel.ToActivityJson)
        });
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string shareToken,
        string? mapJson,
        string? operationSummary,
        string? nodeId,
        string? nodeTopic,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await LoadMapAsync(connection, shareToken, cancellationToken);
        if (map is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        if (string.Equals(map.MapStatus, "Final", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new
            {
                success = false,
                locked = true,
                message = "This mind map is final and cannot be edited."
            })
            { StatusCode = 409 };
        }

        var normalizedJson = MindMapsModel.NormalizeMapJson(mapJson, map.Title);
        if (normalizedJson is null)
        {
            return new JsonResult(new { success = false, message = "Mind map data is not valid JSON." }) { StatusCode = 400 };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE bee_CrmMindMap
            SET MapJson = @MapJson,
                MapStatus = 'InProgress',
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @MapId AND Status = 'Active';
            """;
        await using (var command = new MySqlCommand(sql, connection, transaction))
        {
            command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = normalizedJson;
            command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = map.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (map.ParticipantId.HasValue)
        {
            const string touchSql = """
                UPDATE bee_CrmMindMapParticipant
                SET LastSeenAtUtc = UTC_TIMESTAMP(6), UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @ParticipantId;
                """;
            await using var touchCommand = new MySqlCommand(touchSql, connection, transaction);
            touchCommand.Parameters.Add("@ParticipantId", MySqlDbType.Int64).Value = map.ParticipantId.Value;
            await touchCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await MindMapEditorModel.InsertActivityAsync(
            connection,
            transaction,
            map.Id,
            map.ParticipantId,
            map.ParticipantName,
            map.ParticipantEmail,
            map.ColorTag,
            nodeId,
            nodeTopic,
            string.IsNullOrWhiteSpace(operationSummary) ? "Updated the mind map" : operationSummary.Trim(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        var now = DateTime.UtcNow;
        return new JsonResult(new
        {
            success = true,
            savedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            updatedAtUtc = now.ToString("O")
        });
    }

    private static async Task<SharedMindMap?> LoadMapAsync(
        MySqlConnection connection,
        string shareToken,
        CancellationToken cancellationToken)
    {
        const string participantSql = """
            SELECT map.id, map.Title, map.MapStatus, map.MapJson, map.UpdatedAtUtc, merchant.BusinessName,
                participant.id AS ParticipantId, participant.DisplayName, participant.Email, participant.ColorTag
            FROM bee_CrmMindMapParticipant AS participant
            INNER JOIN bee_CrmMindMap AS map ON map.id = participant.MindMapId
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = map.MerchantId
            WHERE participant.InviteToken = @ShareToken
              AND participant.Status = 'Active'
              AND map.Status = 'Active'
            LIMIT 1;
            """;
        await using (var command = new MySqlCommand(participantSql, connection))
        {
            command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = shareToken;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSharedMap(reader);
            }
        }

        const string fallbackSql = """
            SELECT map.id, map.Title, map.MapStatus, map.MapJson, map.UpdatedAtUtc, merchant.BusinessName,
                NULL AS ParticipantId, 'Guest' AS DisplayName, '' AS Email, '#7c3aed' AS ColorTag
            FROM bee_CrmMindMap AS map
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = map.MerchantId
            WHERE map.ShareToken = @ShareToken AND map.Status = 'Active'
            LIMIT 1;
            """;
        await using (var command = new MySqlCommand(fallbackSql, connection))
        {
            command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = shareToken;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSharedMap(reader);
            }
        }

        return null;
    }

    private static SharedMindMap ReadSharedMap(MySqlDataReader reader)
    {
        return new SharedMindMap(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["Title"] as string ?? string.Empty,
            reader["MapStatus"] as string ?? "Draft",
            reader["MapJson"] as string ?? JsonSerializer.Serialize(new { }),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
            reader["BusinessName"] as string ?? "Sentribee OA",
            reader.IsDBNull(reader.GetOrdinal("ParticipantId")) ? null : reader.GetInt64(reader.GetOrdinal("ParticipantId")),
            reader["DisplayName"] as string ?? "Guest",
            reader["Email"] as string ?? string.Empty,
            reader["ColorTag"] as string ?? "#7c3aed");
    }

    private static async Task TouchParticipantAsync(
        MySqlConnection connection,
        long? participantId,
        CancellationToken cancellationToken)
    {
        if (!participantId.HasValue)
        {
            return;
        }

        const string sql = """
            UPDATE bee_CrmMindMapParticipant
            SET LastSeenAtUtc = UTC_TIMESTAMP(6), UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ParticipantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ParticipantId", MySqlDbType.Int64).Value = participantId.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record SharedMindMap(
    long Id,
    string Title,
    string MapStatus,
    string MapJson,
    DateTime UpdatedAtUtc,
    string BusinessName,
    long? ParticipantId,
    string ParticipantName,
    string ParticipantEmail,
    string ColorTag);
