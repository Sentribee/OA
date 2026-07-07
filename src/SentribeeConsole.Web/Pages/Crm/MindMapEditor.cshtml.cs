using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

[RequestSizeLimit(6_000_000)]
public class MindMapEditorModel(
    IConfiguration configuration,
    IConsoleEmailService emailService) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public CrmMindMapDetail Map { get; private set; } = null!;

    public IReadOnlyList<CrmMindMapParticipant> Participants { get; private set; } = [];

    public IReadOnlyList<CrmMindMapActivity> Activities { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long mapId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmMindMapsStatus"] as string;

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await MindMapStore.LoadMapAsync(connection, Merchant.Id, mapId, cancellationToken);
        if (map is null)
        {
            return NotFound();
        }

        Map = map;
        Participants = await MindMapStore.LoadParticipantsAsync(connection, Merchant.Id, mapId, cancellationToken);
        Activities = await MindMapStore.LoadActivitiesAsync(connection, mapId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnGetDataAsync(long mapId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return new JsonResult(new { success = false, message = "Login required." }) { StatusCode = 401 };
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await MindMapStore.LoadMapAsync(connection, merchant.Id, mapId, cancellationToken);
        if (map is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        var participants = await MindMapStore.LoadParticipantsAsync(connection, merchant.Id, mapId, cancellationToken);
        var activities = await MindMapStore.LoadActivitiesAsync(connection, mapId, cancellationToken);
        return new JsonResult(new
        {
            success = true,
            title = map.Title,
            mapStatus = map.MapStatus,
            mapJson = map.MapJson,
            updatedAtUtc = map.UpdatedAtUtc.ToString("O"),
            participants = participants.Select(ToParticipantJson),
            activities = activities.Select(ToActivityJson)
        });
    }

    public async Task<IActionResult> OnPostSaveAsync(
        long mapId,
        string? title,
        string? mapStatus,
        string? mapJson,
        string? operationSummary,
        string? nodeId,
        string? nodeTopic,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return new JsonResult(new { success = false, message = "Login required." }) { StatusCode = 401 };
        }

        var normalizedTitle = MindMapsModel.NormalizeTitle(title);
        var normalizedJson = MindMapsModel.NormalizeMapJson(mapJson, normalizedTitle);
        if (normalizedJson is null)
        {
            return new JsonResult(new { success = false, message = "Mind map data is not valid JSON." }) { StatusCode = 400 };
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var existingMap = await MindMapStore.LoadMapAsync(connection, merchant.Id, mapId, cancellationToken);
        if (existingMap is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        var normalizedStatus = MindMapsModel.NormalizeMapStatus(mapStatus);
        if (string.Equals(existingMap.MapStatus, "Final", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedStatus, "Final", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(existingMap.MapJson, normalizedJson, StringComparison.Ordinal))
        {
            return new JsonResult(new
            {
                success = false,
                locked = true,
                message = "This mind map is final. Change status to In Progress before editing."
            })
            { StatusCode = 409 };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string updateSql = """
            UPDATE bee_CrmMindMap
            SET Title = @Title,
                MapStatus = @MapStatus,
                MapJson = @MapJson,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @MapId AND MerchantId = @MerchantId AND Status = 'Active';
            """;
        await using (var command = new MySqlCommand(updateSql, connection, transaction))
        {
            command.Parameters.Add("@Title", MySqlDbType.VarChar, 180).Value = normalizedTitle;
            command.Parameters.Add("@MapStatus", MySqlDbType.VarChar, 40).Value = normalizedStatus;
            command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = normalizedJson;
            command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchant.Id;
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
            }
        }

        await InsertActivityAsync(
            connection,
            transaction,
            mapId,
            null,
            merchant.ContactName ?? merchant.BusinessName,
            merchant.Email,
            "#111827",
            nodeId,
            nodeTopic,
            string.IsNullOrWhiteSpace(operationSummary) ? "Updated the mind map" : operationSummary.Trim(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        var participants = await MindMapStore.LoadParticipantsAsync(connection, merchant.Id, mapId, cancellationToken);
        if (!string.Equals(existingMap.MapStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase))
        {
            await NotifyStatusChangedAsync(merchant, normalizedTitle, normalizedStatus, participants, cancellationToken);
        }

        var now = DateTime.UtcNow;
        return new JsonResult(new
        {
            success = true,
            savedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            updatedAtUtc = now.ToString("O"),
            mapStatus = normalizedStatus
        });
    }

    public async Task<IActionResult> OnPostFinalEmailAsync(
        long mapId,
        string? imageDataUrl,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return new JsonResult(new { success = false, message = "Login required." }) { StatusCode = 401 };
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await MindMapStore.LoadMapAsync(connection, merchant.Id, mapId, cancellationToken);
        if (map is null)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        if (!string.Equals(map.MapStatus, "Final", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { success = false, message = "Set this mind map to Final before sending the final email." }) { StatusCode = 409 };
        }

        var participants = await MindMapStore.LoadParticipantsAsync(connection, merchant.Id, mapId, cancellationToken);
        if (participants.Count == 0)
        {
            return new JsonResult(new { success = false, message = "No shared people found." }) { StatusCode = 400 };
        }

        var outline = MindMapsModel.BuildOutline(map.MapJson, map.Title);
        var sent = 0;
        var failed = new List<string>();
        foreach (var participant in participants)
        {
            var result = await emailService.SendMindMapFinalAsync(
                participant.Email,
                merchant.BusinessName,
                map.Title,
                BuildParticipantShareUrl(participant.InviteToken),
                outline,
                NormalizeImageDataUrl(imageDataUrl),
                cancellationToken);
            if (result.Success)
            {
                sent++;
            }
            else
            {
                failed.Add(participant.Email);
            }
        }

        if (sent > 0)
        {
            await MindMapStore.MarkMapSentAsync(connection, merchant.Id, mapId, cancellationToken);
        }

        return new JsonResult(new
        {
            success = failed.Count == 0,
            sent,
            failed = failed.Count,
            message = failed.Count == 0
                ? $"Final email sent to {sent} participant(s)."
                : $"Final email sent to {sent}; failed for {failed.Count}."
        });
    }

    public async Task<IActionResult> OnPostInviteAsync(
        long mapId,
        long? participantId,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MindMapsModel.EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await MindMapStore.LoadMapAsync(connection, merchant.Id, mapId, cancellationToken);
        if (map is null)
        {
            return NotFound();
        }

        var participants = await MindMapStore.LoadParticipantsAsync(connection, merchant.Id, mapId, cancellationToken);
        var targets = participantId.HasValue
            ? participants.Where(item => item.Id == participantId.Value).ToList()
            : participants.ToList();
        var sent = 0;
        var failed = new List<string>();
        foreach (var participant in targets)
        {
            var result = await emailService.SendMindMapInvitationAsync(
                participant.Email,
                merchant.BusinessName,
                map.Title,
                BuildParticipantShareUrl(participant.InviteToken),
                cancellationToken);
            if (result.Success)
            {
                sent++;
                await MindMapStore.MarkParticipantInvitedAsync(connection, merchant.Id, participant.Id, cancellationToken);
            }
            else
            {
                failed.Add(participant.Email);
            }
        }

        TempData["CrmMindMapsStatus"] = failed.Count == 0
            ? $"Invite sent to {sent} participant(s)."
            : $"Sent to {sent}, failed for {string.Join(", ", failed.Take(3))}.";
        return RedirectToPage("/Crm/MindMapEditor", new { mapId });
    }

    internal static async Task InsertActivityAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long mapId,
        long? participantId,
        string actorName,
        string actorEmail,
        string colorTag,
        string? nodeId,
        string? nodeTopic,
        string summary,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmMindMapActivity
                (MindMapId, ParticipantId, ActorName, ActorEmail, ColorTag, NodeId, NodeTopic, Summary)
            VALUES
                (@MindMapId, @ParticipantId, @ActorName, @ActorEmail, @ColorTag, @NodeId, @NodeTopic, @Summary);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@MindMapId", MySqlDbType.Int64).Value = mapId;
        command.Parameters.Add("@ParticipantId", MySqlDbType.Int64).Value = (object?)participantId ?? DBNull.Value;
        command.Parameters.Add("@ActorName", MySqlDbType.VarChar, 160).Value = actorName[..Math.Min(actorName.Length, 160)];
        command.Parameters.Add("@ActorEmail", MySqlDbType.VarChar, 180).Value = actorEmail[..Math.Min(actorEmail.Length, 180)];
        command.Parameters.Add("@ColorTag", MySqlDbType.VarChar, 20).Value = colorTag;
        command.Parameters.Add("@NodeId", MySqlDbType.VarChar, 120).Value = string.IsNullOrWhiteSpace(nodeId) ? DBNull.Value : nodeId.Trim()[..Math.Min(nodeId.Trim().Length, 120)];
        command.Parameters.Add("@NodeTopic", MySqlDbType.VarChar, 500).Value = string.IsNullOrWhiteSpace(nodeTopic) ? DBNull.Value : nodeTopic.Trim()[..Math.Min(nodeTopic.Trim().Length, 500)];
        command.Parameters.Add("@Summary", MySqlDbType.VarChar, 700).Value = summary[..Math.Min(summary.Length, 700)];
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static object ToParticipantJson(CrmMindMapParticipant participant)
    {
        return new
        {
            participant.Id,
            participant.DisplayName,
            participant.Email,
            participant.ColorTag,
            isOnline = participant.IsOnline,
            lastSeenAtUtc = participant.LastSeenAtUtc?.ToString("O"),
            lastInvitedAtUtc = participant.LastInvitedAtUtc?.ToString("O")
        };
    }

    internal static object ToActivityJson(CrmMindMapActivity activity)
    {
        return new
        {
            activity.Id,
            activity.ParticipantId,
            activity.ActorName,
            activity.ActorEmail,
            activity.ColorTag,
            activity.NodeId,
            activity.NodeTopic,
            activity.Summary,
            createdAtUtc = activity.CreatedAtUtc.ToString("O")
        };
    }

    internal string BuildParticipantShareUrl(string inviteToken)
    {
        var scheme = Request.Scheme;
        var host = Request.Host.Value ?? string.Empty;
        if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{scheme}://{host}/oa/mindmaps/share/{Uri.EscapeDataString(inviteToken)}";
        }

        return $"https://oa.sentribee.ai/oa/mindmaps/share/{Uri.EscapeDataString(inviteToken)}";
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Mind Map Editor";
        ViewData["PageTitle"] = "Mind Map Editor";
        ViewData["ActiveMenu"] = "MindMaps";
    }

    private async Task NotifyStatusChangedAsync(
        CrmMerchantSession merchant,
        string mapTitle,
        string mapStatus,
        IReadOnlyList<CrmMindMapParticipant> participants,
        CancellationToken cancellationToken)
    {
        foreach (var participant in participants)
        {
            await emailService.SendMindMapStatusChangedAsync(
                participant.Email,
                merchant.BusinessName,
                mapTitle,
                mapStatus,
                BuildParticipantShareUrl(participant.InviteToken),
                cancellationToken);
        }
    }

    private static string? NormalizeImageDataUrl(string? imageDataUrl)
    {
        if (string.IsNullOrWhiteSpace(imageDataUrl) ||
            !imageDataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
            imageDataUrl.Length > 5_000_000)
        {
            return null;
        }

        return imageDataUrl;
    }
}
