using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.Training;
using System.Diagnostics;
using System.Text;

namespace SentribeeConsole.Web.Application.Services;

public sealed class YoloModelService(
    IYoloModelRepository repository,
    IProjectRepository projectRepository,
    PanoramaTrainingDatasetExportQueue panoramaTrainingDatasetExportQueue,
    YoloTrainingRunStore trainingRunStore,
    IConfiguration configuration) : IYoloModelService
{
    private readonly string _modelHost = configuration["AiModel:SshHost"] ?? "3.27.97.172";
    private readonly string _modelSshUser = configuration["AiModel:SshUser"] ?? "ubuntu";
    private readonly string _modelSshKeyPath = configuration["AiModel:SshKeyPath"]
        ?? configuration["EdgeRuntime:SshKeyPath"]
        ?? "/home/ubuntu/.ssh/id_ed25519";

    public async Task<YoloModelDashboard> GetDashboardAsync(
        int adminId,
        EdgeEventFilters trainingFilters,
        int eventPageNumber,
        int eventPageSize,
        int subjectPageNumber,
        int subjectPageSize,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        trainingFilters = NormalizeEventFilters(trainingFilters, project.TimeZoneId);
        var dashboard = await repository.GetDashboardAsync(
            adminId,
            project.Id,
            trainingFilters,
            Math.Max(1, eventPageNumber),
            Math.Clamp(eventPageSize, 5, 100),
            Math.Max(1, subjectPageNumber),
            Math.Clamp(subjectPageSize, 5, 100),
            cancellationToken);
        var yamlContent = dashboard.CurrentVersion?.YamlDescription;
        return dashboard with
        {
            Project = project,
            ModelYamlPath = project.AiModelYamlPath,
            PersonPpeModelYamlPath = project.PersonPpeModelYamlPath,
            ModelYamlContent = yamlContent,
            ModelClasses = YoloYamlFile.DefaultModelClasses(),
            PersonPpeModelYamlContent = null,
            PersonPpeModelClasses = YoloYamlFile.DefaultModelClasses()
        };
    }

    public async Task SetScheduleAsync(
        int adminId,
        DateTime? nextTrainingLocal,
        bool autoSchedule,
        CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        DateTime? nextTrainingAtUtc = autoSchedule
            ? GetNextSevenPmInProjectTimeZoneUtc(project.TimeZoneId, DateTime.UtcNow)
            : nextTrainingLocal.HasValue
                ? ProjectTimeZone.ConvertLocalToUtc(nextTrainingLocal.Value, project.TimeZoneId)
                : null;
        await repository.SetScheduleAsync(project.Id, nextTrainingAtUtc, autoSchedule, cancellationToken);
    }

    public async Task ScheduleTonightAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        var dashboard = await repository.GetDashboardAsync(adminId, project.Id, new EdgeEventFilters(), 1, 5, 1, 5, cancellationToken);
        if (dashboard.PendingLearningCount <= 100)
        {
            throw new InvalidOperationException("Tonight training requires more than 100 waiting training events.");
        }

        await SyncCurrentYamlToTrainingHostAsync(project, dashboard, cancellationToken);
        await ScheduleTrainingRunAsync(project, YoloTrainingKinds.Panorama, "Panorama model training requested from the console.", cancellationToken);
    }

    public async Task SchedulePersonSlicePpeTonightAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        var dashboard = await repository.GetDashboardAsync(adminId, project.Id, new EdgeEventFilters(), 1, 5, 1, 5, cancellationToken);
        if (dashboard.PendingSubjectLearningCount <= 0)
        {
            throw new InvalidOperationException("Person slice PPE training requires waiting person slices.");
        }

        await ScheduleTrainingRunAsync(project, YoloTrainingKinds.PersonSlicePpe, "Person slice PPE model training requested from the console.", cancellationToken);
    }

    public async Task CancelScheduleAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        await repository.SetScheduleAsync(project.Id, null, false, cancellationToken);
    }

    public async Task RequestTrainingAsync(int adminId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        await repository.RequestTrainingAsync(project.Id, "Manual training requested from the console.", cancellationToken);
    }

    public async Task AddModelClassAsync(int adminId, string className, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        var dashboard = await repository.GetDashboardAsync(adminId, project.Id, new EdgeEventFilters(), 1, 5, 1, 5, cancellationToken);
        var yamlContent = dashboard.CurrentVersion?.YamlDescription;
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            (yamlContent, _) = await ReadYamlAsync(project.AiModelYamlPath, cancellationToken);
        }

        var (updatedContent, _) = YoloYamlFile.AddClassToContent(yamlContent, className);
        await repository.UpdateCurrentYamlAsync(project.Id, updatedContent, cancellationToken);
    }

    public async Task<bool> RollbackAsync(int adminId, int versionId, CancellationToken cancellationToken)
    {
        var project = await RequireProjectAsync(adminId, cancellationToken);
        RequireModelEditor(project);
        return await repository.RollbackAsync(project.Id, versionId, cancellationToken);
    }

    private static void RequireModelEditor(Project project)
    {
        if (!project.CanEditModel)
        {
            throw new UnauthorizedAccessException("This project role cannot edit AI models.");
        }
    }

    private async Task<Project> RequireProjectAsync(int adminId, CancellationToken cancellationToken)
    {
        return await projectRepository.FindByAdminIdAsync(adminId, cancellationToken)
            ?? throw new InvalidOperationException("Create a project before managing YOLO models.");
    }

    private async Task ScheduleTrainingRunAsync(
        Project project,
        string modelKind,
        string notes,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var startNow = IsAfterTrainingStartTime(project.TimeZoneId, nowUtc);
        var nextTrainingAtUtc = startNow ? (DateTime?)null : GetNextSevenPmInProjectTimeZoneUtc(project.TimeZoneId, nowUtc);
        await trainingRunStore.CreateOrUpdateRunAsync(
            project.Id,
            modelKind,
            nextTrainingAtUtc,
            startNow ? "Staging" : "Scheduled",
            notes,
            cancellationToken);
        panoramaTrainingDatasetExportQueue.QueueProject(project.Id, modelKind, startTrainingAfterExport: startNow);
    }

    private static bool IsAfterTrainingStartTime(string timeZoneId, DateTime nowUtc)
    {
        var timeZone = ProjectTimeZone.Resolve(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        return nowLocal.TimeOfDay >= TimeSpan.FromHours(19);
    }

    private static DateTime GetNextSevenPmInProjectTimeZoneUtc(string timeZoneId, DateTime nowUtc)
    {
        var timeZone = ProjectTimeZone.Resolve(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var nextLocal = nowLocal.Date.AddHours(19);
        if (nextLocal <= nowLocal)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), timeZone);
    }

    private static EdgeEventFilters NormalizeEventFilters(EdgeEventFilters filters, string? timeZoneId)
    {
        return filters with
        {
            DateFrom = filters.DateFrom.HasValue
                ? ProjectTimeZone.ConvertLocalToUtc(filters.DateFrom.Value.Date, timeZoneId)
                : null,
            DateTo = filters.DateTo.HasValue
                ? ProjectTimeZone.ConvertLocalToUtc(filters.DateTo.Value.Date.AddDays(1), timeZoneId)
                : null
        };
    }

    private async Task SyncCurrentYamlToTrainingHostAsync(
        Project project,
        YoloModelDashboard dashboard,
        CancellationToken cancellationToken)
    {
        var yamlContent = dashboard.CurrentVersion?.YamlDescription;
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            (yamlContent, _) = await ReadYamlAsync(project.AiModelYamlPath, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            throw new InvalidOperationException("No YAML class list is available to sync before training.");
        }

        yamlContent = YoloYamlFile.RewriteClassList(yamlContent, YoloYamlFile.DefaultModelClasses());
        await WriteYamlAsync(project.AiModelYamlPath, yamlContent, cancellationToken);
    }

    private async Task<(string? Content, IReadOnlyList<YoloModelClass> Classes)> ReadYamlAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_modelHost))
        {
            try
            {
                var content = await RunSshCaptureAsync($"cat {QuoteShell(path)}", cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return (content, YoloYamlFile.ParseClasses(content));
                }
            }
            catch (Exception)
            {
                // Fall back to the current model version YAML stored in the console database.
            }
        }

        return await YoloYamlFile.ReadAsync(path, cancellationToken);
    }

    private async Task WriteYamlAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_modelHost))
        {
            await RunSshInputAsync($"cat > {QuoteShell(path)}", content, cancellationToken);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private async Task<string> RunSshCaptureAsync(string remoteCommand, CancellationToken cancellationToken)
    {
        var startInfo = BuildSshStartInfo(remoteCommand);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start SSH process.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Unable to read remote data.yaml." : error.Trim());
        }

        return output;
    }

    private async Task RunSshInputAsync(string remoteCommand, string input, CancellationToken cancellationToken)
    {
        var startInfo = BuildSshStartInfo(remoteCommand);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start SSH process.");
        await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await outputTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Unable to update remote data.yaml." : error.Trim());
        }
    }

    private ProcessStartInfo BuildSshStartInfo(string remoteCommand)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(_modelSshKeyPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("StrictHostKeyChecking=no");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=8");
        startInfo.ArgumentList.Add($"{_modelSshUser}@{_modelHost}");
        startInfo.ArgumentList.Add(remoteCommand);
        return startInfo;
    }

    private static string QuoteShell(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}
