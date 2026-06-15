using System.Diagnostics;
using System.Text;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Git;

public sealed class EdgeAiGitService : IEdgeAiGitService
{
    private const string HandoffFolder = "edge-ai-requirements";

    public async Task<GitOperationResult> SyncAsync(Project project, CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        var worktree = project.EdgeAiGitWorkingDirectory!;
        if (!Directory.Exists(Path.Combine(worktree, ".git")))
        {
            var parent = Path.GetDirectoryName(worktree);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var clone = await RunGitAsync(
                null,
                cancellationToken,
                "clone",
                "--branch",
                project.EdgeAiGitBranch,
                "--single-branch",
                project.EdgeAiGitRepositoryUrl,
                worktree);
            if (!clone.Success)
            {
                return clone with { Message = $"Git clone failed. {clone.Message}" };
            }
        }
        else
        {
            var fetch = await RunGitAsync(worktree, cancellationToken, "fetch", "origin", project.EdgeAiGitBranch);
            if (!fetch.Success)
            {
                return fetch with { Message = $"Git fetch failed. {fetch.Message}" };
            }

            var checkout = await RunGitAsync(worktree, cancellationToken, "checkout", project.EdgeAiGitBranch);
            if (!checkout.Success)
            {
                return checkout with { Message = $"Git checkout failed. {checkout.Message}" };
            }

            var pull = await RunGitAsync(worktree, cancellationToken, "pull", "--ff-only", "origin", project.EdgeAiGitBranch);
            if (!pull.Success)
            {
                return pull with { Message = $"Git pull failed. {pull.Message}" };
            }
        }

        var head = await ResolveHeadAsync(worktree, cancellationToken);
        return head.Success
            ? new GitOperationResult(true, "Edge AI code updated from Git.", head.CommitSha)
            : head;
    }

    public async Task<GitOperationResult> CheckoutAsync(
        Project project,
        string revision,
        CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(revision))
        {
            return new GitOperationResult(false, "Enter a commit, tag, or branch to checkout.");
        }

        var worktree = project.EdgeAiGitWorkingDirectory!;
        if (!Directory.Exists(Path.Combine(worktree, ".git")))
        {
            return new GitOperationResult(false, "Sync the Git repository before checking out a revision.");
        }

        var fetch = await RunGitAsync(worktree, cancellationToken, "fetch", "--all", "--tags");
        if (!fetch.Success)
        {
            return fetch with { Message = $"Git fetch failed. {fetch.Message}" };
        }

        var checkout = await RunGitAsync(worktree, cancellationToken, "checkout", revision.Trim());
        if (!checkout.Success)
        {
            return checkout with { Message = $"Git checkout failed. {checkout.Message}" };
        }

