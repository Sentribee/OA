using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Contracts;

public interface IYoloModelRepository
{
    Task<YoloModelDashboard> GetDashboardAsync(
        int adminId,
        int projectId,
        EdgeEventFilters trainingFilters,
        int eventPageNumber,
        int eventPageSize,
        int subjectPageNumber,
        int subjectPageSize,
        CancellationToken cancellationToken);

    Task SetScheduleAsync(
        int projectId,
        DateTime? nextTrainingAtUtc,
        bool autoSchedule,
        CancellationToken cancellationToken);

    Task RequestTrainingAsync(int projectId, string notes, CancellationToken cancellationToken);

    Task UpdateCurrentYamlAsync(int projectId, string yamlContent, CancellationToken cancellationToken);

    Task<bool> RollbackAsync(int projectId, int versionId, CancellationToken cancellationToken);
}
