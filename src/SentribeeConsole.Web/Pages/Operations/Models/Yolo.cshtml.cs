using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.Models;

[Authorize]
public sealed class YoloModel(
    IYoloModelService yoloModelService,
    IEdgeDeviceService edgeDeviceService,
    ILogger<YoloModel> logger) : PageModel
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();

    [BindProperty]
    public AddClassInput ClassInput { get; set; } = new();

    public YoloModelDashboard Dashboard { get; private set; } = new();

    public IReadOnlyList<EdgeDevice> Devices { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    public bool StatusIsError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int ScenePageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int SubjectPageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public string ModelTab { get; set; } = "panorama";

    [BindProperty(SupportsGet = true)]
    public int? DeviceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateTo { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        await LoadPageAsync(adminId.Value, cancellationToken);
        if (!Dashboard.Project.CanViewModels)
        {
            return Forbid();
        }

        StatusMessage = TempData["YoloStatus"] as string;
        StatusIsError = TempData["YoloStatusError"] as bool? ?? false;
        return Page();
    }

    public async Task<IActionResult> OnPostScheduleAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(ClassInput.Name) || ClassInput.Name.Trim().Length < 2)
        {
            ModelState.AddModelError(nameof(ClassInput.Name), "Enter a meaningful class name.");
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }

        await yoloModelService.SetScheduleAsync(
            adminId.Value,
            Input.NextTrainingLocal,
            Input.AutoSchedule,
            cancellationToken);
        TempData["YoloStatus"] = Input.AutoSchedule
            ? "Automatic training schedule enabled."
            : "Next training time saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTrainNowAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        try
        {
            await yoloModelService.RequestTrainingAsync(adminId.Value, cancellationToken);
            TempData["YoloStatus"] = "Training request has been queued.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to queue YOLO training for administrator {AdminId}.", adminId.Value);
            TempData["YoloStatus"] = "Unable to queue training. Please try again.";
            TempData["YoloStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScheduleTonightAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        try
        {
            await yoloModelService.ScheduleTonightAsync(adminId.Value, cancellationToken);
            TempData["YoloStatus"] = "data.yaml has been synced to ins1. Pending panorama images and YOLO labels are being staged in the background. Training starts at 19:00, or immediately after staging if requested after 19:00.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to schedule tonight training for administrator {AdminId}.", adminId.Value);
            TempData["YoloStatus"] = exception.Message;
            TempData["YoloStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSchedulePersonSlicePpeTonightAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        try
        {
            await yoloModelService.SchedulePersonSlicePpeTonightAsync(adminId.Value, cancellationToken);
            TempData["YoloStatus"] = "Pending person slice PPE images and YOLO labels are being staged in the background. Training starts at 19:00, or immediately after staging if requested after 19:00.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to schedule person slice PPE training for administrator {AdminId}.", adminId.Value);
            TempData["YoloStatus"] = exception.Message;
            TempData["YoloStatusError"] = true;
        }

        return RedirectToPage(new { modelTab = "subjects" });
    }

    public async Task<IActionResult> OnPostCancelScheduleAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        await yoloModelService.CancelScheduleAsync(adminId.Value, cancellationToken);
        TempData["YoloStatus"] = "Tonight training schedule has been cancelled.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddClassAsync(CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await LoadPageAsync(adminId.Value, cancellationToken);
            return Page();
        }

        try
        {
            await yoloModelService.AddModelClassAsync(adminId.Value, ClassInput.Name, cancellationToken);
            TempData["YoloStatus"] = "AI model class has been added. It will sync to ins1 before the next training run.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to add AI model class for administrator {AdminId}.", adminId.Value);
            TempData["YoloStatus"] = exception.Message;
            TempData["YoloStatusError"] = true;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRollbackAsync(int versionId, CancellationToken cancellationToken)
    {
        var adminId = GetAdminId();
        if (adminId is null)
        {
            return Challenge();
        }

        var restored = await yoloModelService.RollbackAsync(adminId.Value, versionId, cancellationToken);
        TempData["YoloStatus"] = restored ? "AI model version restored." : "Only trained versions can be restored.";
        TempData["YoloStatusError"] = !restored;
        return RedirectToPage();
    }

    private async Task LoadPageAsync(int adminId, CancellationToken cancellationToken)
    {
        var filters = new EdgeEventFilters
        {
            DeviceId = DeviceId,
            Type = Type,
            DateFrom = DateFrom,
            DateTo = DateTo
        };
        if (PageNumber > 1 && ScenePageNumber == 1)
        {
            ScenePageNumber = PageNumber;
        }

        ModelTab = string.Equals(ModelTab, "subjects", StringComparison.OrdinalIgnoreCase)
            ? "subjects"
            : "panorama";
        Dashboard = await yoloModelService.GetDashboardAsync(adminId, filters, ScenePageNumber, 20, SubjectPageNumber, 20, cancellationToken);
        Devices = await edgeDeviceService.ListEventDevicesByAdminAsync(adminId, cancellationToken);
        Input = new ScheduleInput
        {
            AutoSchedule = Dashboard.Schedule?.AutoSchedule ?? false,
            NextTrainingLocal = Dashboard.Schedule?.NextTrainingAtUtc is DateTime nextTrainingAtUtc
                ? ProjectTimeZone.ConvertUtc(nextTrainingAtUtc, Dashboard.Project.TimeZoneId)
                : null
        };
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }

    public sealed class ScheduleInput
    {
        [Display(Name = "Next Training Time")]
        public DateTime? NextTrainingLocal { get; set; }

        [Display(Name = "Auto schedule training")]
        public bool AutoSchedule { get; set; }
    }

    public sealed class AddClassInput
    {
        [Display(Name = "New Recognition Class")]
        public string Name { get; set; } = string.Empty;
    }
}
