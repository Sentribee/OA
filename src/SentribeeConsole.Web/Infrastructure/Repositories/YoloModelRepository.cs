using System.Data;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Repositories;

public sealed class YoloModelRepository(IConfiguration configuration) : IYoloModelRepository
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");

    public async Task<YoloModelDashboard> GetDashboardAsync(
        int adminId,
        int projectId,
        EdgeEventFilters trainingFilters,
        int eventPageNumber,
        int eventPageSize,
        int subjectPageNumber,
        int subjectPageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var versions = await LoadVersionsAsync(connection, projectId, cancellationToken);
        return new YoloModelDashboard
        {
            CurrentVersion = versions.FirstOrDefault(version => version.IsCurrent),
            Versions = versions,
            PendingTrainingEvents = await LoadTrainingEventsAsync(connection, adminId, trainingFilters, eventPageNumber, eventPageSize, cancellationToken),
            PendingLearningCount = await CountTrainingEventsAsync(connection, adminId, trainingFilters, cancellationToken),
            PendingTrainingSubjects = await LoadTrainingSubjectsAsync(connection, adminId, trainingFilters, subjectPageNumber, subjectPageSize, cancellationToken),
            PendingSubjectLearningCount = await CountTrainingSubjectsAsync(connection, adminId, trainingFilters, cancellationToken),
            Schedule = await LoadScheduleAsync(connection, projectId, cancellationToken)
        };
    }

    public async Task SetScheduleAsync(
        int projectId,
        DateTime? nextTrainingAtUtc,
        bool autoSchedule,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_YoloTrainingSchedule
                (ProjectId, NextTrainingAtUtc, AutoSchedule)
            VALUES (@ProjectId, @NextTrainingAtUtc, @AutoSchedule)
            ON DUPLICATE KEY UPDATE
                NextTrainingAtUtc = VALUES(NextTrainingAtUtc),
                AutoSchedule = VALUES(AutoSchedule),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@NextTrainingAtUtc", MySqlDbType.DateTime).Value =
            (object?)nextTrainingAtUtc ?? DBNull.Value;
        command.Parameters.Add("@AutoSchedule", MySqlDbType.Bit).Value = autoSchedule;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RequestTrainingAsync(int projectId, string notes, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_YoloModelVersion
                (ProjectId, VersionName, Status, Notes, IsCurrent)
            VALUES (@ProjectId, @VersionName, 'Training Requested', @Notes, 0);
            """;
        const string updateEventsSql = """
            UPDATE bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            SET evt.LearningStatus = 'Trained'
            WHERE device.ProjectId = @ProjectId AND COALESCE(evt.LearningStatus, 'None') = 'Pending Learning';
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Transaction = transaction;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@VersionName", MySqlDbType.VarChar, 80).Value = $"yolo-training-{DateTime.UtcNow:yyyyMMddHHmm}";
        command.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value = notes;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var updateCommand = new MySqlCommand(updateEventsSql, connection, transaction);
        updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateCurrentYamlAsync(int projectId, string yamlContent, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_YoloModelVersion
            SET YamlDescription = @YamlDescription
            WHERE ProjectId = @ProjectId AND IsCurrent = 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@YamlDescription", MySqlDbType.LongText).Value = yamlContent;
        await connection.OpenAsync(cancellationToken);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            throw new InvalidOperationException("No current AI model version exists to store the YAML class list.");
        }
    }

    public async Task<bool> RollbackAsync(int projectId, int versionId, CancellationToken cancellationToken)
    {
        const string existsSql = """
            SELECT id
            FROM bee_YoloModelVersion
            WHERE id = @VersionId AND ProjectId = @ProjectId AND Status = 'Trained'
            LIMIT 1;
            """;
        const string clearSql = "UPDATE bee_YoloModelVersion SET IsCurrent = 0 WHERE ProjectId = @ProjectId;";
        const string setCurrentSql = """
            UPDATE bee_YoloModelVersion
            SET IsCurrent = 1
            WHERE id = @VersionId AND ProjectId = @ProjectId;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var existsCommand = new MySqlCommand(existsSql, connection, transaction);
        existsCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        existsCommand.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        if (await existsCommand.ExecuteScalarAsync(cancellationToken) is null)
        {
            return false;
        }

        await using var clearCommand = new MySqlCommand(clearSql, connection, transaction);
        clearCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await clearCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var setCommand = new MySqlCommand(setCurrentSql, connection, transaction);
        setCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        setCommand.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        await setCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<YoloModelVersion>> LoadVersionsAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ProjectId, VersionName, Status, Notes, ModelFileUrl, YamlDescription,
                IsCurrent, CreatedAtUtc, TrainedAtUtc
            FROM bee_YoloModelVersion
            WHERE ProjectId = @ProjectId
            ORDER BY IsCurrent DESC, CreatedAtUtc DESC, id DESC;
            """;
        var versions = new List<YoloModelVersion>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new YoloModelVersion
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
                VersionName = reader["VersionName"] as string ?? string.Empty,
                Status = reader["Status"] as string ?? string.Empty,
                Notes = reader["Notes"] as string,
                ModelFileUrl = reader["ModelFileUrl"] as string,
                YamlDescription = reader["YamlDescription"] as string,
                IsCurrent = reader.GetBoolean(reader.GetOrdinal("IsCurrent")),
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                TrainedAtUtc = reader.IsDBNull(reader.GetOrdinal("TrainedAtUtc"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("TrainedAtUtc"))
            });
        }

        return versions;
    }

    private static async Task<YoloTrainingSchedule?> LoadScheduleAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ProjectId, NextTrainingAtUtc, AutoSchedule, UpdatedAtUtc
            FROM bee_YoloTrainingSchedule
            WHERE ProjectId = @ProjectId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new YoloTrainingSchedule
        {
            ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
            NextTrainingAtUtc = reader.IsDBNull(reader.GetOrdinal("NextTrainingAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("NextTrainingAtUtc")),
            AutoSchedule = reader.GetBoolean(reader.GetOrdinal("AutoSchedule")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
        };
    }

    private static async Task<PagedResult<EdgeEvent>> LoadTrainingEventsAsync(
        MySqlConnection connection,
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = BuildTrainingWhere(filters);
        var sql = $"""
            SELECT evt.id, evt.EdgeDeviceId, device.DeviceName, device.DeviceCode,
                evt.Title, evt.EventDescription, evt.ImageUrl, evt.EventTimeUtc, evt.Status,
                COALESCE(evt.LearningStatus, 'None') AS LearningStatus
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
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
        AddTrainingFilterParameters(command, adminId, filters);
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
                LearningStatus = reader["LearningStatus"] as string ?? "Pending Learning"
            });
        }

        await reader.CloseAsync();
        await using var countCommand = new MySqlCommand(countSql, connection);
        AddTrainingFilterParameters(countCommand, adminId, filters);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new PagedResult<EdgeEvent>
        {
            Items = events,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private static async Task<int> CountTrainingEventsAsync(
        MySqlConnection connection,
        int adminId,
        EdgeEventFilters filters,
        CancellationToken cancellationToken)
    {
        var where = BuildTrainingWhere(filters);
        var sql = $"""
            SELECT COUNT(*)
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where};
            """;
        await using var command = new MySqlCommand(sql, connection);
        AddTrainingFilterParameters(command, adminId, filters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<PagedResult<EdgeEventSubject>> LoadTrainingSubjectsAsync(
        MySqlConnection connection,
        int adminId,
        EdgeEventFilters filters,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = BuildTrainingSubjectWhere(filters);
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
        await using var command = new MySqlCommand(sql, connection);
        AddTrainingFilterParameters(command, adminId, filters);
        command.Parameters.Add("@Offset", MySqlDbType.Int32).Value = (pageNumber - 1) * pageSize;
        command.Parameters.Add("@PageSize", MySqlDbType.Int32).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            subjects.Add(MapTrainingSubject(reader));
        }

        await reader.CloseAsync();
        await using var countCommand = new MySqlCommand(countSql, connection);
        AddTrainingFilterParameters(countCommand, adminId, filters);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new PagedResult<EdgeEventSubject>
        {
            Items = subjects,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private static async Task<int> CountTrainingSubjectsAsync(
        MySqlConnection connection,
        int adminId,
        EdgeEventFilters filters,
        CancellationToken cancellationToken)
    {
        var where = BuildTrainingSubjectWhere(filters);
        var sql = $"""
            SELECT COUNT(*)
            FROM bee_EdgeEventSubject AS subject
            INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            {where};
            """;
        await using var command = new MySqlCommand(sql, connection);
        AddTrainingFilterParameters(command, adminId, filters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string BuildTrainingWhere(EdgeEventFilters filters)
    {
        var clauses = new List<string>
        {
            "device.AdminId = @AdminId",
            "COALESCE(evt.LearningStatus, 'None') = 'Pending Learning'"
        };
        if (filters.DeviceId is not null)
        {
            clauses.Add("evt.EdgeDeviceId = @DeviceId");
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            clauses.Add("(evt.Title LIKE @Type OR evt.EventDescription LIKE @Type)");
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

    private static string BuildTrainingSubjectWhere(EdgeEventFilters filters)
    {
        var clauses = new List<string>
        {
            "device.AdminId = @AdminId",
            "subject.SubjectType = 'Person'",
            "COALESCE(subject.LearningStatus, 'None') = 'Pending Learning'"
        };
        if (filters.DeviceId is not null)
        {
            clauses.Add("evt.EdgeDeviceId = @DeviceId");
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            clauses.Add("(evt.Title LIKE @Type OR evt.EventDescription LIKE @Type OR subject.SubjectKey LIKE @Type OR subject.TrackingLabel LIKE @Type)");
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

    private static void AddTrainingFilterParameters(MySqlCommand command, int adminId, EdgeEventFilters filters)
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

        if (filters.DateFrom is not null)
        {
            command.Parameters.Add("@DateFrom", MySqlDbType.DateTime).Value = filters.DateFrom.Value;
        }

        if (filters.DateTo is not null)
        {
            command.Parameters.Add("@DateTo", MySqlDbType.DateTime).Value = filters.DateTo.Value;
        }
    }

    private static EdgeEventSubject MapTrainingSubject(MySqlDataReader reader)
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
            LearningStatus = reader["LearningStatus"] as string ?? "Pending Learning",
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
