using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public sealed class EdgeAiService(
    IEdgeAiRepository repository,
    IProjectRepository projectRepository,
    IEdgeDeviceRepository edgeDeviceRepository,
    IProjectRequirementService requirementService,
    IProjectRuleGenerator ruleGenerator,
    IEdgeAiGitService gitService) : IEdgeAiService
{
    private const int DailyGitHandoffLimit = 10;
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(30);

    public async Task<EdgeAiDashboard> GetDashboardAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        var devices = await edgeDeviceRepository.ListByAdminAsync(adminId, 1, 100, cancellationToken);
        var logics = await repository.ListLogicsAsync(project.Id, cancellationToken);
        var currentVersion = logics
            .SelectMany(logic => logic.Versions)
            .FirstOrDefault(version => version.IsCurrent);
        return new EdgeAiDashboard
        {
            Project = project,
            Logics = logics,
            EdgeDevices = devices.Items,
            Requirements = requirementService.Summarize(project, currentVersion),
            CurrentVersion = currentVersion,
            DailyGitHandoffLimit = DailyGitHandoffLimit,
            DailyGitHandoffUsed = await repository.CountGitHandoffsTodayAsync(project.Id, cancellationToken),
            ActiveGeneration = await RefreshGenerationAsync(project, cancellationToken)
        };
    }

    public async Task<bool> RollbackAsync(int adminId, int versionId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        return await repository.RollbackAsync(project.Id, versionId, cancellationToken);
    }

    public async Task<GitOperationResult> SyncGitAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        return await gitService.SyncAsync(project, cancellationToken);
    }

    public async Task<GitOperationResult> CheckoutGitRevisionAsync(
        int adminId,
        string revision,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        return await gitService.CheckoutAsync(project, revision, cancellationToken);
    }

    public async Task<EdgeAiRuleUpdateResult> AddVersionRuleAsync(
        int adminId,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Describe the rule you want to add or remove.", nameof(prompt));
        }

        var normalizedPrompt = prompt.Trim();
        if (!HasMeaningfulRulePrompt(normalizedPrompt))
        {
            throw new InvalidOperationException("Rule requirement is too short or unclear. Describe a real safety rule, condition, event, or response method.");
        }

        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        var logics = await repository.ListLogicsAsync(project.Id, cancellationToken);
        var logic = logics.FirstOrDefault(logic => logic.Versions.Any(version => version.IsCurrent))
            ?? logics.FirstOrDefault()
            ?? throw new InvalidOperationException("Create an Edge AI logic before adding rules.");
        var currentVersion = logic.Versions.FirstOrDefault(version => version.IsCurrent)
            ?? throw new InvalidOperationException("Create a current Edge AI code version before adding rules.");

        IReadOnlyList<GeneratedProjectRule> generatedRules;
        try
        {
            generatedRules = await ruleGenerator.GenerateAsync(
                project.Name,
                project.Description,
                normalizedPrompt,
                cancellationToken);
        }
        catch
        {
            generatedRules = [];
        }

        var newRules = NormalizeGeneratedRules(generatedRules, normalizedPrompt);
        if (newRules.Count == 0)
        {
            throw new InvalidOperationException("Add rule version failed: the requirement was not recognized as Environment Recognition, Recognition Logic, Event Recognition, or Response Method.");
        }

        var changeType = DetectChangeType(normalizedPrompt, newRules);
        await repository.AddPendingRulesAsync(
            project.Id,
            changeType,
            normalizedPrompt,
            newRules,
            cancellationToken);

        generatedRules = GetPendingDiffRules(project)
            .Select(rule => new GeneratedProjectRule(rule.Dimension, rule.RuleText))
            .Concat(newRules)
            .DistinctBy(rule => $"{rule.Dimension}|{rule.RuleText}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projectedProject = ProjectWithProjectedRules(project, currentVersion, generatedRules, changeType);
        var summary = requirementService.Summarize(projectedProject, currentVersion);
        var versionBump = DetermineVersionBump(generatedRules, summary);
        var nextVersion = IncrementVersion(currentVersion.VersionName, versionBump);

        return new EdgeAiRuleUpdateResult(nextVersion, changeType, versionBump, generatedRules.Count);
    }

    public async Task<GitOperationResult> HandOffPendingRulesToGitAsync(
        int adminId,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        var logics = await repository.ListLogicsAsync(project.Id, cancellationToken);
        var logic = logics.FirstOrDefault(logic => logic.Versions.Any(version => version.IsCurrent))
            ?? logics.FirstOrDefault();
        var currentVersion = logic?.Versions.FirstOrDefault(version => version.IsCurrent);
        if (logic is null || currentVersion is null)
        {
            return new GitOperationResult(false, "Create a current Edge AI code version before applying rules.");
        }

        var diffRules = GetPendingDiffRules(project).ToList();
        if (diffRules.Count == 0)
        {
            return new GitOperationResult(false, "No pending rule changes remain.");
        }

        var usedToday = await repository.CountGitHandoffsTodayAsync(project.Id, cancellationToken);
        if (usedToday >= DailyGitHandoffLimit)
        {
            return new GitOperationResult(false, "Daily code generation limit reached. Try again tomorrow.");
        }

        var generatedRules = diffRules.Select(rule => new GeneratedProjectRule(rule.Dimension, rule.RuleText)).ToList();
        var projectedProject = ProjectWithProjectedRules(project, currentVersion, generatedRules, "Added");
        var summary = requirementService.Summarize(projectedProject, currentVersion);
        var versionBump = DetermineVersionBump(generatedRules, summary);
        var nextVersion = IncrementVersion(currentVersion.VersionName, versionBump);
        var virtualVersion = currentVersion with
        {
            Id = 0,
            VersionName = nextVersion,
            Description = $"Apply {diffRules.Count} pending rule(s) through Git/Xcode.",
            IsCurrent = false,
            Notes = $"Pending rule update generated at {DateTime.UtcNow:O}"
        };

        var gitResult = await gitService.CreatePaddingHandoffAsync(project, virtualVersion, diffRules, cancellationToken);
        if (!gitResult.Success)
        {
            return gitResult;
        }

        var generation = await repository.CreateGenerationAsync(
            project.Id,
            logic.Id,
            "padding",
            nextVersion,
            "Generating",
            35,
            gitResult.CommitSha,
            null,
            "Rules sent to the padding branch. Waiting for code generation to finish.",
            cancellationToken);

        var generationResult = await gitService.StartPaddingCodeGenerationAsync(project, virtualVersion, cancellationToken);
        if (!generationResult.Success)
        {
            var failureMessage = $"Code generation could not start. {generationResult.Message}";
            await repository.UpdateGenerationStatusAsync(
                generation.Id,
                "Failed",
                100,
                null,
                failureMessage,
                cancellationToken);
            return generationResult with { Message = failureMessage };
        }

        return gitResult with { Message = "Padding branch prepared. Local Codex code generation is now running." };
    }

    public async Task<GitOperationResult> PublishGeneratedCodeAsync(
        int adminId,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        var generation = await RefreshGenerationAsync(project, cancellationToken);
        if (generation is null)
        {
            return new GitOperationResult(false, "No code generation is waiting to publish.");
        }

        if (!string.Equals(generation.Status, "ReadyToPublish", StringComparison.OrdinalIgnoreCase))
        {
            return new GitOperationResult(false, "Code generation is not ready to publish yet.");
        }

        var logics = await repository.ListLogicsAsync(project.Id, cancellationToken);
        var logic = logics.FirstOrDefault(logic => logic.Id == generation.LogicId)
            ?? logics.FirstOrDefault(logic => logic.Versions.Any(version => version.IsCurrent));
        var currentVersion = logic?.Versions.FirstOrDefault(version => version.IsCurrent);
        if (logic is null || currentVersion is null)
        {
            return new GitOperationResult(false, "Create a current Edge AI code version before publishing generated code.");
        }

        var diffRules = GetPendingDiffRules(project).ToList();
        if (diffRules.Count == 0)
        {
            return new GitOperationResult(false, "No pending rules are available to publish.");
        }

        var generatedRules = diffRules.Select(rule => new GeneratedProjectRule(rule.Dimension, rule.RuleText)).ToList();
        var appliedVersion = await repository.ApplyPendingRulesAsync(
            project.Id,
            logic.Id,
            generation.VersionName,
            $"Generated code from padding branch {generation.GeneratedCommitSha ?? generation.HandoffCommitSha}.",
            currentVersion.DirectoryStructure,
            AppendFeatureList(currentVersion.FeatureList, generation.VersionName, "Applied", generatedRules),
            $"Published generated code from padding branch at {DateTime.UtcNow:O}.",
            diffRules,
            cancellationToken);

        await repository.UpdateGenerationStatusAsync(
            generation.Id,
            "Published",
            100,
            generation.GeneratedCommitSha,
            $"Published Edge AI code version {appliedVersion.VersionName}.",
            cancellationToken);

        return new GitOperationResult(true, $"Published Edge AI code version {appliedVersion.VersionName}.", generation.GeneratedCommitSha);
    }

    public async Task<bool> DeletePendingRuleAsync(
        int adminId,
        int ruleId,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireCodeManager(project);
        return await repository.DeletePendingRuleAsync(project.Id, ruleId, cancellationToken);
    }

    public async Task CreateInstanceAsync(
        int adminId,
        int logicId,
        int edgeDeviceId,
        string instanceName,
        CancellationToken cancellationToken)
    {
        var dashboard = await GetDashboardAsync(adminId, cancellationToken);
        RequireCodeManager(dashboard.Project);
        if (!dashboard.Logics.Any(logic => logic.Id == logicId) ||
            !dashboard.EdgeDevices.Any(device => device.Id == edgeDeviceId))
        {
            throw new InvalidOperationException("Select a valid Edge AI logic and edge device.");
        }

        var logic = dashboard.Logics.First(logic => logic.Id == logicId);
        var currentVersion = logic.Versions.FirstOrDefault(version => version.IsCurrent);
        var device = dashboard.EdgeDevices.First(device => device.Id == edgeDeviceId);
        var normalizedInstanceName = string.IsNullOrWhiteSpace(instanceName)
            ? $"{device.Name} {logic.Name}"
            : instanceName.Trim();

        await repository.CreateInstanceAsync(
            logicId,
            edgeDeviceId,
            currentVersion?.Id,
            normalizedInstanceName,
            "Pending",
            cancellationToken);
    }

    private async Task<Project> RequireProjectAsync(int adminId, CancellationToken cancellationToken)
    {
        return await projectRepository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Create a project before managing Edge AI code.");
    }

    private static void RequireCodeManager(Project project)
    {
        if (!project.CanManageCode)
        {
            throw new UnauthorizedAccessException("This project role cannot manage AI code.");
        }
    }

    private async Task<EdgeAiCodeGeneration?> RefreshGenerationAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        var generation = await repository.GetActiveGenerationAsync(project.Id, cancellationToken);
        if (generation is null ||
            !string.Equals(generation.Status, "Generating", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(generation.HandoffCommitSha))
        {
            return generation;
        }

        var branchHead = await gitService.ResolveBranchHeadAsync(project, generation.BranchName, cancellationToken);
        if (!branchHead.Success || string.IsNullOrWhiteSpace(branchHead.CommitSha))
        {
            if (IsGenerationTimedOut(generation))
            {
                return await MarkGenerationTimedOutAsync(generation, cancellationToken);
            }

            return generation with
            {
                ProgressPercent = EstimateGenerationProgress(generation),
                StatusMessage = "Waiting for the padding branch to become available."
            };
        }

        if (!string.Equals(branchHead.CommitSha, generation.HandoffCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            var codeChanges = await gitService.VerifyGeneratedCodeChangesAsync(
                project,
                generation.HandoffCommitSha,
                branchHead.CommitSha,
                cancellationToken);
            if (!codeChanges.Success)
            {
                const string noChangesMessage = "Code generation finished, but no code changes were detected. Publishing is disabled for this run.";
                await repository.UpdateGenerationStatusAsync(
                    generation.Id,
                    "NoChanges",
                    100,
                    branchHead.CommitSha,
                    noChangesMessage,
                    cancellationToken);
                return generation with
                {
                    Status = "NoChanges",
                    ProgressPercent = 100,
                    GeneratedCommitSha = branchHead.CommitSha,
                    StatusMessage = noChangesMessage
                };
            }

            await repository.UpdateGenerationStatusAsync(
                generation.Id,
                "ReadyToPublish",
                100,
                branchHead.CommitSha,
                "Generated code is available on the padding branch. Ready to publish.",
                cancellationToken);
            return generation with
            {
                Status = "ReadyToPublish",
                ProgressPercent = 100,
                GeneratedCommitSha = branchHead.CommitSha,
                StatusMessage = "Generated code is available on the padding branch. Ready to publish."
            };
        }

        if (IsGenerationTimedOut(generation))
        {
            return await MarkGenerationTimedOutAsync(generation, cancellationToken);
        }

        return generation with
        {
            ProgressPercent = EstimateGenerationProgress(generation),
            StatusMessage = BuildGenerationWaitMessage(generation)
        };
    }

    private async Task<EdgeAiCodeGeneration> MarkGenerationTimedOutAsync(
        EdgeAiCodeGeneration generation,
        CancellationToken cancellationToken)
    {
        const string timeoutMessage = "Code generation timed out. No generated commit was detected on the padding branch. You can update the code again.";
        await repository.UpdateGenerationStatusAsync(
            generation.Id,
            "TimedOut",
            100,
            generation.GeneratedCommitSha,
            timeoutMessage,
            cancellationToken);
        return generation with
        {
            Status = "TimedOut",
            ProgressPercent = 100,
            StatusMessage = timeoutMessage
        };
    }

    private static bool IsGenerationTimedOut(EdgeAiCodeGeneration generation)
    {
        return DateTime.UtcNow - generation.CreatedAtUtc >= GenerationTimeout;
    }

    private static int EstimateGenerationProgress(EdgeAiCodeGeneration generation)
    {
        var elapsedMinutes = Math.Max(0, (DateTime.UtcNow - generation.CreatedAtUtc).TotalMinutes);
        var estimated = 35 + (int)Math.Min(50, elapsedMinutes / 10d * 50);
        return Math.Clamp(Math.Max(generation.ProgressPercent, estimated), 35, 85);
    }

    private static string BuildGenerationWaitMessage(EdgeAiCodeGeneration generation)
    {
        var elapsed = DateTime.UtcNow - generation.CreatedAtUtc;
        var target = TimeSpan.FromMinutes(10);
        var remaining = target - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return "Still generating. This is taking longer than usual; the page will keep checking the padding branch.";
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Generating code on the padding branch. Estimated remaining time: about {minutes} minute(s).";
    }

    private static Project ProjectWithProjectedRules(
        Project project,
        EdgeAiCodeVersion currentVersion,
        IReadOnlyList<GeneratedProjectRule> generatedRules,
        string changeType)
    {
        var baseRules = project.Rules
            .Where(rule => rule.EdgeAiCodeVersionId == currentVersion.Id || rule.EdgeAiCodeVersionId is null)
            .Where(rule => string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule.ChangeType, "Added", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var existing = baseRules.ToList();
        if (string.Equals(changeType, "Removed", StringComparison.OrdinalIgnoreCase))
        {
            existing = existing
                .Where(rule => !generatedRules.Any(generated => SameRuleIntent(rule, generated)))
                .ToList();
        }
        else
        {
            existing.AddRange(generatedRules.Select(rule => new ProjectRule
            {
                ProjectId = project.Id,
                EdgeAiCodeVersionId = currentVersion.Id,
                ChangeType = "Added",
                Dimension = rule.Dimension,
                RuleText = rule.RuleText
            }));
        }

        return project with { Rules = existing };
    }

    private static string DetectChangeType(string prompt, IReadOnlyList<GeneratedProjectRule> generatedRules)
    {
        var text = $"{prompt} {string.Join(' ', generatedRules.Select(rule => rule.RuleText))}";
        string[] removeTerms =
        [
            "remove",
            "delete",
            "disable",
            "turn off",
            "no longer",
            "stop",
            "\u53d6\u6d88",
            "\u5220\u9664",
            "\u79fb\u9664",
            "\u51cf\u6389",
            "\u53bb\u6389",
            "\u7981\u7528",
            "\u4e0d\u8981",
            "\u505c\u6b62",
            "\u5173\u95ed"
        ];
        return removeTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? "Removed"
            : "Added";
    }

    private static IReadOnlyList<ProjectRule> GetPendingDiffRules(Project project)
    {
        return project.Rules
            .Where(rule => string.Equals(rule.ChangeType, "PendingAdded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule.ChangeType, "PendingRemoved", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string DetermineVersionBump(
        IReadOnlyList<GeneratedProjectRule> generatedRules,
        ProjectRequirementSummary projectedSummary)
    {
        if (generatedRules.Any(rule => IsResponse(rule)))
        {
            return "Patch";
        }

        if (generatedRules.Any(rule => IsEvent(rule)))
        {
            return "Minor";
        }

        var recognitionCount = projectedSummary.EnvironmentRecognition.Count + projectedSummary.EventRecognition.Count;
        if (recognitionCount > 3 && projectedSummary.LogicRequirements.Count > 3)
        {
            return "Major";
        }

        return "Minor";
    }

    private static string IncrementVersion(string currentVersion, string versionBump)
    {
        var parts = currentVersion.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToList();
        while (parts.Count < 3)
        {
            parts.Add(0);
        }

        if (string.Equals(versionBump, "Major", StringComparison.OrdinalIgnoreCase))
        {
            return $"{parts[0] + 1}.0";
        }

        if (string.Equals(versionBump, "Patch", StringComparison.OrdinalIgnoreCase))
        {
            return $"{parts[0]}.{parts[1]}.{parts[2] + 1}";
        }

        return $"{parts[0]}.{parts[1] + 1}";
    }

    private static string AppendFeatureList(
        string currentFeatureList,
        string versionName,
        string changeType,
        IReadOnlyList<GeneratedProjectRule> rules)
    {
        var ruleLines = rules.Select(rule => $"  - [{rule.Dimension}] {rule.RuleText}");
        return string.Join(
            Environment.NewLine,
            currentFeatureList.TrimEnd(),
            string.Empty,
            $"Version {versionName} requirement change ({changeType}):",
            string.Join(Environment.NewLine, ruleLines));
    }

    private static bool SameRuleIntent(ProjectRule existing, GeneratedProjectRule generated)
    {
        return string.Equals(existing.Dimension, generated.Dimension, StringComparison.OrdinalIgnoreCase) ||
            existing.RuleText.Contains(generated.RuleText, StringComparison.OrdinalIgnoreCase) ||
            generated.RuleText.Contains(existing.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GeneratedProjectRule> NormalizeGeneratedRules(
        IReadOnlyList<GeneratedProjectRule> generatedRules,
        string sourcePrompt)
    {
        var normalized = generatedRules
            .Select(rule => new GeneratedProjectRule(NormalizeDimension(rule.Dimension, rule.RuleText), rule.RuleText.Trim()))
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Dimension) && !string.IsNullOrWhiteSpace(rule.RuleText))
            .DistinctBy(rule => $"{rule.Dimension}|{rule.RuleText}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > 0)
        {
            return normalized;
        }

        var fallbackDimension = NormalizeDimension(string.Empty, sourcePrompt);
        return string.IsNullOrWhiteSpace(fallbackDimension)
            ? []
            : [new GeneratedProjectRule(fallbackDimension, sourcePrompt)];
    }

    private static string NormalizeDimension(string dimension, string text)
    {
        if (string.Equals(dimension, "Environment Recognition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dimension, "Recognition Logic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dimension, "Event Recognition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dimension, "Response Method", StringComparison.OrdinalIgnoreCase))
        {
            return dimension;
        }

        var combined = $"{dimension} {text}";
        if (ContainsAny(combined, "response", "notify", "alert", "escalate", "operator", "action", "email", "mail", "sms", "\u54cd\u5e94", "\u901a\u77e5", "\u544a\u8b66", "\u5347\u7ea7", "\u5904\u7406", "\u90ae\u4ef6", "\u53d1\u9001"))
        {
            return "Response Method";
        }

        if (ContainsAny(combined, "event", "incident", "level", "severity", "alarm", "\u4e8b\u4ef6", "\u4e8b\u6545", "\u7ea7\u522b", "\u8b66\u62a5"))
        {
            return "Event Recognition";
        }

        if (ContainsAny(combined, "logic", "rule", "policy", "when", "if", "threshold", "per", "within", "\u903b\u8f91", "\u89c4\u5219", "\u7b56\u7565", "\u6bcf", "\u4e0d\u8d85\u8fc7", "\u6700\u591a", "\u9608\u503c"))
        {
            return "Recognition Logic";
        }

        if (ContainsAny(combined, "environment", "detect", "camera", "ppe", "helmet", "vest", "zone", "site", "worker", "\u73af\u5883", "\u8bc6\u522b", "\u68c0\u6d4b", "\u6444\u50cf", "\u5b89\u5168\u5e3d", "\u53cd\u5149\u8863", "\u5de5\u4eba", "\u533a\u57df"))
        {
            return "Environment Recognition";
        }

        return string.Empty;
    }

    private static bool IsResponse(GeneratedProjectRule rule)
    {
        return ContainsAny($"{rule.Dimension} {rule.RuleText}", "response", "notify", "alert", "escalate", "operator", "action", "email", "mail", "\u54cd\u5e94", "\u901a\u77e5", "\u544a\u8b66", "\u5347\u7ea7", "\u5904\u7406", "\u90ae\u4ef6");
    }

    private static bool IsEvent(GeneratedProjectRule rule)
    {
        return ContainsAny($"{rule.Dimension} {rule.RuleText}", "event", "incident", "level", "access", "\u4e8b\u4ef6", "\u4e8b\u6545", "\u7ea7\u522b", "\u8bc6\u522b");
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMeaningfulRulePrompt(string prompt)
    {
        if (prompt.Length < 10)
        {
            return false;
        }

        var meaningfulCharacters = prompt.Count(character =>
            char.IsLetterOrDigit(character) ||
            character is >= '\u4e00' and <= '\u9fff');
        if (meaningfulCharacters < 8)
        {
            return false;
        }

        var uniqueCharacters = prompt
            .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .Distinct()
            .Count();
        if (uniqueCharacters < 6)
        {
            return false;
        }

        string[] fillerTerms = ["test", "asdf", "qwer", "123456", "\u6d4b\u8bd5", "\u968f\u4fbf", "\u4e71\u5199"];
        return !fillerTerms.Any(term => string.Equals(prompt, term, StringComparison.OrdinalIgnoreCase));
    }
}
