using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeAiService
{
    Task<EdgeAiDashboard> GetDashboardAsync(int adminId, CancellationToken cancellationToken);

    Task<bool> RollbackAsync(int adminId, int versionId, CancellationToken cancellationToken);

    Task<GitOperationResult> SyncGitAsync(int adminId, CancellationToken cancellationToken);

    Task<GitOperationResult> CheckoutGitRevisionAsync(
        int adminId,
        string revision,
        CancellationToken cancellationToken);

    Task<EdgeAiRuleUpdateResult> AddVersionRuleAsync(
        int adminId,
        string prompt,
        CancellationToken cancellationToken);

    Task<GitOperationResult> HandOffPendingRulesToGitAsync(int adminId, CancellationToken cancellationToken);

    Task<GitOperationResult> PublishGeneratedCodeAsync(int adminId, CancellationToken cancellationToken);

    Task<bool> DeletePendingRuleAsync(int adminId, int ruleId, CancellationToken cancellationToken);

    Task CreateInstanceAsync(int adminId, int logicId, int edgeDeviceId, string instanceName, CancellationToken cancellationToken);
}

public sealed record EdgeAiRuleUpdateResult(
    string VersionName,
    string ChangeType,
    string VersionBump,
    int RuleCount);
