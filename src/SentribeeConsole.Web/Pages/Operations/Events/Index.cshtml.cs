using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Operations.Events;

[Authorize]
public sealed class IndexModel(
    IEdgeDeviceService edgeDeviceService,
    IProjectService projectService) : PageModel
{
    public IReadOnlyList<EdgeEvent> Events { get; private set; } = [];

    public PagedResult<EdgeEvent> EventPage { get; private set; } = new();

    public IReadOnlyList<EdgeEventSubject> Subjects { get; private set; } = [];

    public PagedResult<EdgeEventSubject> SubjectPage { get; private set; } = new();

    public IReadOnlyList<EdgeDevice> Devices { get; private set; } = [];

    public EdgeEventStatusCounts StatusCounts { get; private set; } = new();

    public Project Project { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public string ViewMode { get; set; } = "scenes";

    [BindProperty(SupportsGet = true)]
    public int? DeviceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LearningStatus { get; set; }

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

        var filters = new EdgeEventFilters
        {
            DeviceId = DeviceId,
            Type = Type,
            Status = Status,
            LearningStatus = LearningStatus,
            DateFrom = DateFrom,
            DateTo = DateTo
        };
        Project = await projectService.GetByAdminIdAsync(adminId.Value, cancellationToken) ?? new Project();
        ViewData["CanEditEvents"] = Project.CanEditEvents;
        ViewMode = string.Equals(ViewMode, "subjects", StringComparison.OrdinalIgnoreCase) ? "subjects" : "scenes";
        if (ViewMode == "subjects")
        {
            SubjectPage = await edgeDeviceService.ListEventSubjectsByAdminAsync(adminId.Value, filters, PageNumber, 20, cancellationToken);
            Subjects = SubjectPage.Items;
        }
        else
        {
            EventPage = await edgeDeviceService.ListEventsByAdminAsync(adminId.Value, filters, PageNumber, 20, cancellationToken);
            Events = EventPage.Items;
        }

        Devices = await edgeDeviceService.ListEventDevicesByAdminAsync(adminId.Value, cancellationToken);
        StatusCounts = await edgeDeviceService.GetEventStatusCountsAsync(adminId.Value, filters, cancellationToken);
        return Page();
    }

    private int? GetAdminId()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            ? adminId
            : null;
    }
}
