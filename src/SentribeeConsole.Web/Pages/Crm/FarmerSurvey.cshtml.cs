using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public sealed class FarmerSurveyModel(
    IConfiguration configuration,
    IConsoleEmailService emailService,
    ILogger<FarmerSurveyModel> logger) : PageModel
{
    private const string SurveyVersion = "2026-08-short-en-v1";
    private const string DefaultReportTo = "emily@sentribee.ai";
    private const string DefaultReportCc = "rock@sentribee.ai";

    [BindProperty]
    public FarmerSurveyInput Input { get; set; } = new();

    public string? SubmissionReference { get; private set; }

    public void OnGet(string? submitted)
    {
        if (Guid.TryParse(submitted, out var responseGuid))
        {
            SubmissionReference = responseGuid.ToString("D");
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Input.CompanyWebsite))
        {
            return RedirectToPage(new { submitted = Guid.NewGuid().ToString("D") });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var responseGuid = Guid.NewGuid();
        var submittedAtUtc = DateTime.UtcNow;
        var reportSections = BuildReportSections(Input);
        var answerJson = JsonSerializer.Serialize(
            new
            {
                surveyVersion = SurveyVersion,
                responseId = responseGuid,
                submittedAtUtc,
                sections = reportSections
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        long responseId;
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string insertSql = """
                INSERT INTO bee_FarmerSurveyResponse
                    (ResponseGuid, SurveyVersion, SubmittedAtUtc, FarmName, FarmRole, Region,
                     LivestockOperation, CattleCount, SheepCount, FarmArea, StaffCount,
                     ContactName, ContactPhone, ContactEmail, AnswerJson, UserAgent,
                     ReportTo, ReportCc, ReportStatus)
                VALUES
                    (@ResponseGuid, @SurveyVersion, @SubmittedAtUtc, @FarmName, @FarmRole, @Region,
                     @LivestockOperation, @CattleCount, @SheepCount, @FarmArea, @StaffCount,
                     @ContactName, @ContactPhone, @ContactEmail, @AnswerJson, @UserAgent,
                     @ReportTo, @ReportCc, 'Pending');
                """;
            await using var command = new MySqlCommand(insertSql, connection);
            command.Parameters.Add("@ResponseGuid", MySqlDbType.VarChar, 36).Value = responseGuid.ToString("D");
            command.Parameters.Add("@SurveyVersion", MySqlDbType.VarChar, 40).Value = SurveyVersion;
            command.Parameters.Add("@SubmittedAtUtc", MySqlDbType.DateTime).Value = submittedAtUtc;
            command.Parameters.Add("@FarmName", MySqlDbType.VarChar, 180).Value = DbValue(Input.FarmName);
            command.Parameters.Add("@FarmRole", MySqlDbType.VarChar, 120).Value = DisplayChoice(Input.Role, Input.RoleOther);
            command.Parameters.Add("@Region", MySqlDbType.VarChar, 120).Value = DisplayChoice(Input.Region, Input.RegionOther);
            command.Parameters.Add("@LivestockOperation", MySqlDbType.VarChar, 140).Value = DisplayChoice(Input.LivestockOperation, Input.LivestockOperationOther);
            command.Parameters.Add("@CattleCount", MySqlDbType.VarChar, 60).Value = Input.CattleCount;
            command.Parameters.Add("@SheepCount", MySqlDbType.VarChar, 60).Value = Input.SheepCount;
            command.Parameters.Add("@FarmArea", MySqlDbType.VarChar, 60).Value = Input.FarmArea;
            command.Parameters.Add("@StaffCount", MySqlDbType.VarChar, 60).Value = Input.StaffCount;
            command.Parameters.Add("@ContactName", MySqlDbType.VarChar, 160).Value = DbValue(Input.ContactName);
            command.Parameters.Add("@ContactPhone", MySqlDbType.VarChar, 80).Value = DbValue(Input.ContactPhone);
            command.Parameters.Add("@ContactEmail", MySqlDbType.VarChar, 180).Value = DbValue(Input.ContactEmail?.ToLowerInvariant());
            command.Parameters.Add("@AnswerJson", MySqlDbType.JSON).Value = answerJson;
            command.Parameters.Add("@UserAgent", MySqlDbType.VarChar, 500).Value = DbValue(TrimTo(Request.Headers.UserAgent.ToString(), 500));
            command.Parameters.Add("@ReportTo", MySqlDbType.VarChar, 180).Value = ReportTo;
            command.Parameters.Add("@ReportCc", MySqlDbType.VarChar, 180).Value = ReportCc;
            await command.ExecuteNonQueryAsync(cancellationToken);
            responseId = command.LastInsertedId;
        }

        var subject = BuildReportSubject(responseGuid, Input);
        var html = BuildReportHtml(responseGuid, submittedAtUtc, reportSections);
        var text = BuildReportText(responseGuid, submittedAtUtc, reportSections);
        ConsoleEmailResult emailResult;
        try
        {
            emailResult = await emailService.SendFarmerSurveyReportAsync(
                ReportTo,
                ReportCc,
                subject,
                html,
                text,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            emailResult = new ConsoleEmailResult(false, "AmazonSes", null, TrimTo(exception.Message, 1000));
        }

        try
        {
            await UpdateReportDeliveryAsync(connectionString, responseId, emailResult, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not update report delivery status for farmer survey {ResponseGuid}.", responseGuid);
        }

        if (!emailResult.Success)
        {
            logger.LogError(
                "Farmer survey {ResponseGuid} was saved, but its report email failed: {ErrorText}",
                responseGuid,
                emailResult.ErrorText);
        }

        return RedirectToPage(new { submitted = responseGuid.ToString("D") });
    }

    private string ReportTo => configuration["FarmerSurvey:ReportTo"]?.Trim() is { Length: > 0 } value
        ? value
        : DefaultReportTo;

    private string ReportCc => configuration["FarmerSurvey:ReportCc"]?.Trim() is { Length: > 0 } value
        ? value
        : DefaultReportCc;

    private static async Task UpdateReportDeliveryAsync(
        string connectionString,
        long responseId,
        ConsoleEmailResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE bee_FarmerSurveyResponse
            SET ReportStatus = @ReportStatus,
                ReportProvider = @ReportProvider,
                ReportProviderMessageId = @ReportProviderMessageId,
                ReportErrorText = @ReportErrorText,
                ReportSentAtUtc = CASE WHEN @ReportStatus = 'Sent' THEN UTC_TIMESTAMP(6) ELSE NULL END
            WHERE id = @ResponseId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ReportStatus", MySqlDbType.VarChar, 30).Value = result.Success ? "Sent" : "Failed";
        command.Parameters.Add("@ReportProvider", MySqlDbType.VarChar, 40).Value = result.Provider;
        command.Parameters.Add("@ReportProviderMessageId", MySqlDbType.VarChar, 200).Value = DbValue(result.ProviderMessageId);
        command.Parameters.Add("@ReportErrorText", MySqlDbType.VarChar, 1000).Value = DbValue(TrimTo(result.ErrorText, 1000));
        command.Parameters.Add("@ResponseId", MySqlDbType.Int64).Value = responseId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<SurveyReportSection> BuildReportSections(FarmerSurveyInput input)
    {
        return
        [
            new("Section 1 — Farm Profile",
            [
                new("1. Farm Name", Display(input.FarmName)),
                new("2. Role on the farm", DisplayChoice(input.Role, input.RoleOther)),
                new("3. Region", DisplayChoice(input.Region, input.RegionOther)),
                new("4. Primary livestock operation", DisplayChoice(input.LivestockOperation, input.LivestockOperationOther)),
                new("5. Cattle managed", input.CattleCount),
                new("6. Sheep managed", input.SheepCount),
                new("7. Farm size", input.FarmArea),
                new("8. Regular workers", input.StaffCount)
            ]),
            new("Section 2 — Farm Operations & Challenges",
            [
                new("9. Biggest challenges", DisplayChoices(input.Challenges, input.ChallengesOther)),
                new("10. Activities taking most staff time", DisplayChoices(input.TimeConsumingActivities, input.TimeConsumingActivitiesOther)),
                new("11. Greatest financial impacts", DisplayChoices(input.FinancialImpacts, input.FinancialImpactsOther)),
                new("12. Time-consuming, repetitive or difficult routine task", Display(input.RoutineTask))
            ]),
            new("Section 3 — Animal Monitoring & Management",
            [
                new("13. Current animal monitoring methods", DisplayChoices(input.MonitoringMethods, input.MonitoringMethodsOther)),
                new("14. Inspection frequency", input.InspectionFrequency),
                new("15. Monitoring difficulties", DisplayChoices(input.MonitoringDifficulties, input.MonitoringDifficultiesOther)),
                new("16. How problems are first detected", DisplayChoice(input.ProblemDetection, input.ProblemDetectionOther)),
                new("17. Common animal health or welfare issues", Display(input.HealthIssues))
            ]),
            new("Section 4 — Current Technology",
            [
                new("18. Technologies currently used", DisplayChoices(input.CurrentTechnologies, input.CurrentTechnologiesOther)),
                new("19. Brands or products", Display(input.TechnologyBrands)),
                new("20. Overall technology satisfaction", input.TechnologySatisfaction),
                new("21. Biggest technology limitation", DisplayChoice(input.TechnologyLimitation, input.TechnologyLimitationOther))
            ]),
            new("Section 5 — Future Technology Needs",
            [
                new("22. Areas technology should improve", DisplayChoices(input.ImprovementAreas, input.ImprovementAreasOther)),
                new("23. One area technology should significantly improve", input.PriorityImprovement)
            ]),
            new("Section 6 — Follow-up & Research Participation",
            [
                new("24. Open to an on-farm research visit", input.ResearchVisit),
                new("25. Interested in future trials or pilots", input.TechnologyTrials),
                new("26. Name", Display(input.ContactName)),
                new("27. Phone", Display(input.ContactPhone)),
                new("28. Email", Display(input.ContactEmail)),
                new("29. Anything else providers should understand", Display(input.AdditionalComments))
            ])
        ];
    }

    private static string BuildReportSubject(Guid responseGuid, FarmerSurveyInput input)
    {
        var farm = string.IsNullOrWhiteSpace(input.FarmName) ? input.Region : input.FarmName.Trim();
        return $"New livestock farm survey — {farm} — {responseGuid.ToString("N")[..8].ToUpperInvariant()}";
    }

    private static string BuildReportHtml(
        Guid responseGuid,
        DateTime submittedAtUtc,
        IReadOnlyList<SurveyReportSection> sections)
    {
        var builder = new StringBuilder();
        builder.Append("""
            <!doctype html><html><body style="margin:0;background:#f3f6f0;font-family:Arial,Helvetica,sans-serif;color:#17251a;">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="padding:28px 12px;background:#f3f6f0;"><tr><td align="center">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:720px;background:#ffffff;border:1px solid #dfe7dc;border-radius:18px;overflow:hidden;">
            <tr><td style="padding:30px 34px;background:#173f2a;color:#ffffff;"><div style="font-size:12px;font-weight:700;letter-spacing:.12em;text-transform:uppercase;color:#b9d8a9;">Sentribee Research</div><h1 style="margin:10px 0 8px;font-size:25px;line-height:1.25;">New livestock farm survey response</h1>
            """);
        builder.Append("<p style=\"margin:0;color:#dbe9d7;font-size:14px;\">Reference ")
            .Append(Encode(responseGuid.ToString("D")))
            .Append(" · Submitted ")
            .Append(Encode(FormatSubmissionTime(submittedAtUtc)))
            .Append("</p></td></tr>");

        foreach (var section in sections)
        {
            builder.Append("<tr><td style=\"padding:24px 34px 4px;\"><h2 style=\"margin:0;font-size:17px;color:#173f2a;\">")
                .Append(Encode(section.Title))
                .Append("</h2></td></tr><tr><td style=\"padding:10px 34px 20px;\"><table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\">");
            foreach (var answer in section.Answers)
            {
                builder.Append("<tr><td style=\"padding:11px 0;border-bottom:1px solid #edf1ea;vertical-align:top;width:44%;font-size:13px;font-weight:700;color:#526052;\">")
                    .Append(Encode(answer.Question))
                    .Append("</td><td style=\"padding:11px 0 11px 20px;border-bottom:1px solid #edf1ea;vertical-align:top;font-size:14px;line-height:1.5;color:#17251a;\">")
                    .Append(Encode(answer.Answer).Replace("\n", "<br>", StringComparison.Ordinal))
                    .Append("</td></tr>");
            }

            builder.Append("</table></td></tr>");
        }

        builder.Append("""
            <tr><td style="padding:22px 34px 28px;background:#f8faf7;color:#647064;font-size:12px;line-height:1.5;">This report was generated automatically from the public Sentribee New Zealand Livestock Farm Operations Survey.</td></tr>
            </table></td></tr></table></body></html>
            """);
        return builder.ToString();
    }

    private static string BuildReportText(
        Guid responseGuid,
        DateTime submittedAtUtc,
        IReadOnlyList<SurveyReportSection> sections)
    {
        var builder = new StringBuilder()
            .AppendLine("Sentribee New Zealand Livestock Farm Operations Survey")
            .AppendLine($"Reference: {responseGuid:D}")
            .AppendLine($"Submitted: {FormatSubmissionTime(submittedAtUtc)}")
            .AppendLine();
        foreach (var section in sections)
        {
            builder.AppendLine(section.Title);
            foreach (var answer in section.Answers)
            {
                builder.AppendLine($"{answer.Question}: {answer.Answer}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatSubmissionTime(DateTime submittedAtUtc)
    {
        try
        {
            var nzTime = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(submittedAtUtc, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
            return $"{nzTime:dd MMM yyyy, h:mm tt} New Zealand time";
        }
        catch (TimeZoneNotFoundException)
        {
            return $"{submittedAtUtc:dd MMM yyyy, HH:mm} UTC";
        }
    }

    private static string DisplayChoices(IReadOnlyCollection<string> choices, string? other)
    {
        return choices.Count == 0
            ? "Not provided"
            : string.Join(", ", choices.Select(choice => DisplayChoice(choice, choice == "Other" ? other : null)));
    }

    private static string DisplayChoice(string choice, string? other)
    {
        return string.Equals(choice, "Other", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(other)
            ? $"Other — {other.Trim()}"
            : choice;
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "Not provided" : value.Trim();

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public sealed record SurveyReportSection(string Title, IReadOnlyList<SurveyReportAnswer> Answers);

public sealed record SurveyReportAnswer(string Question, string Answer);

public sealed class FarmerSurveyInput : IValidatableObject
{
    [StringLength(180)]
    public string? FarmName { get; set; }

    [Required(ErrorMessage = "Select your role on the farm.")]
    public string Role { get; set; } = string.Empty;

    [StringLength(120)]
    public string? RoleOther { get; set; }

    [Required(ErrorMessage = "Select your region.")]
    public string Region { get; set; } = string.Empty;

    [StringLength(120)]
    public string? RegionOther { get; set; }

    [Required(ErrorMessage = "Select your primary livestock operation.")]
    public string LivestockOperation { get; set; } = string.Empty;

    [StringLength(140)]
    public string? LivestockOperationOther { get; set; }

    [Required(ErrorMessage = "Select the number of cattle you manage.")]
    public string CattleCount { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select the number of sheep you manage.")]
    public string SheepCount { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select the size of your farming operation.")]
    public string FarmArea { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select how many people regularly work on the farm.")]
    public string StaffCount { get; set; } = string.Empty;

    public List<string> Challenges { get; set; } = [];

    [StringLength(180)]
    public string? ChallengesOther { get; set; }

    public List<string> TimeConsumingActivities { get; set; } = [];

    [StringLength(180)]
    public string? TimeConsumingActivitiesOther { get; set; }

    public List<string> FinancialImpacts { get; set; } = [];

    [StringLength(180)]
    public string? FinancialImpactsOther { get; set; }

    [StringLength(2000)]
    public string? RoutineTask { get; set; }

    public List<string> MonitoringMethods { get; set; } = [];

    [StringLength(180)]
    public string? MonitoringMethodsOther { get; set; }

    [Required(ErrorMessage = "Select how often individual animals are closely inspected.")]
    public string InspectionFrequency { get; set; } = string.Empty;

    public List<string> MonitoringDifficulties { get; set; } = [];

    [StringLength(180)]
    public string? MonitoringDifficultiesOther { get; set; }

    [Required(ErrorMessage = "Select how animal problems are usually first detected.")]
    public string ProblemDetection { get; set; } = string.Empty;

    [StringLength(180)]
    public string? ProblemDetectionOther { get; set; }

    [StringLength(2000)]
    public string? HealthIssues { get; set; }

    public List<string> CurrentTechnologies { get; set; } = [];

    [StringLength(180)]
    public string? CurrentTechnologiesOther { get; set; }

    [StringLength(500)]
    public string? TechnologyBrands { get; set; }

    [Required(ErrorMessage = "Select your overall satisfaction with current technologies.")]
    public string TechnologySatisfaction { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select the biggest limitation of your current farm technology.")]
    public string TechnologyLimitation { get; set; } = string.Empty;

    [StringLength(180)]
    public string? TechnologyLimitationOther { get; set; }

    public List<string> ImprovementAreas { get; set; } = [];

    [StringLength(180)]
    public string? ImprovementAreasOther { get; set; }

    [Required(ErrorMessage = "Tell us the one area you would most like technology to improve.")]
    [StringLength(2000)]
    public string PriorityImprovement { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select whether you would consider an on-farm research visit.")]
    public string ResearchVisit { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select whether you would consider future technology trials.")]
    public string TechnologyTrials { get; set; } = string.Empty;

    [StringLength(160)]
    public string? ContactName { get; set; }

    [Phone]
    [StringLength(80)]
    public string? ContactPhone { get; set; }

    [EmailAddress]
    [StringLength(180)]
    public string? ContactEmail { get; set; }

    [StringLength(2000)]
    public string? AdditionalComments { get; set; }

    [StringLength(200)]
    public string? CompanyWebsite { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in RequireSelections(Challenges, 1, 3, nameof(Challenges), "Select one to three current farm challenges."))
        {
            yield return result;
        }

        foreach (var result in RequireSelections(TimeConsumingActivities, 1, 3, nameof(TimeConsumingActivities), "Select one to three activities taking the most staff time."))
        {
            yield return result;
        }

        foreach (var result in RequireSelections(FinancialImpacts, 1, 3, nameof(FinancialImpacts), "Select one to three areas with the greatest financial impact."))
        {
            yield return result;
        }

        foreach (var result in RequireSelections(MonitoringMethods, 1, int.MaxValue, nameof(MonitoringMethods), "Select at least one current animal monitoring method."))
        {
            yield return result;
        }

        foreach (var result in RequireSelections(MonitoringDifficulties, 1, int.MaxValue, nameof(MonitoringDifficulties), "Select at least one animal monitoring difficulty."))
        {
            yield return result;
        }

        foreach (var result in RequireSelections(CurrentTechnologies, 1, int.MaxValue, nameof(CurrentTechnologies), "Select at least one technology, or select None."))
        {
            yield return result;
        }

        if (CurrentTechnologies.Contains("None", StringComparer.Ordinal) && CurrentTechnologies.Count > 1)
        {
            yield return new ValidationResult("Select None by itself, or choose the technologies you use.", [nameof(CurrentTechnologies)]);
        }

        foreach (var result in RequireSelections(ImprovementAreas, 1, 3, nameof(ImprovementAreas), "Select one to three areas for future technology to improve."))
        {
            yield return result;
        }

        foreach (var result in RequireOther(Role, RoleOther, nameof(RoleOther), "Describe your other farm role.")) yield return result;
        foreach (var result in RequireOther(Region, RegionOther, nameof(RegionOther), "Enter your other region.")) yield return result;
        foreach (var result in RequireOther(LivestockOperation, LivestockOperationOther, nameof(LivestockOperationOther), "Describe your other livestock operation.")) yield return result;
        foreach (var result in RequireOther(Challenges, ChallengesOther, nameof(ChallengesOther), "Describe the other farm challenge.")) yield return result;
        foreach (var result in RequireOther(TimeConsumingActivities, TimeConsumingActivitiesOther, nameof(TimeConsumingActivitiesOther), "Describe the other time-consuming activity.")) yield return result;
        foreach (var result in RequireOther(FinancialImpacts, FinancialImpactsOther, nameof(FinancialImpactsOther), "Describe the other financial impact.")) yield return result;
        foreach (var result in RequireOther(MonitoringMethods, MonitoringMethodsOther, nameof(MonitoringMethodsOther), "Describe the other monitoring method.")) yield return result;
        foreach (var result in RequireOther(MonitoringDifficulties, MonitoringDifficultiesOther, nameof(MonitoringDifficultiesOther), "Describe the other monitoring difficulty.")) yield return result;
        foreach (var result in RequireOther(ProblemDetection, ProblemDetectionOther, nameof(ProblemDetectionOther), "Describe how the problem is otherwise detected.")) yield return result;
        foreach (var result in RequireOther(CurrentTechnologies, CurrentTechnologiesOther, nameof(CurrentTechnologiesOther), "Describe the other technology.")) yield return result;
        foreach (var result in RequireOther(TechnologyLimitation, TechnologyLimitationOther, nameof(TechnologyLimitationOther), "Describe the other technology limitation.")) yield return result;
        foreach (var result in RequireOther(ImprovementAreas, ImprovementAreasOther, nameof(ImprovementAreasOther), "Describe the other improvement area.")) yield return result;
    }

    private static IEnumerable<ValidationResult> RequireSelections(
        IReadOnlyCollection<string> values,
        int minimum,
        int maximum,
        string memberName,
        string message)
    {
        if (values.Count < minimum || values.Count > maximum)
        {
            yield return new ValidationResult(message, [memberName]);
        }
    }

    private static IEnumerable<ValidationResult> RequireOther(
        string choice,
        string? other,
        string memberName,
        string message)
    {
        if (string.Equals(choice, "Other", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(other))
        {
            yield return new ValidationResult(message, [memberName]);
        }
    }

    private static IEnumerable<ValidationResult> RequireOther(
        IReadOnlyCollection<string> choices,
        string? other,
        string memberName,
        string message)
    {
        if (choices.Contains("Other", StringComparer.Ordinal) && string.IsNullOrWhiteSpace(other))
        {
            yield return new ValidationResult(message, [memberName]);
        }
    }
}