        var head = await ResolveHeadAsync(worktree, cancellationToken);
        return head.Success
            ? new GitOperationResult(true, "Edge AI code checked out from Git.", head.CommitSha)
            : head;
    }

    public async Task<GitOperationResult> CreateDevelopmentHandoffAsync(
        Project project,
        EdgeAiCodeVersion version,
        IReadOnlyList<ProjectRule> diffRules,
        CancellationToken cancellationToken)
    {
        return await CreateHandoffOnBranchAsync(
            project,
            version,
            diffRules,
            $"edge-ai-rules/{SanitizeBranchToken(version.VersionName)}",
            createFromCurrentBranch: false,
            cancellationToken);
    }

    public async Task<GitOperationResult> CreatePaddingHandoffAsync(
        Project project,
        EdgeAiCodeVersion version,
        IReadOnlyList<ProjectRule> diffRules,
        CancellationToken cancellationToken)
    {
        return await CreateHandoffOnBranchAsync(
            project,
            version,
            diffRules,
            "padding",
            createFromCurrentBranch: true,
            cancellationToken);
    }

    public async Task<GitOperationResult> StartPaddingCodeGenerationAsync(
        Project project,
        EdgeAiCodeVersion version,
        CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        var worktree = project.EdgeAiGitWorkingDirectory!;
        if (!Directory.Exists(Path.Combine(worktree, ".git")))
        {
            return new GitOperationResult(false, "Configure an existing Git working directory before running Codex generation.");
        }

        var codexCheck = await RunProcessAsync(
            "bash",
            worktree,
            cancellationToken,
            "-lc",
            "command -v codex");
        if (!codexCheck.Success)
        {
            return codexCheck with { Message = "Codex CLI is not installed on the server. Install it with npm install -g @openai/codex." };
        }

        var runFolder = Path.Combine(Path.GetTempPath(), "sentribee-codex-generation");
        Directory.CreateDirectory(runFolder);
        var token = $"{SanitizeFileToken(version.VersionName)}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var promptPath = Path.Combine(runFolder, $"prompt-{token}.md");
        var scriptPath = Path.Combine(runFolder, $"run-{token}.sh");
        var logPath = Path.Combine(runFolder, $"run-{token}.log");

        await File.WriteAllTextAsync(
            promptPath,
            BuildCodexPrompt(version),
            Encoding.UTF8,
            cancellationToken);
        await File.WriteAllTextAsync(
            scriptPath,
            BuildCodexGenerationScript(worktree, promptPath, logPath, version.VersionName),
            Encoding.UTF8,
            cancellationToken);

        var chmod = await RunProcessAsync("chmod", worktree, cancellationToken, "+x", scriptPath);
        if (!chmod.Success)
        {
            return chmod with { Message = $"Unable to prepare Codex generation script. {chmod.Message}" };
        }

        var startInfo = new ProcessStartInfo("bash", scriptPath)
        {
            WorkingDirectory = worktree,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            UseShellExecute = false
        };
        AddCodexEnvironment(startInfo);

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            return new GitOperationResult(false, exception.Message);
        }

        return new GitOperationResult(true, $"Local Codex generation started. Log: {logPath}");
    }

    public async Task<GitOperationResult> ResolveBranchHeadAsync(
        Project project,
        string branchName,
        CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(project.EdgeAiGitWorkingDirectory) ||
            !Directory.Exists(Path.Combine(project.EdgeAiGitWorkingDirectory, ".git")))
        {
            return new GitOperationResult(false, "Configure an existing Git working directory before checking generation progress.");
        }

        var showRef = await RunGitAsync(project.EdgeAiGitWorkingDirectory, cancellationToken, "rev-parse", branchName);
        return showRef.Success
            ? showRef with { CommitSha = showRef.Message.Trim() }
            : showRef with { Message = $"Unable to resolve branch {branchName}. {showRef.Message}" };
    }

    public async Task<GitOperationResult> VerifyGeneratedCodeChangesAsync(
        Project project,
        string baseCommitSha,
        string headCommitSha,
        CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(project.EdgeAiGitWorkingDirectory) ||
            !Directory.Exists(Path.Combine(project.EdgeAiGitWorkingDirectory, ".git")))
        {
            return new GitOperationResult(false, "Configure an existing Git working directory before checking generated code changes.");
        }

        var diff = await RunGitAsync(
            project.EdgeAiGitWorkingDirectory,
            cancellationToken,
            "diff",
            "--name-only",
            baseCommitSha,
            headCommitSha);
        if (!diff.Success)
        {
            return diff with { Message = $"Unable to inspect generated code changes. {diff.Message}" };
        }

        var changedFiles = diff.Message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(file => !file.StartsWith($"{HandoffFolder}/", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.StartsWith(".sentribee/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return changedFiles.Count == 0
            ? new GitOperationResult(false, "No generated code changes were detected on the padding branch.", headCommitSha)
            : new GitOperationResult(true, string.Join(Environment.NewLine, changedFiles), headCommitSha);
    }

    private static async Task<GitOperationResult> CreateHandoffOnBranchAsync(
        Project project,
        EdgeAiCodeVersion version,
        IReadOnlyList<ProjectRule> diffRules,
        string branchName,
        bool createFromCurrentBranch,
        CancellationToken cancellationToken)
    {
        var validation = ValidateProjectGit(project);
        if (validation is not null)
        {
            return validation;
        }

        var worktree = project.EdgeAiGitWorkingDirectory!;
        if (!Directory.Exists(Path.Combine(worktree, ".git")))
        {
            return new GitOperationResult(false, "Configure an existing Git working directory for Xcode handoff.");
        }

        var checkout = createFromCurrentBranch
            ? await CheckoutPaddingBranchAsync(project, worktree, branchName, cancellationToken)
            : await RunGitAsync(worktree, cancellationToken, "checkout", "-B", branchName);
        if (!checkout.Success)
        {
            return checkout with { Message = $"Git branch preparation failed. {checkout.Message}" };
        }

        var folder = Path.Combine(worktree, HandoffFolder);
        Directory.CreateDirectory(folder);
        var fileName = $"edge-ai-rules-{SanitizeFileToken(version.VersionName)}.md";
        var relativePath = $"{HandoffFolder}/{fileName}";
        await File.WriteAllTextAsync(
            Path.Combine(folder, fileName),
            BuildHandoffMarkdown(version, diffRules),
            Encoding.UTF8,
            cancellationToken);

        var add = await RunGitAsync(worktree, cancellationToken, "add", relativePath);
        if (!add.Success)
        {
            return add with { Message = $"Git add failed. {add.Message}" };
        }

        var commit = await RunGitAsync(
            worktree,
            cancellationToken,
            "commit",
            "-m",
            $"Add Edge AI rule requirements {version.VersionName}");
        if (!commit.Success && !commit.Message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return commit with { Message = $"Git commit failed. {commit.Message}" };
        }

        var push = await RunGitAsync(worktree, cancellationToken, "push", "-u", "origin", branchName);
        if (!push.Success)
        {
            return push with { Message = $"Git push failed. {push.Message}" };
        }

        var head = await ResolveHeadAsync(worktree, cancellationToken);
        return head.Success
            ? new GitOperationResult(true, $"Development handoff ready on branch {branchName}.", head.CommitSha)
            : head;
    }

    private static async Task<GitOperationResult> CheckoutPaddingBranchAsync(
        Project project,
        string worktree,
        string branchName,
        CancellationToken cancellationToken)
    {
        var exists = await RunGitAsync(worktree, cancellationToken, "rev-parse", "--verify", branchName);
        if (exists.Success)
        {
            return await RunGitAsync(worktree, cancellationToken, "checkout", branchName);
        }

        var checkoutBase = await RunGitAsync(worktree, cancellationToken, "checkout", project.EdgeAiGitBranch);
        if (!checkoutBase.Success)
        {
            return checkoutBase;
        }

        return await RunGitAsync(worktree, cancellationToken, "checkout", "-b", branchName);
    }

    private static GitOperationResult? ValidateProjectGit(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.EdgeAiGitRepositoryUrl))
        {
            return new GitOperationResult(false, "Configure the Edge AI Git repository first.");
        }

        if (string.IsNullOrWhiteSpace(project.EdgeAiGitBranch))
        {
            return new GitOperationResult(false, "Configure the Edge AI Git branch first.");
        }

        if (string.IsNullOrWhiteSpace(project.EdgeAiGitWorkingDirectory))
        {
            return new GitOperationResult(false, "Configure a local working directory before running Git operations.");
        }

        return null;
    }

    private static async Task<GitOperationResult> ResolveHeadAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var head = await RunGitAsync(workingDirectory, cancellationToken, "rev-parse", "HEAD");
        return head.Success
            ? head with { CommitSha = head.Message.Trim() }
            : head with { Message = $"Unable to resolve Git HEAD. {head.Message}" };
    }

    private static async Task<GitOperationResult> RunGitAsync(
        string? workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        return await RunProcessAsync("git", workingDirectory, cancellationToken, arguments);
    }

    private static async Task<GitOperationResult> RunProcessAsync(
        string fileName,
        string? workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new GitOperationResult(false, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var message = BuildMessage(output, error);
        return new GitOperationResult(process.ExitCode == 0, message);
    }

    private static void AddCodexEnvironment(ProcessStartInfo startInfo)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            startInfo.Environment["OPENAI_API_KEY"] = apiKey;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            startInfo.Environment["HOME"] = home;
        }
    }

    private static string BuildMessage(string output, string error)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.Append(output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(error.Trim());
        }

        return builder.Length == 0 ? "No Git output." : builder.ToString();
    }

    private static string BuildHandoffMarkdown(EdgeAiCodeVersion version, IReadOnlyList<ProjectRule> diffRules)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Edge AI Rule Development Handoff {version.VersionName}");
        builder.AppendLine();
        builder.AppendLine($"Version: {version.VersionName}");
        builder.AppendLine($"Created UTC: {DateTime.UtcNow:O}");
        builder.AppendLine();
        builder.AppendLine("## Changed Requirements");
        foreach (var rule in diffRules)
        {
            builder.AppendLine();
            builder.AppendLine($"- Change: {rule.ChangeType}");
            builder.AppendLine($"  Dimension: {rule.Dimension}");
            builder.AppendLine($"  Rule: {rule.RuleText}");
        }

        return builder.ToString();
    }

    private static string BuildCodexPrompt(EdgeAiCodeVersion version)
    {
        return $"""
            You are updating the PREVENX Edge AI safety codebase.

            Work only in this repository and keep the changes focused.

            Read the latest requirement handoff file under edge-ai-requirements/ for version {version.VersionName}. Implement the requested Edge AI rule changes in the codebase. Preserve existing behavior unless the handoff explicitly changes it.

            After editing, run the most relevant lightweight validation available in the repository. Do not create rollback versions or deployment artifacts. Do not commit; the surrounding automation will commit and push your changes after you finish.
            """;
    }

    private static string BuildCodexGenerationScript(
        string worktree,
        string promptPath,
        string logPath,
        string versionName)
    {
        var quotedWorktree = QuoteShell(worktree);
        var quotedPromptPath = QuoteShell(promptPath);
        var quotedLogPath = QuoteShell(logPath);
        var commitMessage = QuoteShell($"Generate Edge AI code {versionName}");
        var noChangesPath = QuoteShell($"{HandoffFolder}/no-code-changes-{SanitizeFileToken(versionName)}.txt");
        return $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            exec >> {{quotedLogPath}} 2>&1
            echo "[sentribee] generation started at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
            cd {{quotedWorktree}}
            git checkout padding
            git pull --ff-only origin padding

            codex exec --sandbox workspace-write --skip-git-repo-check --cd {{quotedWorktree}} - < {{quotedPromptPath}}

            if [ -n "$(git status --porcelain)" ]; then
              git add -A
              git commit -m {{commitMessage}}
              git push origin padding
              echo "[sentribee] generated code committed and pushed"
            else
              mkdir -p {{QuoteShell(HandoffFolder)}}
              printf "Codex completed at %s but did not produce code changes.\n" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > {{noChangesPath}}
              git add {{noChangesPath}}
              git commit -m {{QuoteShell($"Record no generated code changes {versionName}")}}
              git push origin padding
              echo "[sentribee] codex finished with no code changes"
            fi
            echo "[sentribee] generation finished at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
            """;
    }

    private static string QuoteShell(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string SanitizeBranchToken(string value)
    {
        return string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')).Trim('-');
    }

    private static string SanitizeFileToken(string value)
    {
        return string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')).Trim('-');
    }
}
