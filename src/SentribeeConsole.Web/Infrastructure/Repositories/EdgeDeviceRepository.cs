using System.Data;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Repositories;

public sealed class EdgeDeviceRepository(IConfiguration configuration) : IEdgeDeviceRepository
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");

    public async Task<IReadOnlyList<DeviceCatalogItem>> ListCatalogAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, CatalogName, Description
            FROM bee_DeviceCatalog
            WHERE IsActive = 1
            ORDER BY SortOrder, CatalogName;
            """;
        var items = new List<DeviceCatalogItem>();
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DeviceCatalogItem
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                Name = reader["CatalogName"] as string ?? string.Empty,
                Description = reader["Description"] as string
            });
        }

        return items;
    }

    public async Task<PagedResult<EdgeDevice>> ListByAdminAsync(
        int adminId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT device.id, device.ProjectId, device.AdminId, device.DeviceCode, device.DeviceName, device.Address,
                device.Latitude, device.Longitude, device.GooglePlaceId, device.StreetViewThumbnailUrl,
                device.IpAddress, device.ServerResourceInstanceName, device.Description, device.CreatedAtUtc, device.UpdatedAtUtc,
                heartbeat.RuntimeStatus, heartbeat.DeviceStatus, heartbeat.DetailJson AS HeartbeatDetailJson,
                heartbeat.ReportedAtUtc AS LastHeartbeatAtUtc
            FROM bee_EdgeDevice AS device
            LEFT JOIN (
                SELECT latest.ProjectId, latest.EdgeDeviceId, latest.RuntimeStatus, latest.DeviceStatus,
                    latest.DetailJson, latest.ReportedAtUtc
                FROM bee_EdgeAiHeartbeat AS latest
                INNER JOIN (
                    SELECT EdgeDeviceId, MAX(id) AS LatestHeartbeatId
                    FROM bee_EdgeAiHeartbeat
                    GROUP BY EdgeDeviceId
                ) AS grouped ON grouped.LatestHeartbeatId = latest.id
            ) AS heartbeat ON heartbeat.EdgeDeviceId = device.id
            WHERE device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
            ORDER BY device.CreatedAtUtc DESC, device.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        const string countSql = """
            SELECT COUNT(*)
            FROM bee_EdgeDevice AS device
            WHERE device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            );
            """;
        var devices = new List<EdgeDevice>();
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(MapDevice(reader));
        }

        await reader.CloseAsync();
        var items = await AttachEndpointCountsAsync(connection, devices, cancellationToken);
        await using var countCommand = new MySqlCommand(countSql, connection);
        countCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new PagedResult<EdgeDevice>
        {
            Items = items,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<EdgeDevice?> FindByAdminAsync(int adminId, int deviceId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT device.id, device.ProjectId, device.AdminId, device.DeviceCode, device.DeviceName, device.Address,
                device.Latitude, device.Longitude, device.GooglePlaceId, device.StreetViewThumbnailUrl,
                device.IpAddress, device.ServerResourceInstanceName, device.Description, device.CreatedAtUtc, device.UpdatedAtUtc,
                heartbeat.RuntimeStatus, heartbeat.DeviceStatus, heartbeat.DetailJson AS HeartbeatDetailJson,
                heartbeat.ReportedAtUtc AS LastHeartbeatAtUtc
            FROM bee_EdgeDevice AS device
            LEFT JOIN (
                SELECT latest.ProjectId, latest.EdgeDeviceId, latest.RuntimeStatus, latest.DeviceStatus,
                    latest.DetailJson, latest.ReportedAtUtc
                FROM bee_EdgeAiHeartbeat AS latest
                INNER JOIN (
                    SELECT EdgeDeviceId, MAX(id) AS LatestHeartbeatId
                    FROM bee_EdgeAiHeartbeat
                    GROUP BY EdgeDeviceId
                ) AS grouped ON grouped.LatestHeartbeatId = latest.id
            ) AS heartbeat ON heartbeat.EdgeDeviceId = device.id
            WHERE device.id = @DeviceId
              AND device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
              )
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = deviceId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var device = MapDevice(reader);
        await reader.CloseAsync();
        return device with
        {
            Endpoints = await LoadEndpointsAsync(connection, device.Id, cancellationToken),
            Events = await LoadEventsByDeviceAsync(connection, adminId, device.Id, cancellationToken)
        };
    }

    public async Task<EdgeDevice> CreateAsync(
        int adminId,
        int projectId,
        string name,
        string address,
        decimal? latitude,
        decimal? longitude,
        string? googlePlaceId,
        string? streetViewThumbnailUrl,
        string ipAddress,
        string serverResourceInstanceName,
        string? description,
        IReadOnlyList<EdgeDeviceEndpointInput> endpoints,
        CancellationToken cancellationToken)
    {
        const string deviceSql = """
            INSERT INTO bee_EdgeDevice
                (ProjectId, AdminId, DeviceCode, DeviceName, Address, Latitude, Longitude,
                 GooglePlaceId, StreetViewThumbnailUrl, IpAddress, ServerResourceInstanceName, Description)
            VALUES (@ProjectId, @AdminId, @DeviceCode, @DeviceName, @Address, @Latitude, @Longitude,
                @GooglePlaceId, @StreetViewThumbnailUrl, @IpAddress, @ServerResourceInstanceName, @Description);
            """;
        const string endpointSql = """
            INSERT INTO bee_EdgeDeviceEndpoint
                (EdgeDeviceId, CatalogDeviceId, DeviceName, AccessUrl)
            VALUES (@EdgeDeviceId, @CatalogDeviceId, @DeviceName, @AccessUrl);
            """;

        var code = $"EDGE-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..23].ToUpperInvariant();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand(deviceSql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = code;
        command.Parameters.Add("@DeviceName", MySqlDbType.VarChar, 150).Value = name;
        command.Parameters.Add("@Address", MySqlDbType.VarChar, 300).Value = address;
        command.Parameters.Add("@Latitude", MySqlDbType.Decimal).Value = (object?)latitude ?? DBNull.Value;
        command.Parameters.Add("@Longitude", MySqlDbType.Decimal).Value = (object?)longitude ?? DBNull.Value;
        command.Parameters.Add("@GooglePlaceId", MySqlDbType.VarChar, 200).Value = (object?)googlePlaceId ?? DBNull.Value;
        command.Parameters.Add("@StreetViewThumbnailUrl", MySqlDbType.VarChar, 1000).Value =
            (object?)streetViewThumbnailUrl ?? DBNull.Value;
        command.Parameters.Add("@IpAddress", MySqlDbType.VarChar, 45).Value = ipAddress;
        command.Parameters.Add("@ServerResourceInstanceName", MySqlDbType.VarChar, 80).Value = serverResourceInstanceName;
        command.Parameters.Add("@Description", MySqlDbType.Text).Value = (object?)description ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var deviceId = Convert.ToInt32(command.LastInsertedId);

        foreach (var endpoint in endpoints)
        {
            await using var endpointCommand = new MySqlCommand(endpointSql, connection, transaction);
            endpointCommand.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = deviceId;
            endpointCommand.Parameters.Add("@CatalogDeviceId", MySqlDbType.Int32).Value =
                (object?)endpoint.CatalogDeviceId ?? DBNull.Value;
            endpointCommand.Parameters.Add("@DeviceName", MySqlDbType.VarChar, 150).Value = endpoint.DeviceName;
            endpointCommand.Parameters.Add("@AccessUrl", MySqlDbType.VarChar, 500).Value = endpoint.AccessUrl;
            await endpointCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await FindByAdminAsync(adminId, deviceId, cancellationToken)
            ?? throw new InvalidOperationException("Unable to reload the edge device.");
    }

    public async Task<bool> UpdateProfileAsync(
        int adminId,
        int deviceId,
        string name,
        string serverResourceInstanceName,
        string? description,
        IReadOnlyList<EdgeDeviceEndpointInput> endpoints,
        CancellationToken cancellationToken)
    {
        const string deviceSql = """
            UPDATE bee_EdgeDevice
            SET DeviceName = @DeviceName,
                ServerResourceInstanceName = @ServerResourceInstanceName,
                Description = @Description,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @DeviceId
              AND ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.Role = 'Administrator'
              );
            """;
        const string deleteEndpointSql = """
            DELETE FROM bee_EdgeDeviceEndpoint
            WHERE EdgeDeviceId = @DeviceId;
            """;
        const string endpointSql = """
            INSERT INTO bee_EdgeDeviceEndpoint
                (EdgeDeviceId, CatalogDeviceId, DeviceName, AccessUrl)
            VALUES (@EdgeDeviceId, @CatalogDeviceId, @DeviceName, @AccessUrl);
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand(deviceSql, connection, transaction);
        command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = deviceId;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@DeviceName", MySqlDbType.VarChar, 150).Value = name;
        command.Parameters.Add("@ServerResourceInstanceName", MySqlDbType.VarChar, 80).Value = serverResourceInstanceName;
        command.Parameters.Add("@Description", MySqlDbType.Text).Value = (object?)description ?? DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var deleteCommand = new MySqlCommand(deleteEndpointSql, connection, transaction))
        {
            deleteCommand.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = deviceId;
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var endpoint in endpoints)
        {
            await using var endpointCommand = new MySqlCommand(endpointSql, connection, transaction);
            endpointCommand.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = deviceId;
            endpointCommand.Parameters.Add("@CatalogDeviceId", MySqlDbType.Int32).Value =
                (object?)endpoint.CatalogDeviceId ?? DBNull.Value;
            endpointCommand.Parameters.Add("@DeviceName", MySqlDbType.VarChar, 150).Value = endpoint.DeviceName;
            endpointCommand.Parameters.Add("@AccessUrl", MySqlDbType.VarChar, 500).Value = endpoint.AccessUrl;
            await endpointCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(int adminId, int deviceId, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM bee_EdgeDevice
            WHERE id = @DeviceId
              AND ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.Role = 'Administrator'
              );
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = deviceId;
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<PagedResult<EdgeEvent>> ListEventsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadEventsPageAsync(connection, adminId, filters, pageNumber, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<EdgeDevice>> ListEventDevicesByAdminAsync(
        int adminId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT device.id, device.ProjectId, device.AdminId, device.DeviceCode, device.DeviceName,
                device.Address, device.Latitude, device.Longitude, device.GooglePlaceId, device.StreetViewThumbnailUrl,
                device.IpAddress, device.ServerResourceInstanceName, device.Description, device.CreatedAtUtc, device.UpdatedAtUtc,
                NULL AS RuntimeStatus, NULL AS DeviceStatus, NULL AS HeartbeatDetailJson, NULL AS LastHeartbeatAtUtc
            FROM bee_EdgeDevice AS device
            INNER JOIN bee_EdgeEvent AS evt ON evt.EdgeDeviceId = device.id
            WHERE device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
            ORDER BY device.DeviceName;
            """;
        var devices = new List<EdgeDevice>();
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(MapDevice(reader));
        }

        return devices;
    }

    public async Task<PagedResult<EdgeEventSubject>> ListEventSubjectsByAdminAsync(
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = BuildEventSubjectWhere(filters, includeStatus: true);
        var sql = $"""
            SELECT subject.id, subject.EdgeEventId, evt.EdgeDeviceId, device.DeviceName, device.DeviceCode,
                evt.Title, evt.Status, COALESCE(evt.LearningStatus, 'None') AS EventLearningStatus,
                COALESCE(subject.LearningStatus, 'None') AS LearningStatus, evt.EventTimeUtc,
                subject.SubjectKey, subject.SubjectType, subject.TrackingLabel, subject.CropImageUrl, subject.PreviewImageUrl,
                subject.BoundingBoxJson, subject.PpeBoxJson, subject.PpeStatusJson,
                subject.IsRisk, subject.RiskCategory, subject.RiskSeverity, subject.RiskReason
            FROM bee_EdgeEventSubject AS subject
            INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where}
            ORDER BY evt.EventTimeUtc DESC, evt.id DESC, subject.SubjectKey, subject.id
            LIMIT @PageSize OFFSET @Offset;
            """;
        var countSql = $"""
            SELECT COUNT(*)
            FROM bee_EdgeEventSubject AS subject
            INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where};
            """;

        var subjects = new List<EdgeEventSubject>();
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        AddEventFilterParameters(command, adminId, filters, includeStatus: true);
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            subjects.Add(MapEventSubject(reader));
        }

        await reader.CloseAsync();
        await using var countCommand = new MySqlCommand(countSql, connection);
        AddEventFilterParameters(countCommand, adminId, filters, includeStatus: true);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new PagedResult<EdgeEventSubject>
        {
            Items = subjects,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<EdgeEventStatusCounts> GetEventStatusCountsAsync(
        int adminId,
        EdgeEventFilters filters,
        CancellationToken cancellationToken)
    {
        var where = BuildEventWhere(filters, includeStatus: false);
        var sql = $"""
            SELECT evt.Status, COALESCE(evt.LearningStatus, 'None') AS LearningStatus, COUNT(*) AS TotalCount
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where}
            GROUP BY evt.Status, COALESCE(evt.LearningStatus, 'None');
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        AddEventFilterParameters(command, adminId, filters, includeStatus: false);
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var realRisk = 0;
        var invalidEvent = 0;
        var severeDanger = 0;
        var ordinaryRisk = 0;
        var noRisk = 0;
        var pendingReview = 0;
        var pendingLearning = 0;
        var trained = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader["Status"] as string ?? string.Empty;
            var learningStatus = reader["LearningStatus"] as string ?? string.Empty;
            var count = Convert.ToInt32(reader["TotalCount"]);
            if (status.Equals("Invalid Event", StringComparison.OrdinalIgnoreCase))
            {
                invalidEvent += count;
            }
            else if (status.Equals("Severe Danger", StringComparison.OrdinalIgnoreCase))
            {
                severeDanger += count;
                realRisk += count;
            }
            else if (status.Equals("Ordinary Risk", StringComparison.OrdinalIgnoreCase))
            {
                ordinaryRisk += count;
                realRisk += count;
            }
            else if (status.Equals("No Risk", StringComparison.OrdinalIgnoreCase))
            {
                noRisk += count;
            }
            else if (status.Equals("Real Risk", StringComparison.OrdinalIgnoreCase))
            {
                realRisk += count;
                ordinaryRisk += count;
            }
            else if (status.Equals("Pending Review", StringComparison.OrdinalIgnoreCase))
            {
                pendingReview += count;
            }

            if (learningStatus.Equals("Pending Learning", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Pending Learning", StringComparison.OrdinalIgnoreCase))
            {
                pendingLearning += count;
            }
            else if (learningStatus.Equals("Trained", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Trained", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                trained += count;
            }
        }

        return new EdgeEventStatusCounts
        {
            InvalidEvent = invalidEvent,
            SevereDanger = severeDanger,
            OrdinaryRisk = ordinaryRisk,
            NoRisk = noRisk,
            RealRisk = realRisk,
            PendingReview = pendingReview,
            PendingLearning = pendingLearning,
            Trained = trained
        };
    }

    public async Task<IReadOnlyList<EdgeEvent>> ListEventsByDeviceAsync(
        int adminId,
        int deviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadEventsByDeviceAsync(connection, adminId, deviceId, cancellationToken);
    }

    private static async Task<IReadOnlyList<EdgeDevice>> AttachEndpointCountsAsync(
        MySqlConnection connection,
        IReadOnlyList<EdgeDevice> devices,
        CancellationToken cancellationToken)
    {
        var result = new List<EdgeDevice>(devices.Count);
        foreach (var device in devices)
        {
            result.Add(device with { Endpoints = await LoadEndpointsAsync(connection, device.Id, cancellationToken) });
        }

        return result;
    }

    private static async Task<IReadOnlyList<EdgeDeviceEndpoint>> LoadEndpointsAsync(
        MySqlConnection connection,
        int deviceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, EdgeDeviceId, CatalogDeviceId, DeviceName, AccessUrl, CreatedAtUtc
            FROM bee_EdgeDeviceEndpoint
            WHERE EdgeDeviceId = @DeviceId
            ORDER BY id;
            """;
        var endpoints = new List<EdgeDeviceEndpoint>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            endpoints.Add(new EdgeDeviceEndpoint
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                EdgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
                CatalogDeviceId = reader.IsDBNull(reader.GetOrdinal("CatalogDeviceId"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("CatalogDeviceId")),
                DeviceName = reader["DeviceName"] as string ?? string.Empty,
                AccessUrl = reader["AccessUrl"] as string ?? string.Empty,
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            });
        }

        return endpoints;
    }

    private static Task<IReadOnlyList<EdgeEvent>> LoadEventsByDeviceAsync(
        MySqlConnection connection,
        int adminId,
        int deviceId,
        CancellationToken cancellationToken)
    {
        return LoadEventsAsync(connection, adminId, deviceId, cancellationToken);
    }

    private static async Task<IReadOnlyList<EdgeEvent>> LoadEventsAsync(
        MySqlConnection connection,
        int adminId,
        int? deviceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT evt.id, evt.EdgeDeviceId, device.DeviceName, device.DeviceCode,
                evt.Title, evt.EventDescription, evt.ImageUrl, evt.EventTimeUtc, evt.Status,
                COALESCE(evt.LearningStatus, 'None') AS LearningStatus,
                evt.AnnotationJson, evt.YoloLabelUrl, evt.PpeReviewJson, video.VideoUrl
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            LEFT JOIN (
                SELECT latest.EdgeEventId, latest.VideoUrl
                FROM bee_EdgeEventVideo AS latest
                INNER JOIN (
                    SELECT EdgeEventId, MAX(id) AS LatestVideoId
                    FROM bee_EdgeEventVideo
                    WHERE Status = 'Completed'
                    GROUP BY EdgeEventId
                ) AS grouped ON grouped.LatestVideoId = latest.id
            ) AS video ON video.EdgeEventId = evt.id
            WHERE device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
              AND (@DeviceId IS NULL OR evt.EdgeDeviceId = @DeviceId)
            ORDER BY evt.EventTimeUtc DESC, evt.id DESC;
            """;
        var events = new List<EdgeEvent>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new EdgeEvent
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                EdgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
                EdgeDeviceName = reader["DeviceName"] as string ?? string.Empty,
                EdgeDeviceCode = reader["DeviceCode"] as string ?? string.Empty,
                Title = reader["Title"] as string ?? string.Empty,
                Description = reader["EventDescription"] as string,
                ImageUrl = reader["ImageUrl"] as string,
                EventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")),
                Status = reader["Status"] as string ?? "Ordinary Risk",
                LearningStatus = reader["LearningStatus"] as string ?? "None",
                AnnotationJson = reader["AnnotationJson"] as string,
                YoloLabelUrl = reader["YoloLabelUrl"] as string,
                PpeReviewJson = reader["PpeReviewJson"] as string,
                VideoUrl = reader["VideoUrl"] as string
            });
        }

        return events;
    }

    private static async Task<PagedResult<EdgeEvent>> LoadEventsPageAsync(
        MySqlConnection connection,
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = BuildEventWhere(filters, includeStatus: true);
        var sql = $"""
            SELECT evt.id, evt.EdgeDeviceId, device.DeviceName, device.DeviceCode,
                evt.Title, evt.EventDescription, evt.ImageUrl, evt.EventTimeUtc, evt.Status,
                COALESCE(evt.LearningStatus, 'None') AS LearningStatus,
                evt.AnnotationJson, evt.YoloLabelUrl, evt.PpeReviewJson, video.VideoUrl
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            LEFT JOIN (
                SELECT latest.EdgeEventId, latest.VideoUrl
                FROM bee_EdgeEventVideo AS latest
                INNER JOIN (
                    SELECT EdgeEventId, MAX(id) AS LatestVideoId
                    FROM bee_EdgeEventVideo
                    WHERE Status = 'Completed'
                    GROUP BY EdgeEventId
                ) AS grouped ON grouped.LatestVideoId = latest.id
            ) AS video ON video.EdgeEventId = evt.id
            {where}
            ORDER BY evt.EventTimeUtc DESC, evt.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """;
        var countSql = $"""
            SELECT COUNT(*)
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where};
            """;
        var events = new List<EdgeEvent>();
        await using var command = new MySqlCommand(sql, connection);
        AddEventFilterParameters(command, adminId, filters, includeStatus: true);
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new EdgeEvent
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                EdgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
                EdgeDeviceName = reader["DeviceName"] as string ?? string.Empty,
                EdgeDeviceCode = reader["DeviceCode"] as string ?? string.Empty,
                Title = reader["Title"] as string ?? string.Empty,
                Description = reader["EventDescription"] as string,
                ImageUrl = reader["ImageUrl"] as string,
                EventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")),
                Status = reader["Status"] as string ?? "Ordinary Risk",
                LearningStatus = reader["LearningStatus"] as string ?? "None",
                AnnotationJson = reader["AnnotationJson"] as string,
                YoloLabelUrl = reader["YoloLabelUrl"] as string,
                PpeReviewJson = reader["PpeReviewJson"] as string,
                VideoUrl = reader["VideoUrl"] as string
            });
        }

        await reader.CloseAsync();
        await using var countCommand = new MySqlCommand(countSql, connection);
        AddEventFilterParameters(countCommand, adminId, filters, includeStatus: true);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new PagedResult<EdgeEvent>
        {
            Items = events,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private static string BuildEventWhere(EdgeEventFilters filters, bool includeStatus)
    {
        var clauses = new List<string>
        {
            """
            device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
            """
        };
        if (filters.DeviceId is not null)
        {
            clauses.Add("evt.EdgeDeviceId = @DeviceId");
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            clauses.Add("(evt.Title LIKE @Type OR evt.EventDescription LIKE @Type)");
        }

        if (includeStatus && !string.IsNullOrWhiteSpace(filters.Status))
        {
            clauses.Add("evt.Status = @Status");
        }

        if (!string.IsNullOrWhiteSpace(filters.LearningStatus))
        {
            clauses.Add("COALESCE(evt.LearningStatus, 'None') = @LearningStatus");
        }

        if (filters.DateFrom is not null)
        {
            clauses.Add("evt.EventTimeUtc >= @DateFrom");
        }

        if (filters.DateTo is not null)
        {
            clauses.Add("evt.EventTimeUtc < @DateTo");
        }

        return $"WHERE {string.Join(" AND ", clauses)}";
    }

    private static string BuildEventSubjectWhere(EdgeEventFilters filters, bool includeStatus)
    {
        var clauses = new List<string>
        {
            """
            device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
            """,
            "subject.SubjectType = 'Person'"
        };
        if (filters.DeviceId is not null)
        {
            clauses.Add("evt.EdgeDeviceId = @DeviceId");
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            clauses.Add("(evt.Title LIKE @Type OR evt.EventDescription LIKE @Type OR subject.SubjectKey LIKE @Type OR subject.TrackingLabel LIKE @Type)");
        }

        if (includeStatus && !string.IsNullOrWhiteSpace(filters.Status))
        {
            clauses.Add("evt.Status = @Status");
        }

        if (!string.IsNullOrWhiteSpace(filters.LearningStatus))
        {
            clauses.Add("COALESCE(subject.LearningStatus, 'None') = @LearningStatus");
        }

        if (filters.DateFrom is not null)
        {
            clauses.Add("evt.EventTimeUtc >= @DateFrom");
        }

        if (filters.DateTo is not null)
        {
            clauses.Add("evt.EventTimeUtc < @DateTo");
        }

        return $"WHERE {string.Join(" AND ", clauses)}";
    }

    private static void AddEventFilterParameters(
        MySqlCommand command,
        int adminId,
        EdgeEventFilters filters,
        bool includeStatus)
    {
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        if (filters.DeviceId is not null)
        {
            command.Parameters.Add("@DeviceId", MySqlDbType.Int32).Value = filters.DeviceId.Value;
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            command.Parameters.Add("@Type", MySqlDbType.VarChar, 220).Value = $"%{filters.Type.Trim()}%";
        }

        if (includeStatus && !string.IsNullOrWhiteSpace(filters.Status))
        {
            command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = filters.Status.Trim();
        }

        if (!string.IsNullOrWhiteSpace(filters.LearningStatus))
        {
            command.Parameters.Add("@LearningStatus", MySqlDbType.VarChar, 40).Value = filters.LearningStatus.Trim();
        }

        if (filters.DateFrom is not null)
        {
            command.Parameters.Add("@DateFrom", MySqlDbType.DateTime).Value = filters.DateFrom.Value;
        }

        if (filters.DateTo is not null)
        {
            command.Parameters.Add("@DateTo", MySqlDbType.DateTime).Value = filters.DateTo.Value;
        }
    }

    private static EdgeDevice MapDevice(MySqlDataReader reader)
    {
        return new EdgeDevice
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
            AdminId = reader.GetInt32(reader.GetOrdinal("AdminId")),
            DeviceCode = reader["DeviceCode"] as string ?? string.Empty,
            Name = reader["DeviceName"] as string ?? string.Empty,
            Address = reader["Address"] as string ?? string.Empty,
            Latitude = reader.IsDBNull(reader.GetOrdinal("Latitude"))
                ? null
                : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            Longitude = reader.IsDBNull(reader.GetOrdinal("Longitude"))
                ? null
                : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            GooglePlaceId = reader["GooglePlaceId"] as string,
            StreetViewThumbnailUrl = reader["StreetViewThumbnailUrl"] as string,
            IpAddress = reader["IpAddress"] as string ?? string.Empty,
            ServerResourceInstanceName = reader["ServerResourceInstanceName"] as string,
            Description = reader["Description"] as string,
            RuntimeStatus = reader["RuntimeStatus"] as string,
            DeviceStatus = reader["DeviceStatus"] as string,
            HeartbeatDetailJson = reader["HeartbeatDetailJson"] as string,
            LastHeartbeatAtUtc = reader.IsDBNull(reader.GetOrdinal("LastHeartbeatAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("LastHeartbeatAtUtc")),
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
        };
    }

    private static EdgeEventSubject MapEventSubject(MySqlDataReader reader)
    {
        return new EdgeEventSubject
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            EdgeEventId = reader.GetInt32(reader.GetOrdinal("EdgeEventId")),
            EdgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
            EdgeDeviceName = reader["DeviceName"] as string ?? string.Empty,
            EdgeDeviceCode = reader["DeviceCode"] as string ?? string.Empty,
            EventTitle = reader["Title"] as string ?? string.Empty,
            EventStatus = reader["Status"] as string ?? string.Empty,
            EventLearningStatus = reader["EventLearningStatus"] as string ?? "None",
            LearningStatus = reader["LearningStatus"] as string ?? "None",
            EventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")),
            SubjectKey = reader["SubjectKey"] as string ?? string.Empty,
            SubjectType = reader["SubjectType"] as string ?? "Person",
            TrackingLabel = reader["TrackingLabel"] as string,
            CropImageUrl = reader["CropImageUrl"] as string,
            PreviewImageUrl = reader["PreviewImageUrl"] as string,
            BoundingBoxJson = reader["BoundingBoxJson"] as string,
            PpeBoxJson = reader["PpeBoxJson"] as string,
            PpeStatusJson = reader["PpeStatusJson"] as string,
            IsRisk = Convert.ToBoolean(reader["IsRisk"]),
            RiskCategory = reader["RiskCategory"] as string,
            RiskSeverity = reader["RiskSeverity"] as string,
            RiskReason = reader["RiskReason"] as string
        };
    }
}
