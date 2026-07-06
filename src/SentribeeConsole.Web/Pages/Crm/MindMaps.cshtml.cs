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
    private static readonly string[] ParticipantColors =
    [
        "#7c3aed", "#dc2626", "#0891b2", "#16a34a", "#ea580c",
        "#c026d3", "#2563eb", "#65a30d", "#be123c", "#0f766e"
    ];

    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmMindMapRow> Maps { get; private set; } = [];

    public IReadOnlyList<CrmMindMapCandidate> Candidates { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
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
        await EnsureMindMapTablesAsync(connection, cancellationToken);
        await LoadMapsAsync(connection, cancellationToken);
        Candidates = await LoadCandidatesAsync(connection, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string? title,
        string? mapStatus,
        string[]? participantName,
        string[]? participantEmail,
        string[]? participantSourceType,
        long?[]? participantSourceId,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var normalizedTitle = NormalizeTitle(title);
        var names = participantName ?? Array.Empty<string>();
        var emails = participantEmail ?? Array.Empty<string>();
        var sourceTypes = participantSourceType ?? Array.Empty<string>();
        var sourceIds = participantSourceId ?? Array.Empty<long?>();
        if (!emails.Any(item => NormalizeEmail(item) is not null))
        {
            TempData["CrmMindMapsStatus"] = "Add at least one shared person before creating a mind map.";
            return RedirectToPage("/Crm/MindMaps");
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMindMapTablesAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO bee_CrmMindMap
                (ProjectId, MerchantId, Title, MapStatus, MapJson, ParticipantEmails, ShareToken, Status)
            VALUES
                (@ProjectId, @MerchantId, @Title, @MapStatus, @MapJson, '', @ShareToken, 'Active');
            SELECT LAST_INSERT_ID();
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@Title", MySqlDbType.VarChar, 180).Value = normalizedTitle;
        command.Parameters.Add("@MapStatus", MySqlDbType.VarChar, 40).Value = NormalizeMapStatus(mapStatus);
        command.Parameters.Add("@MapJson", MySqlDbType.LongText).Value = BuildDefaultMapJson(normalizedTitle);
        command.Parameters.Add("@ShareToken", MySqlDbType.VarChar, 80).Value = CreateToken();
        var mapId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

        await SaveParticipantsAsync(
            connection,
            transaction,
            Merchant.ProjectId,
            Merchant.Id,
            mapId,
            names,
            emails,
            sourceTypes,
            sourceIds,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        TempData["CrmMindMapsStatus"] = "Mind map created.";
        return RedirectToPage("/Crm/MindMapEditor", new { mapId });
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

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureMindMapTablesAsync(connection, cancellationToken);
        var map = await MindMapStore.LoadMapAsync(connection, Merchant.Id, mapId, cancellationToken);
        if (map is null)
        {
            TempData["CrmMindMapsStatus"] = "Mind map was not found.";
            return RedirectToPage("/Crm/MindMaps");
        }

        var participants = await MindMapStore.LoadParticipantsAsync(connection, Merchant.Id, mapId, cancellationToken);
        var targets = participantId.HasValue
            ? participants.Where(item => item.Id == participantId.Value).ToList()
            : participants.ToList();
        if (targets.Count == 0)
        {
            TempData["CrmMindMapsStatus"] = "No participant email found.";
            return RedirectToPage("/Crm/MindMaps");
        }

        var sent = 0;
        var failed = new List<string>();
        foreach (var participant in targets)
        {
            if (string.IsNullOrWhiteSpace(participant.Email))
            {
                continue;
            }

            var result = await emailService.SendMindMapInvitationAsync(
                participant.Email,
                Merchant.BusinessName,
                map.Title,
                BuildParticipantShareUrl(participant.InviteToken),
                cancellationToken);
            if (result.Success)
            {
                sent++;
                await MindMapStore.MarkParticipantInvitedAsync(connection, Merchant.Id, participant.Id, cancellationToken);
            }
            else
            {
                failed.Add($"{participant.Email}: {result.Message}");
            }
        }

        TempData["CrmMindMapsStatus"] = failed.Count == 0
            ? $"Invite sent to {sent} participant(s)."
            : $"Sent to {sent}, failed {failed.Count}: {string.Join("; ", failed.Take(2))}";
        return RedirectToPage("/Crm/MindMaps");
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
            SELECT map.id, map.Title, map.MapStatus, map.CreatedAtUtc, map.UpdatedAtUtc,
                COUNT(participant.id) AS SharedCount
            FROM bee_CrmMindMap AS map
            LEFT JOIN bee_CrmMindMapParticipant AS participant
              ON participant.MindMapId = map.id AND participant.Status = 'Active'
            WHERE map.MerchantId = @MerchantId AND map.Status = 'Active'
            GROUP BY map.id, map.Title, map.MapStatus, map.CreatedAtUtc, map.UpdatedAtUtc
            ORDER BY map.UpdatedAtUtc DESC, map.id DESC
            LIMIT 120;
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
                reader["MapStatus"] as string ?? "Draft",
                Convert.ToInt32(reader["SharedCount"]),
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))));
        }

        Maps = rows;
    }

    private async Task<IReadOnlyList<CrmMindMapCandidate>> LoadCandidatesAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 'Employee' AS SourceType, employee.id AS SourceId,
                COALESCE(NULLIF(employee.RealName, ''), NULLIF(employee.PreferredName, ''), employee.WorkEmail) AS DisplayName,
                employee.WorkEmail AS Email
            FROM bee_CrmEmployee AS employee
            WHERE employee.MerchantId = @MerchantId
              AND employee.Status = 'Active'
              AND employee.WorkEmail IS NOT NULL
              AND employee.WorkEmail <> ''
            UNION ALL
            SELECT 'Customer' AS SourceType, profile.id AS SourceId,
                COALESCE(NULLIF(profile.DisplayName, ''), NULLIF(profile.VisitorLabel, ''), profile.Email) AS DisplayName,
                profile.Email AS Email
            FROM bee_CrmCustomerProfile AS profile
            WHERE profile.MerchantId = @MerchantId
              AND profile.Email IS NOT NULL
              AND profile.Email <> ''
            ORDER BY DisplayName, Email
            LIMIT 200;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmMindMapCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmMindMapCandidate(
                reader["SourceType"] as string ?? "Manual",
                reader.GetInt64(reader.GetOrdinal("SourceId")),
                reader["DisplayName"] as string ?? string.Empty,
                reader["Email"] as string ?? string.Empty));
        }

        return rows;
    }

    internal static async Task EnsureMindMapTablesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var sql in MindMapStore.SchemaSql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureMapStatusColumnAsync(connection, cancellationToken);
    }

    private static async Task EnsureMapStatusColumnAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string existsSql = """
            SELECT COUNT(*)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'bee_CrmMindMap'
              AND COLUMN_NAME = 'MapStatus';
            """;
        await using (var command = new MySqlCommand(existsSql, connection))
        {
            var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (exists)
            {
                return;
            }
        }

        const string alterSql = "ALTER TABLE bee_CrmMindMap ADD COLUMN MapStatus VARCHAR(40) NOT NULL DEFAULT 'Draft' AFTER Title;";
        await using var alterCommand = new MySqlCommand(alterSql, connection);
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task SaveParticipantsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int projectId,
        long merchantId,
        long mapId,
        IReadOnlyList<string> names,
        IReadOnlyList<string> emails,
        IReadOnlyList<string> sourceTypes,
        IReadOnlyList<long?> sourceIds,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO bee_CrmMindMapParticipant
                (ProjectId, MerchantId, MindMapId, DisplayName, Email, SourceType, SourceId, InviteToken, ColorTag, Status)
            VALUES
                (@ProjectId, @MerchantId, @MindMapId, @DisplayName, @Email, @SourceType, @SourceId, @InviteToken, @ColorTag, 'Active')
            ON DUPLICATE KEY UPDATE
                DisplayName = VALUES(DisplayName),
                SourceType = VALUES(SourceType),
                SourceId = VALUES(SourceId),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < emails.Count; i++)
        {
            var email = NormalizeEmail(emails[i]);
            if (email is null || !seen.Add(email))
            {
                continue;
            }

            var name = i < names.Count && !string.IsNullOrWhiteSpace(names[i])
                ? names[i].Trim()
                : email;
            var sourceType = i < sourceTypes.Count && !string.IsNullOrWhiteSpace(sourceTypes[i])
                ? sourceTypes[i].Trim()
                : "Manual";
            var sourceId = i < sourceIds.Count ? sourceIds[i] : null;

            await using var command = new MySqlCommand(insertSql, connection, transaction);
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
            command.Parameters.Add("@MindMapId", MySqlDbType.Int64).Value = mapId;
            command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 160).Value = name;
            command.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = email;
            command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 40).Value = sourceType;
            command.Parameters.Add("@SourceId", MySqlDbType.Int64).Value = (object?)sourceId ?? DBNull.Value;
            command.Parameters.Add("@InviteToken", MySqlDbType.VarChar, 80).Value = CreateToken();
            command.Parameters.Add("@ColorTag", MySqlDbType.VarChar, 20).Value = ParticipantColors[i % ParticipantColors.Length];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await MindMapStore.RefreshParticipantEmailCacheAsync(connection, transaction, merchantId, mapId, cancellationToken);
    }

    internal static string BuildDefaultMapJson(string title)
    {
        var data = new
        {
            nodeData = new
            {
                id = CreateNodeId(),
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

    internal static string NormalizeTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title) ? "Product brainstorm" : title.Trim()[..Math.Min(title.Trim().Length, 180)];
    }

    internal static string NormalizeMapStatus(string? status)
    {
        return string.Equals(status, "Final", StringComparison.OrdinalIgnoreCase)
            ? "Final"
            : string.Equals(status, "InProgress", StringComparison.OrdinalIgnoreCase) ? "InProgress" : "Draft";
    }

    internal static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return new MailAddress(value.Trim()).Address;
        }
        catch (FormatException)
        {
            return null;
        }
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

    internal static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static string CreateNodeId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
}

