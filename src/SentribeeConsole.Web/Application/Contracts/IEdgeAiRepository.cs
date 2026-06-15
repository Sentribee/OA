using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeAiRepository
{
    Task<IReadOnlyList<EdgeAiLogic>> ListLogicsAsync(int projectId, CancellationToken cancellationToken);

    Task<(int LogicId, string LogicName, int VersionId, string VersionName)?> FindVersionAsync(
        int projectId,
        int versionId,
        CancellationToken cancellationToken);

    Task<bool> RollbackAsync(int projectId, int versionId, CancellationToken cancellationToken);

    Task<EdgeAiCodeVersion> CreateRuleVersionAsync(
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
        CancellationToken cancellationToken);

    Task AddPendingRulesAsync(
        int projectId,
        string changeType,
        string sourcePrompt,
        IReadOnlyList<GeneratedProjectRule> rules,
        CancellationToken cancellationToken);

    Task<EdgeAiCodeVersion> ApplyPendingRulesAsync(
        int projectId,
        int logicId,
        string versionName,
        string description,
        string directoryStructure,
        string featureList,
        string notes,
        IReadOnlyList<ProjectRule> pendingRules,
        CancellationToken cancellationToken);

    Task<bool> RecordGitHandoffAsync(
        int projectId,
        int versionId,
        int dailyLimit,
        CancellationToken cancellationToken);

    Task<int> CountGitHandoffsTodayAsync(
        int projectId,
        CancellationToken cancellationToken);

    Task<bool> DeletePendingRuleAsync(
        int projectId,
        int ruleId,
        CancellationToken cancellationToken);

    Task<EdgeAiCodeGeneration?> GetActiveGenerationAsync(
        int projectId,
        CancellationToken cancellationToken);

    Task<EdgeAiCodeGeneration> CreateGenerationAsync(
        int projectId,
        int logicId,
        string branchName,
        string versionName,
        string status,
        int progressPercent,
        string? handoffCommitSha,
        string? generatedCommitSha,
        string? statusMessage,
        CancellationToken cancellationToken);

    Task UpdateGenerationStatusAsync(
        int generationId,
        string status,
        int progressPercent,
        string? generatedCommitSha,
        string? statusMessage,
        CancellationToken cancellationToken);

    Task CreateInstanceAsync(
        int logicId,
        int edgeDeviceId,
        int? codeVersionId,
        string instanceName,
        string runtimeStatus,
        CancellationToken cancellationToken);
}
