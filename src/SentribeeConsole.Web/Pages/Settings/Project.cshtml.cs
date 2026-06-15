using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Settings;

[Authorize]
public class ProjectModel(
    IProjectService projectService,
    IFileStorageService storageService,
    ILogger<ProjectModel> logger) : PageModel
{
    private const long MaxLogoLength = 3 * 1024 * 1024;

    [BindProperty]
    public ProjectInput Input { get; set; } = new();

    public Project Project { get; private set; } = new();

    public IReadOnlyList<ProjectTimeZoneOption> TimeZoneOptions => ProjectTimeZone.Options;

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    public string? GeneratedApiKey { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var project = await LoadProjectAsync(cancellationToken);
        if (project is null)
        {
            return Challenge();
        }

        SetPageData(project);
        StatusMessage = TempData["ProjectStatus"] as string;
        StatusIsError = TempData["ProjectStatusError"] as bool? ?? false;
        GeneratedApiKey = TempData["GeneratedApiKey"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var current = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (current is null || !current.CanEditProjectDetails)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            SetPageData(current ?? new Project { AdminId = adminId.Value }, preserveInput: true);
            return Page();
        }

        await projectService.SaveAsync(
            adminId.Value,
            Input.Name,
            Input.Description,
            Input.CompanyName,
            Input.WebsiteUrl,
            Input.TimeZoneId,
            Input.EdgeAiGitRepositoryUrl,
            Input.EdgeAiGitBranch,
            Input.EdgeAiGitWorkingDirectory,
            cancellationToken);
        TempData["ProjectStatus"] = "Project details saved successfully.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGenerateRulesAsync(
        string? rulePrompt,
        CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (project is null || !project.CanEditProjectDetails)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(rulePrompt))
        {
            TempData["ProjectStatus"] = "Describe the rule you want to create first.";
            TempData["ProjectStatusError"] = true;
            return RedirectToPage();
        }

        try
        {
            await projectService.GenerateRulesAsync(adminId.Value, rulePrompt, cancellationToken);
            TempData["ProjectStatus"] = "Project rules generated and saved.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Rule generation failed for administrator {AdminId}.", adminId.Value);
            TempData["ProjectStatus"] = "Rule generation failed. Please review the description and try again.";
            TempData["ProjectStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSwitchProjectAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        await projectService.SwitchCurrentProjectAsync(adminId.Value, projectId, cancellationToken);
        return Redirect(Request.Headers.Referer.ToString() is { Length: > 0 } referer
            ? referer
            : Url.Page("/Dashboard/Index") ?? "/dashboard");
    }

    public async Task<IActionResult> OnPostGenerateApiKeyAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        try
        {
            var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
            if (project is null || !project.CanManageProjectApiKey)
            {
                return Forbid();
            }

            var generated = await projectService.GenerateApiKeyAsync(adminId.Value, cancellationToken);
            TempData["ProjectStatus"] = "Project API key generated. Copy it now; it will not be shown again.";
            TempData["GeneratedApiKey"] = generated.ApiKey;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "API key generation failed for administrator {AdminId}.", adminId.Value);
            TempData["ProjectStatus"] = "API key generation failed. Save the project and try again.";
            TempData["ProjectStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteRuleAsync(
        int ruleId,
        CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (project is null || !project.CanEditProjectDetails)
        {
            return Forbid();
        }

        var removed = await projectService.DeleteRuleAsync(adminId.Value, ruleId, cancellationToken);
        TempData["ProjectStatus"] = removed ? "Project rule removed." : "The project rule could not be found.";
        TempData["ProjectStatusError"] = !removed;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLogoAsync(
        IFormFile? logo,
        CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Unauthorized();
        }

        var project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken);
        if (project is null)
        {
            return new JsonResult(new { success = false, message = "Save the project details before uploading a logo." })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }
        if (!project.CanEditProjectDetails)
        {
            return new JsonResult(new { success = false, message = "Only project administrators can upload a logo." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        if (logo is null || logo.Length == 0 ||
            logo.Length > MaxLogoLength ||
            !string.Equals(logo.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { success = false, message = "The logo must be a cropped JPEG image under 3 MB." })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        try
        {
            await using var stream = logo.OpenReadStream();
            var storedFile = await storageService.UploadAsync(
                stream,
                "image/jpeg",
                ".jpg",
                $"project-logos/{project.Id}",
                cancellationToken);
            await projectService.UpdateLogoAsync(adminId.Value, storedFile.PublicUrl, cancellationToken);
            var displayLogoUrl = BuildVersionedProjectLogoProxyUrl();

            return new JsonResult(new
            {
                success = true,
                logoUrl = storedFile.PublicUrl,
                displayLogoUrl,
                message = "Project logo updated successfully."
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Project logo upload failed for project {ProjectId}.", project.Id);
            return new JsonResult(new { success = false, message = "Project logo upload failed. Please try again." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task<Project?> LoadProjectAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        return adminId.HasValue
            ? await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken)
            : null;
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    private void SetPageData(Project project, bool preserveInput = false)
    {
        Project = project;
        if (!preserveInput)
        {
            Input = new ProjectInput
            {
                Name = project.Name,
                Description = project.Description,
                CompanyName = project.CompanyName,
                WebsiteUrl = project.WebsiteUrl,
                TimeZoneId = project.TimeZoneId,
                EdgeAiGitRepositoryUrl = project.EdgeAiGitRepositoryUrl,
                EdgeAiGitBranch = project.EdgeAiGitBranch,
                EdgeAiGitWorkingDirectory = project.EdgeAiGitWorkingDirectory
            };
        }
    }

    private static string BuildVersionedProjectLogoProxyUrl()
    {
        return $"/api/projects/current/logo?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    public sealed class ProjectInput
    {
        [Required]
        [StringLength(150)]
        [Display(Name = "Project Name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(2000)]
        [Display(Name = "Project Description")]
        public string? Description { get; set; }

        [StringLength(150)]
        [Display(Name = "Company Name")]
        public string? CompanyName { get; set; }

        [Url]
        [StringLength(500)]
        [Display(Name = "Website URL")]
        public string? WebsiteUrl { get; set; }

        [Required]
        [StringLength(80)]
        [Display(Name = "Project Time Zone")]
        public string TimeZoneId { get; set; } = ProjectTimeZone.DefaultId;

        [StringLength(500)]
        [Display(Name = "AI Code Git Repository")]
        public string? EdgeAiGitRepositoryUrl { get; set; } = Project.DefaultEdgeAiGitRepositoryUrl;

        [Required]
        [StringLength(100)]
        [Display(Name = "Git Branch")]
        public string EdgeAiGitBranch { get; set; } = Project.DefaultEdgeAiGitBranch;

        [StringLength(500)]
        [Display(Name = "Local Working Directory")]
        public string? EdgeAiGitWorkingDirectory { get; set; }
    }
}
