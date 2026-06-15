using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IYoloModelService
{
    Task<YoloModelDashboard> GetDashboardAsync(
        int adminId,
        EdgeEventFilters trainingFilters,
        int eventPageNumber,
        int eventPageSize,
        int subjectPageNumber,
        int subjectPageSize,
        CancellationToken cancellationToken);

    Task SetScheduleAsync(int adminId, DateTime? nextTrainingLocal, bool autoSchedule, CancellationToken cancellationToken);

    Task ScheduleTonightAsync(int adminId, CancellationToken cancellationToken);

    Task SchedulePersonSlicePpeTonightAsync(int adminId, CancellationToken cancellationToken);

    Task CancelScheduleAsync(int adminId, CancellationToken cancellationToken);

    Task RequestTrainingAsync(int adminId, CancellationToken cancellationToken);

    Task AddModelClassAsync(int adminId, string className, CancellationToken cancellationToken);

    Task<bool> RollbackAsync(int adminId, int versionId, CancellationToken cancellationToken);
}
