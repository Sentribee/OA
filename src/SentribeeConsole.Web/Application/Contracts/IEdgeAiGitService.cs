using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IEdgeAiGitService
{
    Task<GitOperationResult> SyncAsync(Project project, CancellationToken cancellationToken);

    Task<GitOperationResult> CheckoutAsync(Project project, string revision, CancellationToken cancellationToken);

    Task<GitOperationResult> CreateDevelopmentHandoffAsync(
        Project project,
        EdgeAiCodeVersion version,
        IReadOnlyList<ProjectRule> diffRules,
        CancellationToken cancellationToken);

    Task<GitOperationResult> CreatePaddingHandoffAsync(
        Project project,
        EdgeAiCodeVersion version,
        IReadOnlyList<ProjectRule> diffRules,
        CancellationToken cancellationToken);

    Task<GitOperationResult> StartPaddingCodeGenerationAsync(
        Project project,
        EdgeAiCodeVersion version,
        CancellationToken cancellationToken);

    Task<GitOperationResult> ResolveBranchHeadAsync(
        Project project,
        string branchName,
        CancellationToken cancellationToken);

    Task<GitOperationResult> VerifyGeneratedCodeChangesAsync(
        Project project,
        string baseCommitSha,
        string headCommitSha,
        CancellationToken cancellationToken);
}

public sealed record GitOperationResult(bool Success, string Message, string? CommitSha = null);
