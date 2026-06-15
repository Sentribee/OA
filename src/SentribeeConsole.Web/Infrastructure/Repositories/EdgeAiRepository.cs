using System.Data;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Repositories;

public sealed class EdgeAiRepository(IConfiguration configuration) : IEdgeAiRepository
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    public async Task<IReadOnlyList<EdgeAiLogic>> ListLogicsAsync(int projectId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ProjectId, LogicName, Description
            FROM bee_EdgeAiLogic
            WHERE ProjectId = @ProjectId
            ORDER BY id;
            """;
        var logics = new List<EdgeAiLogic>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logics.Add(new EdgeAiLogic
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
                Name = reader["LogicName"] as string ?? string.Empty,
                Description = reader["Description"] as string
            });
        }

        await reader.CloseAsync();
        var result = new List<EdgeAiLogic>();
        foreach (var logic in logics)
        {
            result.Add(logic with
            {
                Versions = await LoadVersionsAsync(connection, logic.Id, cancellationToken),
                Instances = await LoadInstancesAsync(connection, logic.Id, cancellationToken)
            });
        }

        return result;
    }

    public async Task<(int LogicId, string LogicName, int VersionId, string VersionName)?> FindVersionAsync(
        int projectId,
        int versionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT logic.id AS LogicId, logic.LogicName, version.id AS VersionId, version.VersionName
            FROM bee_EdgeAiCodeVersion AS version
            INNER JOIN bee_EdgeAiLogic AS logic ON logic.id = version.LogicId
            WHERE logic.ProjectId = @ProjectId AND version.id = @VersionId
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetInt32(reader.GetOrdinal("LogicId")),
            reader["LogicName"] as string ?? string.Empty,
            reader.GetInt32(reader.GetOrdinal("VersionId")),
            reader["VersionName"] as string ?? string.Empty);
    }

    public async Task<bool> RollbackAsync(int projectId, int versionId, CancellationToken cancellationToken)
    {
        const string logicSql = """
            SELECT version.LogicId
            FROM bee_EdgeAiCodeVersion AS version
            INNER JOIN bee_EdgeAiLogic AS logic ON logic.id = version.LogicId
            WHERE version.id = @VersionId AND logic.ProjectId = @ProjectId
            LIMIT 1;
            """;
        const string clearSql = "UPDATE bee_EdgeAiCodeVersion SET IsCurrent = 0 WHERE LogicId = @LogicId;";
        const string setCurrentSql = "UPDATE bee_EdgeAiCodeVersion SET IsCurrent = 1 WHERE id = @VersionId;";
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var logicCommand = new MySqlCommand(logicSql, connection, transaction);
        logicCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        logicCommand.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        var logicIdValue = await logicCommand.ExecuteScalarAsync(cancellationToken);
        if (logicIdValue is null)
        {
            return false;
        }

        var logicId = Convert.ToInt32(logicIdValue);
        await using var clearCommand = new MySqlCommand(clearSql, connection, transaction);
        clearCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await clearCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var setCommand = new MySqlCommand(setCurrentSql, connection, transaction);
        setCommand.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        await setCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<EdgeAiCodeVersion> CreateRuleVersionAsync(
        int projectId,
        int logicId,
        string versionName,
        string description,
        string directoryStructure,
        string featureList,
        string notes,
        string changeType,
        string sourcePrompt,
        IReadOnlyList<GeneratedProjectRule> rules,
        CancellationToken cancellationToken)
    {
        const string currentVersionSql = """
            SELECT version.id, version.LogicId, version.PackageSizeBytes, version.FileCount
            FROM bee_EdgeAiCodeVersion AS version
            INNER JOIN bee_EdgeAiLogic AS logic ON logic.id = version.LogicId
            WHERE logic.ProjectId = @ProjectId
              AND logic.id = @LogicId
              AND version.IsCurrent = 1
            LIMIT 1;
            """;
        const string insertVersionSql = """
            INSERT INTO bee_EdgeAiCodeVersion
                (LogicId, VersionName, Description, IsCurrent, PackageSizeBytes, FileCount,
                    DirectoryStructure, FeatureList, Notes)
            VALUES
                (@LogicId, @VersionName, @Description, 0, @PackageSizeBytes, @FileCount,
                    @DirectoryStructure, @FeatureList, @Notes);
            SELECT LAST_INSERT_ID();
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var currentCommand = new MySqlCommand(currentVersionSql, connection, transaction);
        currentCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        currentCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await using var currentReader = await currentCommand.ExecuteReaderAsync(cancellationToken);
        if (!await currentReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Create a current Edge AI code version before adding rules.");
        }

        var currentVersionId = currentReader.GetInt32(currentReader.GetOrdinal("id"));
        var packageSizeBytes = currentReader.IsDBNull(currentReader.GetOrdinal("PackageSizeBytes"))
            ? null
            : (long?)currentReader.GetInt64(currentReader.GetOrdinal("PackageSizeBytes"));
        var fileCount = currentReader.IsDBNull(currentReader.GetOrdinal("FileCount"))
            ? null
            : (int?)currentReader.GetInt32(currentReader.GetOrdinal("FileCount"));
        await currentReader.CloseAsync();

        var activeRules = await LoadActiveVersionRulesAsync(
            connection,
            transaction,
            projectId,
            currentVersionId,
            cancellationToken);

        if (string.Equals(changeType, "Removed", StringComparison.OrdinalIgnoreCase))
        {
            activeRules = activeRules
                .Where(rule => !rules.Any(generated => SameRuleIntent(rule, generated)))
                .ToList();
        }
        else
        {
            activeRules.AddRange(rules.Select(rule => new ProjectRule
            {
                ProjectId = projectId,
                EdgeAiCodeVersionId = currentVersionId,
                ChangeType = "Active",
                Dimension = rule.Dimension,
                RuleText = rule.RuleText,
                SourcePrompt = sourcePrompt,
                CreatedAtUtc = DateTime.UtcNow
            }));
        }

        await using var insertVersionCommand = new MySqlCommand(insertVersionSql, connection, transaction);
        insertVersionCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        insertVersionCommand.Parameters.Add("@VersionName", MySqlDbType.VarChar, 80).Value = versionName;
        insertVersionCommand.Parameters.Add("@Description", MySqlDbType.VarChar, 500).Value = description;
        insertVersionCommand.Parameters.Add("@PackageSizeBytes", MySqlDbType.Int64).Value =
            (object?)packageSizeBytes ?? DBNull.Value;
        insertVersionCommand.Parameters.Add("@FileCount", MySqlDbType.Int32).Value =
            (object?)fileCount ?? DBNull.Value;
        insertVersionCommand.Parameters.Add("@DirectoryStructure", MySqlDbType.Text).Value = directoryStructure;
        insertVersionCommand.Parameters.Add("@FeatureList", MySqlDbType.Text).Value = featureList;
        insertVersionCommand.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value = notes;
        var newVersionId = Convert.ToInt32(await insertVersionCommand.ExecuteScalarAsync(cancellationToken));

        foreach (var rule in activeRules)
        {
            await InsertProjectRuleAsync(
                connection,
                transaction,
                projectId,
                newVersionId,
                "Active",
                rule.Dimension,
                rule.RuleText,
                rule.SourcePrompt ?? "Copied from previous Edge AI code version.",
                cancellationToken);
        }

        foreach (var rule in rules)
        {
            await InsertProjectRuleAsync(
                connection,
                transaction,
                projectId,
                newVersionId,
                changeType,
                rule.Dimension,
                rule.RuleText,
                sourcePrompt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new EdgeAiCodeVersion
        {
            Id = newVersionId,
            LogicId = logicId,
            VersionName = versionName,
            Description = description,
            IsCurrent = false,
            PackageSizeBytes = packageSizeBytes,
            FileCount = fileCount,
            DirectoryStructure = directoryStructure,
            FeatureList = featureList,
            Notes = notes,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task AddPendingRulesAsync(
        int projectId,
        string changeType,
        string sourcePrompt,
        IReadOnlyList<GeneratedProjectRule> rules,
        CancellationToken cancellationToken)
    {
        const string existsSql = """
            SELECT COUNT(*)
            FROM bee_ProjectRule
            WHERE ProjectId = @ProjectId
              AND ChangeType = @ChangeType
              AND Dimension = @Dimension
              AND RuleText = @RuleText;
            """;
        const string insertSql = """
            INSERT INTO bee_ProjectRule
                (ProjectId, EdgeAiCodeVersionId, ChangeType, Dimension, RuleText, SourcePrompt)
            VALUES
                (@ProjectId, NULL, @ChangeType, @Dimension, @RuleText, @SourcePrompt);
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var rule in rules)
        {
            var pendingChangeType = string.Equals(changeType, "Removed", StringComparison.OrdinalIgnoreCase)
                ? "PendingRemoved"
                : "PendingAdded";
            await using var existsCommand = new MySqlCommand(existsSql, connection, transaction);
            existsCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            existsCommand.Parameters.Add("@ChangeType", MySqlDbType.VarChar, 20).Value = pendingChangeType;
            existsCommand.Parameters.Add("@Dimension", MySqlDbType.VarChar, 100).Value = rule.Dimension;
            existsCommand.Parameters.Add("@RuleText", MySqlDbType.VarChar, 1000).Value = rule.RuleText;
            if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                continue;
            }

            await using var insertCommand = new MySqlCommand(insertSql, connection, transaction);
            insertCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
            insertCommand.Parameters.Add("@ChangeType", MySqlDbType.VarChar, 20).Value = pendingChangeType;
            insertCommand.Parameters.Add("@Dimension", MySqlDbType.VarChar, 100).Value = rule.Dimension;
            insertCommand.Parameters.Add("@RuleText", MySqlDbType.VarChar, 1000).Value = rule.RuleText;
            insertCommand.Parameters.Add("@SourcePrompt", MySqlDbType.Text).Value = sourcePrompt;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<EdgeAiCodeVersion> ApplyPendingRulesAsync(
        int projectId,
        int logicId,
        string versionName,
        string description,
        string directoryStructure,
        string featureList,
        string notes,
        IReadOnlyList<ProjectRule> pendingRules,
        CancellationToken cancellationToken)
    {
        const string currentVersionSql = """
            SELECT version.id, version.PackageSizeBytes, version.FileCount
            FROM bee_EdgeAiCodeVersion AS version
            INNER JOIN bee_EdgeAiLogic AS logic ON logic.id = version.LogicId
            WHERE logic.ProjectId = @ProjectId
              AND logic.id = @LogicId
              AND version.IsCurrent = 1
            LIMIT 1;
            """;
        const string clearCurrentSql = "UPDATE bee_EdgeAiCodeVersion SET IsCurrent = 0 WHERE LogicId = @LogicId;";
        const string insertVersionSql = """
            INSERT INTO bee_EdgeAiCodeVersion
                (LogicId, VersionName, Description, IsCurrent, PackageSizeBytes, FileCount,
                    DirectoryStructure, FeatureList, Notes)
            VALUES
                (@LogicId, @VersionName, @Description, 1, @PackageSizeBytes, @FileCount,
                    @DirectoryStructure, @FeatureList, @Notes);
            SELECT LAST_INSERT_ID();
            """;
        const string deletePendingSql = """
            DELETE FROM bee_ProjectRule
            WHERE ProjectId = @ProjectId
              AND ChangeType IN ('PendingAdded', 'PendingRemoved');
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var currentCommand = new MySqlCommand(currentVersionSql, connection, transaction);
        currentCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        currentCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await using var currentReader = await currentCommand.ExecuteReaderAsync(cancellationToken);
        if (!await currentReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Create a current Edge AI code version before applying rules.");
        }

        var currentVersionId = currentReader.GetInt32(currentReader.GetOrdinal("id"));
        var packageSizeBytes = currentReader.IsDBNull(currentReader.GetOrdinal("PackageSizeBytes"))
            ? null
            : (long?)currentReader.GetInt64(currentReader.GetOrdinal("PackageSizeBytes"));
        var fileCount = currentReader.IsDBNull(currentReader.GetOrdinal("FileCount"))
            ? null
            : (int?)currentReader.GetInt32(currentReader.GetOrdinal("FileCount"));
        await currentReader.CloseAsync();

        var activeRules = await LoadActiveVersionRulesAsync(connection, transaction, projectId, currentVersionId, cancellationToken);
        foreach (var pendingRule in pendingRules)
        {
            if (string.Equals(pendingRule.ChangeType, "PendingRemoved", StringComparison.OrdinalIgnoreCase))
            {
                activeRules = activeRules.Where(rule => !SameRuleIntent(rule, new GeneratedProjectRule(pendingRule.Dimension, pendingRule.RuleText))).ToList();
            }
            else
            {
                if (!activeRules.Any(rule => SameRuleIntent(rule, new GeneratedProjectRule(pendingRule.Dimension, pendingRule.RuleText))))
                {
                    activeRules.Add(new ProjectRule
                    {
                        ProjectId = projectId,
                        ChangeType = "Active",
                        Dimension = pendingRule.Dimension,
                        RuleText = pendingRule.RuleText,
                        SourcePrompt = pendingRule.SourcePrompt,
                        CreatedAtUtc = pendingRule.CreatedAtUtc
                    });
                }
            }
        }

        await using var clearCommand = new MySqlCommand(clearCurrentSql, connection, transaction);
        clearCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await clearCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var insertVersionCommand = new MySqlCommand(insertVersionSql, connection, transaction);
        insertVersionCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        insertVersionCommand.Parameters.Add("@VersionName", MySqlDbType.VarChar, 80).Value = versionName;
        insertVersionCommand.Parameters.Add("@Description", MySqlDbType.VarChar, 500).Value = description;
        insertVersionCommand.Parameters.Add("@PackageSizeBytes", MySqlDbType.Int64).Value = (object?)packageSizeBytes ?? DBNull.Value;
        insertVersionCommand.Parameters.Add("@FileCount", MySqlDbType.Int32).Value = (object?)fileCount ?? DBNull.Value;
        insertVersionCommand.Parameters.Add("@DirectoryStructure", MySqlDbType.Text).Value = directoryStructure;
        insertVersionCommand.Parameters.Add("@FeatureList", MySqlDbType.Text).Value = featureList;
        insertVersionCommand.Parameters.Add("@Notes", MySqlDbType.VarChar, 500).Value = notes;
        var newVersionId = Convert.ToInt32(await insertVersionCommand.ExecuteScalarAsync(cancellationToken));

        foreach (var rule in activeRules)
        {
            await InsertProjectRuleAsync(
                connection,
                transaction,
                projectId,
                newVersionId,
                "Active",
                rule.Dimension,
                rule.RuleText,
                rule.SourcePrompt ?? "Applied from Edge AI pending rules.",
                cancellationToken);
        }

        await using var deletePendingCommand = new MySqlCommand(deletePendingSql, connection, transaction);
        deletePendingCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await deletePendingCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EdgeAiCodeVersion
        {
            Id = newVersionId,
            LogicId = logicId,
            VersionName = versionName,
            Description = description,
            IsCurrent = true,
            PackageSizeBytes = packageSizeBytes,
            FileCount = fileCount,
            DirectoryStructure = directoryStructure,
            FeatureList = featureList,
            Notes = notes,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<bool> RecordGitHandoffAsync(
        int projectId,
        int versionId,
        int dailyLimit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_EdgeAiGitHandoff (ProjectId, EdgeAiCodeVersionId, CreatedAtUtc)
            SELECT @ProjectId, @VersionId, UTC_TIMESTAMP(6)
            WHERE (
                SELECT COUNT(*)
                FROM bee_EdgeAiGitHandoff
                WHERE ProjectId = @ProjectId
                  AND DATE(CreatedAtUtc) = UTC_DATE()
            ) < @DailyLimit;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = versionId;
        command.Parameters.Add("@DailyLimit", MySqlDbType.Int32).Value = dailyLimit;
        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> CountGitHandoffsTodayAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM bee_EdgeAiCodeGeneration
            WHERE ProjectId = @ProjectId
              AND DATE(CreatedAtUtc) = UTC_DATE();
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await connection.OpenAsync(cancellationToken);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> DeletePendingRuleAsync(
        int projectId,
        int ruleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE rule
            FROM bee_ProjectRule AS rule
            INNER JOIN bee_EdgeAiCodeVersion AS version ON version.id = rule.EdgeAiCodeVersionId
            INNER JOIN bee_EdgeAiLogic AS logic ON logic.id = version.LogicId
            WHERE rule.id = @RuleId
              AND rule.ProjectId = @ProjectId
              AND logic.ProjectId = @ProjectId
              AND version.IsCurrent = 0
              AND rule.ChangeType <> 'Active';
            """;
        const string pendingSql = """
            DELETE rule
            FROM bee_ProjectRule AS rule
            WHERE rule.id = @RuleId
              AND rule.ProjectId = @ProjectId
              AND rule.ChangeType IN ('PendingAdded', 'PendingRemoved');
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@RuleId", MySqlDbType.Int32).Value = ruleId;
        await connection.OpenAsync(cancellationToken);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (deleted > 0)
        {
            return true;
        }

        await using var pendingCommand = new MySqlCommand(pendingSql, connection);
        pendingCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        pendingCommand.Parameters.Add("@RuleId", MySqlDbType.Int32).Value = ruleId;
        return await pendingCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<EdgeAiCodeGeneration?> GetActiveGenerationAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, ProjectId, LogicId, BranchName, VersionName, Status, ProgressPercent,
                HandoffCommitSha, GeneratedCommitSha, StatusMessage, CreatedAtUtc, UpdatedAtUtc
            FROM bee_EdgeAiCodeGeneration
            WHERE ProjectId = @ProjectId
              AND Status IN ('Requested', 'Generating', 'ReadyToPublish', 'TimedOut', 'Failed', 'NoChanges')
            ORDER BY CreatedAtUtc DESC
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapGeneration(reader) : null;
    }

    public async Task<EdgeAiCodeGeneration> CreateGenerationAsync(
        int projectId,
        int logicId,
        string branchName,
        string versionName,
        string status,
        int progressPercent,
        string? handoffCommitSha,
        string? generatedCommitSha,
        string? statusMessage,
        CancellationToken cancellationToken)
    {
        const string closeSql = """
            UPDATE bee_EdgeAiCodeGeneration
            SET Status = 'Superseded',
                ProgressPercent = 100,
                StatusMessage = 'Superseded by a newer generation request.',
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE ProjectId = @ProjectId
              AND Status IN ('Requested', 'Generating', 'ReadyToPublish');
            """;
        const string insertSql = """
            INSERT INTO bee_EdgeAiCodeGeneration
                (ProjectId, LogicId, BranchName, VersionName, Status, ProgressPercent,
                    HandoffCommitSha, GeneratedCommitSha, StatusMessage)
            VALUES
                (@ProjectId, @LogicId, @BranchName, @VersionName, @Status, @ProgressPercent,
                    @HandoffCommitSha, @GeneratedCommitSha, @StatusMessage);
            SELECT LAST_INSERT_ID();
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var closeCommand = new MySqlCommand(closeSql, connection, transaction);
        closeCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await closeCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var insertCommand = new MySqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        insertCommand.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        insertCommand.Parameters.Add("@BranchName", MySqlDbType.VarChar, 100).Value = branchName;
        insertCommand.Parameters.Add("@VersionName", MySqlDbType.VarChar, 80).Value = versionName;
        insertCommand.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
        insertCommand.Parameters.Add("@ProgressPercent", MySqlDbType.Int32).Value = progressPercent;
        insertCommand.Parameters.Add("@HandoffCommitSha", MySqlDbType.VarChar, 80).Value = (object?)handoffCommitSha ?? DBNull.Value;
        insertCommand.Parameters.Add("@GeneratedCommitSha", MySqlDbType.VarChar, 80).Value = (object?)generatedCommitSha ?? DBNull.Value;
        insertCommand.Parameters.Add("@StatusMessage", MySqlDbType.VarChar, 500).Value = (object?)statusMessage ?? DBNull.Value;
        var id = Convert.ToInt32(await insertCommand.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        return new EdgeAiCodeGeneration
        {
            Id = id,
            ProjectId = projectId,
            LogicId = logicId,
            BranchName = branchName,
            VersionName = versionName,
            Status = status,
            ProgressPercent = progressPercent,
            HandoffCommitSha = handoffCommitSha,
            GeneratedCommitSha = generatedCommitSha,
            StatusMessage = statusMessage,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task UpdateGenerationStatusAsync(
        int generationId,
        string status,
        int progressPercent,
        string? generatedCommitSha,
        string? statusMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_EdgeAiCodeGeneration
            SET Status = @Status,
                ProgressPercent = @ProgressPercent,
                GeneratedCommitSha = COALESCE(@GeneratedCommitSha, GeneratedCommitSha),
                StatusMessage = @StatusMessage,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @GenerationId;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@GenerationId", MySqlDbType.Int32).Value = generationId;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
        command.Parameters.Add("@ProgressPercent", MySqlDbType.Int32).Value = progressPercent;
        command.Parameters.Add("@GeneratedCommitSha", MySqlDbType.VarChar, 80).Value = (object?)generatedCommitSha ?? DBNull.Value;
        command.Parameters.Add("@StatusMessage", MySqlDbType.VarChar, 500).Value = (object?)statusMessage ?? DBNull.Value;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateInstanceAsync(
        int logicId,
        int edgeDeviceId,
        int? codeVersionId,
        string instanceName,
        string runtimeStatus,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_EdgeAiInstance
                (LogicId, EdgeDeviceId, CodeVersionId, InstanceName, Status, RuntimeStatus)
            VALUES (@LogicId, @EdgeDeviceId, @CodeVersionId, @InstanceName, 'Deployed', @RuntimeStatus)
            ON DUPLICATE KEY UPDATE
                CodeVersionId = VALUES(CodeVersionId),
                InstanceName = VALUES(InstanceName),
                Status = VALUES(Status),
                RuntimeStatus = VALUES(RuntimeStatus);
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = edgeDeviceId;
        command.Parameters.Add("@CodeVersionId", MySqlDbType.Int32).Value = (object?)codeVersionId ?? DBNull.Value;
        command.Parameters.Add("@InstanceName", MySqlDbType.VarChar, 150).Value = instanceName;
        command.Parameters.Add("@RuntimeStatus", MySqlDbType.VarChar, 80).Value = runtimeStatus;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EdgeAiCodeGeneration MapGeneration(MySqlDataReader reader)
    {
        return new EdgeAiCodeGeneration
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
            LogicId = reader.GetInt32(reader.GetOrdinal("LogicId")),
            BranchName = reader["BranchName"] as string ?? string.Empty,
            VersionName = reader["VersionName"] as string ?? string.Empty,
            Status = reader["Status"] as string ?? string.Empty,
            ProgressPercent = reader.GetInt32(reader.GetOrdinal("ProgressPercent")),
            HandoffCommitSha = reader["HandoffCommitSha"] as string,
            GeneratedCommitSha = reader["GeneratedCommitSha"] as string,
            StatusMessage = reader["StatusMessage"] as string,
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
        };
    }

    private static async Task<IReadOnlyList<EdgeAiCodeVersion>> LoadVersionsAsync(
        MySqlConnection connection,
        int logicId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, LogicId, VersionName, Description, IsCurrent, PackageSizeBytes, FileCount,
                DirectoryStructure, FeatureList, Notes, CreatedAtUtc
            FROM bee_EdgeAiCodeVersion
            WHERE LogicId = @LogicId
            ORDER BY IsCurrent DESC, CreatedAtUtc DESC;
            """;
        var versions = new List<EdgeAiCodeVersion>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new EdgeAiCodeVersion
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                LogicId = reader.GetInt32(reader.GetOrdinal("LogicId")),
                VersionName = reader["VersionName"] as string ?? string.Empty,
                Description = reader["Description"] as string,
                IsCurrent = reader.GetBoolean(reader.GetOrdinal("IsCurrent")),
                PackageSizeBytes = reader.IsDBNull(reader.GetOrdinal("PackageSizeBytes"))
                    ? null
                    : reader.GetInt64(reader.GetOrdinal("PackageSizeBytes")),
                FileCount = reader.IsDBNull(reader.GetOrdinal("FileCount"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("FileCount")),
                DirectoryStructure = reader["DirectoryStructure"] as string ?? string.Empty,
                FeatureList = reader["FeatureList"] as string ?? string.Empty,
                Notes = reader["Notes"] as string,
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            });
        }

        return versions;
    }

    private static async Task<List<ProjectRule>> LoadActiveVersionRulesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int projectId,
        int currentVersionId,
        CancellationToken cancellationToken)
    {
        var versionRules = await LoadRulesAsync(
            connection,
            transaction,
            projectId,
            "EdgeAiCodeVersionId = @VersionId",
            currentVersionId,
            cancellationToken);
        return versionRules.Count > 0
            ? versionRules
            : await LoadRulesAsync(
                connection,
                transaction,
                projectId,
                "EdgeAiCodeVersionId IS NULL",
                currentVersionId,
                cancellationToken);
    }

    private static async Task<List<ProjectRule>> LoadRulesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int projectId,
        string versionPredicate,
        int currentVersionId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT id, ProjectId, EdgeAiCodeVersionId, ChangeType, Dimension, RuleText, SourcePrompt, CreatedAtUtc
            FROM bee_ProjectRule
            WHERE ProjectId = @ProjectId
              AND {versionPredicate}
              AND ChangeType <> 'Removed'
            ORDER BY CreatedAtUtc ASC, id ASC;
            """;
        var rules = new List<ProjectRule>();
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@VersionId", MySqlDbType.Int32).Value = currentVersionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new ProjectRule
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                ProjectId = reader.GetInt32(reader.GetOrdinal("ProjectId")),
                EdgeAiCodeVersionId = reader.IsDBNull(reader.GetOrdinal("EdgeAiCodeVersionId"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("EdgeAiCodeVersionId")),
                ChangeType = reader["ChangeType"] as string ?? "Active",
                Dimension = reader["Dimension"] as string ?? string.Empty,
                RuleText = reader["RuleText"] as string ?? string.Empty,
                SourcePrompt = reader["SourcePrompt"] as string,
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            });
        }

        return rules;
    }

    private static async Task InsertProjectRuleAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int projectId,
        int versionId,
        string changeType,
        string dimension,
        string ruleText,
        string sourcePrompt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_ProjectRule
                (ProjectId, EdgeAiCodeVersionId, ChangeType, Dimension, RuleText, SourcePrompt)
            VALUES
                (@ProjectId, @EdgeAiCodeVersionId, @ChangeType, @Dimension, @RuleText, @SourcePrompt);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@EdgeAiCodeVersionId", MySqlDbType.Int32).Value = versionId;
        command.Parameters.Add("@ChangeType", MySqlDbType.VarChar, 20).Value = changeType;
        command.Parameters.Add("@Dimension", MySqlDbType.VarChar, 100).Value = dimension;
        command.Parameters.Add("@RuleText", MySqlDbType.VarChar, 1000).Value = ruleText;
        command.Parameters.Add("@SourcePrompt", MySqlDbType.Text).Value = sourcePrompt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool SameRuleIntent(ProjectRule existing, GeneratedProjectRule generated)
    {
        return string.Equals(existing.Dimension, generated.Dimension, StringComparison.OrdinalIgnoreCase) ||
            existing.RuleText.Contains(generated.RuleText, StringComparison.OrdinalIgnoreCase) ||
            generated.RuleText.Contains(existing.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<EdgeAiInstance>> LoadInstancesAsync(
        MySqlConnection connection,
        int logicId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT inst.id, inst.LogicId, inst.EdgeDeviceId, device.DeviceName,
                inst.CodeVersionId, version.VersionName AS CodeVersionName,
                inst.InstanceName, inst.Status, inst.RuntimeStatus, inst.CreatedAtUtc
            FROM bee_EdgeAiInstance AS inst
            INNER JOIN bee_EdgeDevice AS device ON device.id = inst.EdgeDeviceId
            LEFT JOIN bee_EdgeAiCodeVersion AS version ON version.id = inst.CodeVersionId
            WHERE inst.LogicId = @LogicId
            ORDER BY inst.CreatedAtUtc DESC;
            """;
        var instances = new List<EdgeAiInstance>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LogicId", MySqlDbType.Int32).Value = logicId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(new EdgeAiInstance
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                LogicId = reader.GetInt32(reader.GetOrdinal("LogicId")),
                EdgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
                EdgeDeviceName = reader["DeviceName"] as string ?? string.Empty,
                CodeVersionId = reader.IsDBNull(reader.GetOrdinal("CodeVersionId"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("CodeVersionId")),
                CodeVersionName = reader["CodeVersionName"] as string,
                InstanceName = reader["InstanceName"] as string ?? string.Empty,
                Status = reader["Status"] as string ?? string.Empty,
                RuntimeStatus = reader["RuntimeStatus"] as string ?? "Pending",
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            });
        }

        return instances;
    }
}
