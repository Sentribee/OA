using MySqlConnector;

namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class YoloTrainingRunStore(IConfiguration configuration)
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");

    public async Task<int> CreateOrUpdateRunAsync(
        int projectId,
        string modelKind,
        DateTime? nextTrainingAtUtc,
        string status,
        string? notes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_YoloTrainingRun
                (ProjectId, ModelKind, Status, NextTrainingAtUtc, Notes, UpdatedAtUtc)
            VALUES (@ProjectId, @ModelKind, @Status, @NextTrainingAtUtc, @Notes, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                Status = VALUES(Status),
                NextTrainingAtUtc = VALUES(NextTrainingAtUtc),
                Notes = VALUES(Notes),
                StartedAtUtc = NULL,
                CompletedAtUtc = NULL,
                UpdatedAtUtc = UTC_TIMESTAMP(6);

            SELECT id
            FROM bee_YoloTrainingRun
            WHERE ProjectId = @ProjectId
              AND ModelKind = @ModelKind
            ORDER BY id DESC
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@ModelKind", MySqlDbType.VarChar, 40).Value = YoloTrainingKinds.Normalize(modelKind);
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
        command.Parameters.Add("@NextTrainingAtUtc", MySqlDbType.DateTime).Value = (object?)nextTrainingAtUtc ?? DBNull.Value;
        command.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value = (object?)notes ?? DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<DueTrainingRun>> LoadDueRunsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ProjectId, ModelKind
            FROM bee_YoloTrainingRun
            WHERE (Status = 'Scheduled'
                AND NextTrainingAtUtc IS NOT NULL
                AND NextTrainingAtUtc <= UTC_TIMESTAMP(6))
               OR Status = 'Staging'
            ORDER BY NextTrainingAtUtc, id;
            """;
        var runs = new List<DueTrainingRun>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new DueTrainingRun(
                reader.GetInt32(reader.GetOrdinal("id")),
                reader.GetInt32(reader.GetOrdinal("ProjectId")),
                reader["ModelKind"] as string ?? YoloTrainingKinds.Panorama));
        }

        return runs;
    }

    public async Task MarkStatusAsync(
        int runId,
        string status,
        string? notes,
        CancellationToken cancellationToken)
    {
        var timestampColumn = status switch
        {
            "Training" => "StartedAtUtc = UTC_TIMESTAMP(6),",
            "Completed" or "Failed" => "CompletedAtUtc = UTC_TIMESTAMP(6),",
            _ => string.Empty
        };
        var sql = $"""
            UPDATE bee_YoloTrainingRun
            SET Status = @Status,
                {timestampColumn}
                Notes = COALESCE(@Notes, Notes),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @RunId;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@RunId", MySqlDbType.Int32).Value = runId;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
        command.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value = (object?)notes ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteTrainingAsync(
        int projectId,
        string modelKind,
        YoloTrainingArtifact artifact,
        IReadOnlyList<long> exportedIds,
        CancellationToken cancellationToken)
    {
        modelKind = YoloTrainingKinds.Normalize(modelKind);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (exportedIds.Count > 0)
        {
            var idParameters = exportedIds
                .Select((_, index) => $"@Id{index}")
                .ToList();
            var targetSql = modelKind == YoloTrainingKinds.PersonSlicePpe
                ? $"""
                    UPDATE bee_EdgeEventSubject AS subject
                    INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
                    INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
                    SET subject.LearningStatus = 'Trained'
                    WHERE device.ProjectId = @ProjectId
                      AND subject.id IN ({string.Join(", ", idParameters)})
                      AND COALESCE(subject.LearningStatus, 'None') = 'Pending Learning';
                    """
                : $"""
                    UPDATE bee_EdgeEvent AS evt
                    INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
                    SET evt.LearningStatus = 'Trained'
                    WHERE device.ProjectId = @ProjectId
                      AND evt.id IN ({string.Join(", ", idParameters)})
                      AND COALESCE(evt.LearningStatus, 'None') = 'Pending Learning';
                    """;
            await using var updateCommand = new MySqlCommand(targetSql, connection, (MySqlTransaction)transaction);
            updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            for (var index = 0; index < exportedIds.Count; index++)
            {
                updateCommand.Parameters.Add($"@Id{index}", MySqlDbType.Int64).Value = exportedIds[index];
            }

            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (modelKind == YoloTrainingKinds.Panorama)
        {
            const string clearCurrentSql = "UPDATE bee_YoloModelVersion SET IsCurrent = 0 WHERE ProjectId = @ProjectId;";
            await using (var clearCommand = new MySqlCommand(clearCurrentSql, connection, (MySqlTransaction)transaction))
            {
                clearCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
                await clearCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertVersionSql = """
                INSERT INTO bee_YoloModelVersion
                    (ProjectId, VersionName, Status, Notes, ModelFileUrl, IsCurrent, TrainedAtUtc)
                VALUES
                    (@ProjectId, @VersionName, 'Trained', @Notes, @ModelFileUrl, 1, UTC_TIMESTAMP(6));
                """;
            await using var versionCommand = new MySqlCommand(insertVersionSql, connection, (MySqlTransaction)transaction);
            versionCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            versionCommand.Parameters.Add("@VersionName", MySqlDbType.VarChar, 80).Value = artifact.VersionName;
            versionCommand.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value =
                $"{modelKind} training batch completed. Exported {exportedIds.Count} item(s). Deployed {artifact.DeployedModelPath ?? artifact.BestModelPath ?? "model"}.";
            versionCommand.Parameters.Add("@ModelFileUrl", MySqlDbType.VarChar, 500).Value =
                (object?)(artifact.DeployedModelPath ?? artifact.BestModelPath) ?? DBNull.Value;
            await versionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> HasActiveRunForAdminAsync(int adminId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM bee_YoloTrainingRun AS run
                INNER JOIN bee_Project AS project ON project.id = run.ProjectId
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE run.Status IN ('Staging', 'Exporting', 'Scheduled', 'Training')
                  AND (project.AdminId = @AdminId OR membership.AdminId = @AdminId)
            );
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}

public sealed record DueTrainingRun(int Id, int ProjectId, string ModelKind);
