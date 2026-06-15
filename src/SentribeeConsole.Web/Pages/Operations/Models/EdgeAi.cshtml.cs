using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.Runtime;

namespace SentribeeConsole.Web.Pages.Operations.Models;

[Authorize]
public sealed class EdgeAiModel(
    IEdgeAiService edgeAiService,
    IYoloModelService yoloModelService) : PageModel
{
    [BindProperty]
    public InstanceInput Input { get; set; } = new();

    [BindProperty]
    public VersionRuleInput RuleInput { get; set; } = new();

    [BindProperty]
    public string GitRevision { get; set; } = string.Empty;

    public EdgeAiDashboard Dashboard { get; private set; } = new();

    public PagedResult<EdgeAiInstanceRow> InstancePage { get; private set; } = new();

    public YoloModelVersion? CurrentYoloModel { get; private set; }

    public IReadOnlyList<EdgeRuntimeEnvironmentMapping> RuntimeEnvironmentMappings { get; } = EdgeRuntimeEnvironmentMap.Items;

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int InstancePageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        await LoadPageAsync(adminId.Value, cancellationToken);
        if (!Dashboard.Project.CanManageCode)
        {
            return Forbid();
        }

        StatusMessage = TempData["EdgeAiStatus"] as string;
        StatusIsError = TempData["EdgeAiStatusError"] as bool? ?? false;
        return Page();
    }

    public async Task<IActionResult> OnGetGenerationStatusAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return new JsonResult(new { authenticated = false });
        }

        await LoadPageAsync(adminId.Value, cancellationToken);
        if (!Dashboard.Project.CanManageCode)
        {
            return new JsonResult(new { authenticated = true, authorized = false }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        var generation = Dashboard.ActiveGeneration;
        return new JsonResult(new
        {
            authenticated = true,
            active = generation is not null,
            status = generation?.Status,
            message = generation?.StatusMessage,
            progress = generation?.ProgressPercent ?? 0,
            version = generation?.VersionName,
            branch = generation?.BranchName,
            commit = generation?.GeneratedCommitSha,
            commitShort = ShortSha(generation?.GeneratedCommitSha),
            ready = string.Equals(generation?.Status, "ReadyToPublish", StringComparison.OrdinalIgnoreCase),
            timedOut = string.Equals(generation?.Status, "TimedOut", StringComparison.OrdinalIgnoreCase),
            failed = string.Equals(generation?.Status, "Failed", StringComparison.OrdinalIgnoreCase),
            noChanges = string.Equals(generation?.Status, "NoChanges", StringComparison.OrdinalIgnoreCase),
            gitUrl = generation is null ? null : GetGitBranchUrl(generation.BranchName)
        });
    }

    public async Task<IActionResult> OnPostCreateInstanceAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (Input.LogicId <= 0 || Input.EdgeDeviceId <= 0)
        {
            TempData["EdgeAiStatus"] = "Select a valid Edge AI logic and edge device.";
            TempData["EdgeAiStatusError"] = true;
            return RedirectToPage();
        }

        await edgeAiService.CreateInstanceAsync(
            adminId.Value,
            Input.LogicId,
            Input.EdgeDeviceId,
            Input.InstanceName,
            cancellationToken);
        TempData["EdgeAiStatus"] = "Edge AI instance created.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRollbackAsync(int versionId, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var restored = await edgeAiService.RollbackAsync(adminId.Value, versionId, cancellationToken);
        TempData["EdgeAiStatus"] = restored ? "AI code version restored." : "Code version could not be restored.";
        TempData["EdgeAiStatusError"] = !restored;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncGitAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var result = await edgeAiService.SyncGitAsync(adminId.Value, cancellationToken);
        TempData["EdgeAiStatus"] = result.Success
            ? $"Git update complete. HEAD {ShortSha(result.CommitSha)}"
            : result.Message;
        TempData["EdgeAiStatusError"] = !result.Success;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCheckoutGitAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var result = await edgeAiService.CheckoutGitRevisionAsync(adminId.Value, GitRevision, cancellationToken);
        TempData["EdgeAiStatus"] = result.Success
            ? $"Git checkout complete. HEAD {ShortSha(result.CommitSha)}"
            : result.Message;
        TempData["EdgeAiStatusError"] = !result.Success;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostHandOffGitAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var result = await edgeAiService.HandOffPendingRulesToGitAsync(adminId.Value, cancellationToken);
        TempData["EdgeAiStatus"] = result.Message;
        TempData["EdgeAiStatusError"] = !result.Success;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPublishGeneratedAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var result = await edgeAiService.PublishGeneratedCodeAsync(adminId.Value, cancellationToken);
        TempData["EdgeAiStatus"] = result.Message;
        TempData["EdgeAiStatusError"] = !result.Success;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var deleted = await edgeAiService.DeletePendingRuleAsync(adminId.Value, ruleId, cancellationToken);
        TempData["EdgeAiStatus"] = deleted ? "Pending rule removed." : "Pending rule could not be removed.";
        TempData["EdgeAiStatusError"] = !deleted;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddRuleAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(RuleInput.Prompt))
        {
            TempData["EdgeAiStatus"] = "Describe the rule requirement before adding a version.";
            TempData["EdgeAiStatusError"] = true;
            return RedirectToPage();
        }

        try
        {
            var result = await edgeAiService.AddVersionRuleAsync(
                adminId.Value,
                RuleInput.Prompt,
                cancellationToken);
            TempData["EdgeAiStatus"] =
                $"{result.ChangeType} {result.RuleCount} rule(s). Virtual AI code version {result.VersionName} ({result.VersionBump}) is ready for review.";
        }
        catch (Exception exception)
        {
            TempData["EdgeAiStatus"] = $"Add rule version failed: {exception.Message}";
            TempData["EdgeAiStatusError"] = true;
        }

        return RedirectToPage();
    }

    private async Task LoadPageAsync(int adminId, CancellationToken cancellationToken)
    {
        Dashboard = await edgeAiService.GetDashboardAsync(adminId, cancellationToken);
        CurrentYoloModel = (await yoloModelService.GetDashboardAsync(adminId, new EdgeEventFilters(), 1, 5, 1, 5, cancellationToken)).CurrentVersion;
        var rows = Dashboard.Logics
            .SelectMany(logic => logic.Instances.Select(instance => new EdgeAiInstanceRow
            {
                LogicName = logic.Name,
                Instance = instance
            }))
            .OrderByDescending(row => row.Instance.CreatedAtUtc)
            .ToList();
        const int pageSize = 10;
        var pageNumber = Math.Max(1, InstancePageNumber);
        InstancePage = new PagedResult<EdgeAiInstanceRow>
        {
            Items = rows.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList(),
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = rows.Count
        };

        RuleInput = new VersionRuleInput();
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    private static string ShortSha(string? commitSha)
    {
        return string.IsNullOrWhiteSpace(commitSha)
            ? "unknown"
            : commitSha[..Math.Min(8, commitSha.Length)];
    }

    public sealed class InstanceInput
    {
        [Required]
        [Display(Name = "Logic")]
        public int LogicId { get; set; }

        [Required]
        public int EdgeDeviceId { get; set; }

        [StringLength(150)]
        [Display(Name = "Instance Name")]
        public string InstanceName { get; set; } = string.Empty;
    }

    public sealed class VersionRuleInput
    {
        [StringLength(1200)]
        [Display(Name = "Rule Requirement")]
        public string Prompt { get; set; } = string.Empty;
    }

    public IReadOnlyList<ProjectRule> PendingRules =>
        Dashboard.Project.Rules
            .Where(rule => string.Equals(rule.ChangeType, "PendingAdded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule.ChangeType, "PendingRemoved", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => rule.CreatedAtUtc)
            .ToList();

    public string VirtualPendingVersionName => IncrementVersion(
        Dashboard.CurrentVersion?.VersionName ?? "1.0",
        PendingRules.Any(rule => rule.Dimension.Contains("Response", StringComparison.OrdinalIgnoreCase))
            ? "Patch"
            : PendingRules.Any(rule => rule.Dimension.Contains("Event", StringComparison.OrdinalIgnoreCase))
                ? "Minor"
                : "Minor");

    public IReadOnlyList<ProjectRule> GetVersionDiffRules(int versionId)
    {
        return Dashboard.Project.Rules
            .Where(rule => rule.EdgeAiCodeVersionId == versionId)
            .Where(rule => !string.Equals(rule.ChangeType, "Active", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<ProjectRule> GetVersionDiffRules(int versionId, string group)
    {
        var rules = GetVersionDiffRules(versionId);
        return group switch
        {
            "Environment" => rules
                .Where(rule => rule.Dimension.Contains("Environment", StringComparison.OrdinalIgnoreCase) ||
                    rule.Dimension.Contains("Recognition", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Logic" => rules
                .Where(rule => rule.Dimension.Contains("Logic", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Event" => rules
                .Where(rule => rule.Dimension.Contains("Event", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Response" => rules
                .Where(rule => rule.Dimension.Contains("Response", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            _ => []
        };
    }

    public IReadOnlyList<ProjectRule> GetPendingRules(string group)
    {
        return group switch
        {
            "Environment" => PendingRules
                .Where(rule => rule.Dimension.Equals("Environment Recognition", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Logic" => PendingRules
                .Where(rule => rule.Dimension.Equals("Recognition Logic", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Event" => PendingRules
                .Where(rule => rule.Dimension.Equals("Event Recognition", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "Response" => PendingRules
                .Where(rule => rule.Dimension.Equals("Response Method", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            _ => []
        };
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

        return string.Equals(versionBump, "Patch", StringComparison.OrdinalIgnoreCase)
            ? $"{parts[0]}.{parts[1]}.{parts[2] + 1}"
            : $"{parts[0]}.{parts[1] + 1}";
    }

    public sealed record EdgeAiInstanceRow
    {
        public string LogicName { get; init; } = string.Empty;

        public EdgeAiInstance Instance { get; init; } = new();
    }

    public static IReadOnlyList<string> GetRequiredDevices(EdgeAiCodeVersion? version)
    {
        if (version is null)
        {
            return [];
        }

        var text = string.Join(' ', version.Description, version.DirectoryStructure, version.FeatureList, version.Notes);
        var required = new List<string>();
        if (text.Contains("RTSP", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("camera", StringComparison.OrdinalIgnoreCase))
        {
            required.Add("RTSP Camera");
        }

        if (text.Contains("BLE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bracelet", StringComparison.OrdinalIgnoreCase))
        {
            required.Add("Bluetooth Gateway");
        }

        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string GetGitRepositoryUrl()
    {
        var repositoryUrl = Dashboard.Project.EdgeAiGitRepositoryUrl;
        if (repositoryUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            repositoryUrl = repositoryUrl["git@github.com:".Length..];
            repositoryUrl = repositoryUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? repositoryUrl[..^4]
                : repositoryUrl;
            return $"https://github.com/{repositoryUrl}";
        }

        return repositoryUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryUrl[..^4]
            : repositoryUrl;
    }

    public string GetGitBranchUrl(string? branchName = null)
    {
        var branch = string.IsNullOrWhiteSpace(branchName)
            ? Dashboard.Project.EdgeAiGitBranch
            : branchName;
        return $"{GetGitRepositoryUrl()}/tree/{Uri.EscapeDataString(branch)}";
    }
}