public sealed record CrmMindMapRow(
    long Id,
    string Title,
    string MapStatus,
    int SharedCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CrmMindMapDetail(
    long Id,
    string Title,
    string MapStatus,
    string MapJson,
    string ShareToken,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastSentAtUtc);

public sealed record CrmMindMapCandidate(
    string SourceType,
    long SourceId,
    string DisplayName,
    string Email);

public sealed record CrmMindMapParticipant(
    long Id,
    long MindMapId,
    string DisplayName,
    string Email,
    string SourceType,
    long? SourceId,
    string InviteToken,
    string ColorTag,
    DateTime? LastSeenAtUtc,
    DateTime? LastInvitedAtUtc,
    string Status)
{
    public bool IsOnline => LastSeenAtUtc.HasValue && LastSeenAtUtc.Value >= DateTime.UtcNow.AddMinutes(-2);
};

public sealed record CrmMindMapActivity(
    long Id,
    long MindMapId,
    long? ParticipantId,
    string ActorName,
    string ActorEmail,
    string ColorTag,
    string? NodeId,
    string? NodeTopic,
    string Summary,
    DateTime CreatedAtUtc);

internal static class MindMapStore
{
    public static IReadOnlyList<string> SchemaSql { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS bee_CrmMindMap (
            id BIGINT NOT NULL AUTO_INCREMENT,
            ProjectId INT NOT NULL,
            MerchantId BIGINT NOT NULL,
            Title VARCHAR(180) NOT NULL,
            MapStatus VARCHAR(40) NOT NULL DEFAULT 'Draft',
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
        """,
        """
        CREATE TABLE IF NOT EXISTS bee_CrmMindMapParticipant (
            id BIGINT NOT NULL AUTO_INCREMENT,
            ProjectId INT NOT NULL,
            MerchantId BIGINT NOT NULL,
            MindMapId BIGINT NOT NULL,
            DisplayName VARCHAR(160) NOT NULL,
            Email VARCHAR(180) NOT NULL,
            SourceType VARCHAR(40) NOT NULL DEFAULT 'Manual',
            SourceId BIGINT NULL,
            InviteToken VARCHAR(80) NOT NULL,
            ColorTag VARCHAR(20) NOT NULL,
            LastSeenAtUtc DATETIME(6) NULL,
            LastInvitedAtUtc DATETIME(6) NULL,
            Status VARCHAR(40) NOT NULL DEFAULT 'Active',
            CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
            UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
            PRIMARY KEY (id),
            UNIQUE KEY UX_bee_CrmMindMapParticipant_Token (InviteToken),
            UNIQUE KEY UX_bee_CrmMindMapParticipant_Email (MindMapId, Email),
            KEY IX_bee_CrmMindMapParticipant_Map (MindMapId, Status),
            KEY IX_bee_CrmMindMapParticipant_Merchant (MerchantId, Status)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
        """,
        """
        CREATE TABLE IF NOT EXISTS bee_CrmMindMapActivity (
            id BIGINT NOT NULL AUTO_INCREMENT,
            MindMapId BIGINT NOT NULL,
            ParticipantId BIGINT NULL,
            ActorName VARCHAR(160) NOT NULL,
            ActorEmail VARCHAR(180) NOT NULL,
            ColorTag VARCHAR(20) NOT NULL,
            NodeId VARCHAR(120) NULL,
            NodeTopic VARCHAR(500) NULL,
            Summary VARCHAR(700) NOT NULL,
            CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
            PRIMARY KEY (id),
            KEY IX_bee_CrmMindMapActivity_Map (MindMapId, CreatedAtUtc)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
        """
    ];

    public static async Task<CrmMindMapDetail?> LoadMapAsync(
        MySqlConnection connection,
        long merchantId,
        long mapId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, Title, MapStatus, MapJson, ShareToken, CreatedAtUtc, UpdatedAtUtc, LastSentAtUtc
            FROM bee_CrmMindMap
            WHERE id = @MapId AND MerchantId = @MerchantId AND Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrmMindMapDetail(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["Title"] as string ?? string.Empty,
            reader["MapStatus"] as string ?? "Draft",
            reader["MapJson"] as string ?? string.Empty,
            reader["ShareToken"] as string ?? string.Empty,
            reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("LastSentAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastSentAtUtc")));
    }

    public static async Task<IReadOnlyList<CrmMindMapParticipant>> LoadParticipantsAsync(
        MySqlConnection connection,
        long merchantId,
        long mapId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, MindMapId, DisplayName, Email, SourceType, SourceId, InviteToken, ColorTag,
                LastSeenAtUtc, LastInvitedAtUtc, Status
            FROM bee_CrmMindMapParticipant
            WHERE MerchantId = @MerchantId AND MindMapId = @MapId AND Status = 'Active'
            ORDER BY DisplayName, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmMindMapParticipant>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmMindMapParticipant(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("MindMapId")),
                reader["DisplayName"] as string ?? string.Empty,
                reader["Email"] as string ?? string.Empty,
                reader["SourceType"] as string ?? "Manual",
                reader.IsDBNull(reader.GetOrdinal("SourceId")) ? null : reader.GetInt64(reader.GetOrdinal("SourceId")),
                reader["InviteToken"] as string ?? string.Empty,
                reader["ColorTag"] as string ?? "#7c3aed",
                reader.IsDBNull(reader.GetOrdinal("LastSeenAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastSeenAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("LastInvitedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastInvitedAtUtc")),
                reader["Status"] as string ?? "Active"));
        }

        return rows;
    }

    public static async Task<IReadOnlyList<CrmMindMapActivity>> LoadActivitiesAsync(
        MySqlConnection connection,
        long mapId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, MindMapId, ParticipantId, ActorName, ActorEmail, ColorTag, NodeId, NodeTopic, Summary, CreatedAtUtc
            FROM bee_CrmMindMapActivity
            WHERE MindMapId = @MapId
            ORDER BY CreatedAtUtc DESC, id DESC
            LIMIT 20;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmMindMapActivity>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmMindMapActivity(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("MindMapId")),
                reader.IsDBNull(reader.GetOrdinal("ParticipantId")) ? null : reader.GetInt64(reader.GetOrdinal("ParticipantId")),
                reader["ActorName"] as string ?? string.Empty,
                reader["ActorEmail"] as string ?? string.Empty,
                reader["ColorTag"] as string ?? "#7c3aed",
                reader["NodeId"] as string,
                reader["NodeTopic"] as string,
                reader["Summary"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))));
        }

        return rows;
    }

    public static async Task RefreshParticipantEmailCacheAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long merchantId,
        long mapId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_CrmMindMap
            SET ParticipantEmails = (
                SELECT GROUP_CONCAT(Email ORDER BY DisplayName SEPARATOR ', ')
                FROM bee_CrmMindMapParticipant
                WHERE MindMapId = @MapId AND Status = 'Active'
            )
            WHERE id = @MapId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@MapId", MySqlDbType.Int64).Value = mapId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task MarkParticipantInvitedAsync(
        MySqlConnection connection,
        long merchantId,
        long participantId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_CrmMindMapParticipant
            SET LastInvitedAtUtc = UTC_TIMESTAMP(6), UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ParticipantId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ParticipantId", MySqlDbType.Int64).Value = participantId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
