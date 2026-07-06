using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class MindMapsModel(
    IConfiguration configuration,
    IConsoleEmailService emailService) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmMindMapRow> Maps { get; private set; } = [];

    public CrmMindMapDetail? SelectedMap { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(long? mapId, CancellationToken cancellationToken)
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
        await EnsureMindMapTableAsync(connection, cancellationToken);
        await LoadMapsAsync(connection, cancellationToken);
        var selectedMapId = mapId ?? Maps.FirstOrDefault()?.Id;
        if (selectedMapId.HasValue)
        {
            SelectedMap = await LoadMapAsync(connection, selectedMapId.Value, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? title, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var normalizedTitle = NormalizeTitle(title);
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMindMapTableAsync(connection, cancellationToken);
        const string sql = """
            INSERT INTO bee_CrmMindMap
                (ProjectId, MerchantId, Title, MapJson, ParticipantEmails, ShareToken, Status)
            VALUES
                (@ProjectId, @MerchantId, @Title, @MapJson, '', @ShareToken, 'Active');
            SELECT LAST_INSERT_ID();
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@Title", MySqlDbType.VarChar, 180).Value = normalizedTitle;
        command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = BuildDefaultMapJson(normalizedTitle);
        command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = CreateShareToken();
        var mapId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        TempData["CrmMindMapsStatus"] = "Mind map created.";
        return RedirectToPage("/Crm/MindMaps", new { mapId });
    }

    public async Task<IActionResult> OnPostSaveAsync(
        long mapId,
        string? title,
        string? mapJson,
        string? participantEmails,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return new JsonResult(new { success = false, message = "Unauthorized." }) { StatusCode = 401 };
        }

        Merchant = merchant;
        var normalizedJson = NormalizeMapJson(mapJson, NormalizeTitle(title));
        if (normalizedJson is null)
        {
            return new JsonResult(new { success = false, message = "Mind map data is not valid JSON." }) { StatusCode = 400 };
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMindMapTableAsync(connection, cancellationToken);
        const string sql = """
            UPDATE bee_CrmMindMap
            SET Title = @Title,
                MapJson = @MapJson,
                ParticipantEmails = @ParticipantEmails,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @MapId AND MerchantId = @MerchantId AND Status = 'Active';
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Title", MySqlDbType.VarChar, 180).Value = NormalizeTitle(title);
        command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = normalizedJson;
        command.Parameters.Add("@ParticipantEmails", MySqlDbType.Text).Value = DbValue(participantEmails);
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return new JsonResult(new { success = false, message = "Mind map was not found." }) { StatusCode = 404 };
        }

        return new JsonResult(new { success = true, savedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    public async Task<IActionResult> OnPostEmailAsync(long mapId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMindMapTableAsync(connection, cancellationToken);
        var map = await LoadMapAsync(connection, mapId, cancellationToken);
        if (map is null)
        {
            TempData["CrmMindMapsStatus"] = "Mind map was not found.";
            return RedirectToPage("/Crm/MindMaps");
        }

        var recipients = ParseEmails(map.ParticipantEmails);
        if (recipients.Count == 0)
        {
            TempData["CrmMindMapsStatus"] = "Add at least one participant email before sending.";
            return RedirectToPage("/Crm/MindMaps", new { mapId });
        }

        var outline = BuildOutline(map.MapJson, map.Title);
        var shareUrl = BuildShareUrl(map.ShareToken);
        var sent = 0;
        var failed = new List<string>();
        foreach (var recipient in recipients)
        {
            var result = await emailService.SendMindMapSummaryAsync(
                recipient,
                Merchant.BusinessName,
                map.Title,
                shareUrl,
                outline,
                cancellationToken);
            if (result.Success)
            {
                sent++;
            }
            else
            {
                failed.Add($"{recipient}: {result.Message}");
            }
        }

        if (sent > 0)
        {
            const string updateSql = """
                UPDATE bee_CrmMindMap
                SET LastSentAtUtc = UTC_TIMESTAMP(6),
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @MapId AND MerchantId = @MerchantId;
                """;
            await using var updateCommand = new MySqlCommand(updateSql, connection);
            updateCommand.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
            updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        TempData["CrmMindMapsStatus"] = failed.Count == 0
            ? $"Mind map emailed to {sent} participant(s)."
            : $"Sent to {sent}, failed {failed.Count}: {string.Join("; ", failed.Take(2))}";
        return RedirectToPage("/Crm/MindMaps", new { mapId });
    }

    public string BuildShareUrl(string shareToken)
    {
        var scheme = Request.Scheme;
        var host = Request.Host.Value ?? string.Empty;
        if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{scheme}://{host}/oa/mindmaps/share/{Uri.EscapeDataString(shareToken)}";
        }

        return $"https://oa.sentribee.ai/oa/mindmaps/share/{Uri.EscapeDataString(shareToken)}";
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Mind Maps";
        ViewData["PageTitle"] = "Mind Maps";
        ViewData["ActiveMenu"] = "MindMaps";
    }

    private async Task LoadMapsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, Title, ParticipantEmails, UpdatedAtUtc, LastSentAtUtc
            FROM bee_CrmMindMap
            WHERE MerchantId = @MerchantId AND Status = 'Active'
            ORDER BY UpdatedAtUtc DESC, id DESC
            LIMIT 80;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmMindMapRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmMindMapRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["Title"] as string ?? string.Empty,
                reader["ParticipantEmails"] as string,
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastSentAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastSentAtUtc"))));
        }

        Maps = rows;
    }

    private async Task<CrmMindMapDetail?> LoadMapAsync(
        MySqlConnection connection,
        long mapId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, Title, MapJson, ParticipantEmails, ShareToken, UpdatedAtUtc, LastSentAtUtc
            FROM bee_CrmMindMap
            WHERE id = @MapId AND MerchantId = @MerchantId AND Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmMindMapDetail(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["Title"] as string ?? string.Empty,
            reader["MapJson"] as string ?? string.Empty,
            reader["ParticipantEmails"] as string,
            reader["ShareToken"] as string ?? string.Empty,
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("LastSentAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastSentAtUtc")));
    }

    internal static async Task EnsureMindMapTableAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS bee_CrmMindMap (
                id BIGINT NOT NULL AUTO_INCREMENT,
                ProjectId INT NOT NULL,
                MerchantId BIGINT NOT NULL,
                Title VARCHAR(180) NOT NULL,
                MapJson LONGTEXT NOT NULL,
                ParticipantEmails TEXT NULL,
                ShareToken VARCHAR(80) NOT NULL,
                Status VARCHAR(40) NOT NULL DEFAULT 'Active',
                LastSentAtUtc DATETIME(6) NULL,
                CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
                UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
                PRIMARY KEY (id),
                UNIQUE KEY UX_bee_CrmMindMap_ShareToken (ShareToken),
                KEY IX_bee_CrmMindMap_Merchant (MerchantId, Status, UpdatedAtUtc),
                KEY IX_bee_CrmMindMap_Project (ProjectId)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string BuildDefaultMapJson(string title)
    {
        var rootId = CreateNodeId();
        var data = new
        {
            nodeData = new
            {
                id = rootId,
                topic = title,
                root = true,
                children = new[]
                {
                    new { id = CreateNodeId(), topic = "Customer problem", children = Array.Empty<object>() },
                    new { id = CreateNodeId(), topic = "Solution ideas", children = Array.Empty<object>() },
                    new { id = CreateNodeId(), topic = "Risks and assumptions", children = Array.Empty<object>() },
                    new { id = CreateNodeId(), topic = "Next actions", children = Array.Empty<object>() }
                }
            }
        };
        return JsonSerializer.Serialize(data);
    }

    internal static string? NormalizeMapJson(string? mapJson, string title)
    {
        if (string.IsNullOrWhiteSpace(mapJson))
        {
            return BuildDefaultMapJson(title);
        }

        try
        {
            using var _ = JsonDocument.Parse(mapJson);
            return mapJson;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildOutline(string mapJson, string title)
    {
        try
        {
            using var document = JsonDocument.Parse(mapJson);
            if (!document.RootElement.TryGetProperty("nodeData", out var root))
            {
                return title;
            }

            var builder = new StringBuilder();
            AppendNode(builder, root, 0);
            return builder.ToString().Trim();
        }
        catch (JsonException)
        {
            return title;
        }
    }

    private static void AppendNode(StringBuilder builder, JsonElement node, int depth)
    {
        var topic = node.TryGetProperty("topic", out var topicElement)
            ? topicElement.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(topic))
        {
            builder.Append(' ', depth * 2);
            builder.Append("- ");
            builder.AppendLine(topic.Trim());
        }

        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            AppendNode(builder, child, depth + 1);
        }
    }

    private static string NormalizeTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title) ? "Product brainstorm" : title.Trim()[..Math.Min(title.Trim().Length, 180)];
    }

    private static IReadOnlyList<string> ParseEmails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var emails = new List<string>();
        foreach (var part in value.Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var address = new MailAddress(part);
                if (!emails.Contains(address.Address, StringComparer.OrdinalIgnoreCase))
                {
                    emails.Add(address.Address);
                }
            }
            catch (FormatException)
            {
                continue;
            }
        }

        return emails;
    }

    private static string CreateShareToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string CreateNodeId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed record CrmMindMapRow(
    long Id,
    string Title,
    string? ParticipantEmails,
    DateTime UpdatedAtUtc,
    DateTime? LastSentAtUtc);

public sealed record CrmMindMapDetail(
    long Id,
    string Title,
    string MapJson,
    string? ParticipantEmails,
    string ShareToken,
    DateTime UpdatedAtUtc,
    DateTime? LastSentAtUtc);
