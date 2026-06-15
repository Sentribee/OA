using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SentribeeConsole.Web.Infrastructure.OpenAI;

namespace SentribeeConsole.Web.Pages.Crm;

public sealed record CrmWorkLogAnalysis(string Summary, string WorkloadLevel, string Reason);

public sealed record CrmEmployeeAttendanceSchedule(
    string? RealName,
    string? JobTitle,
    TimeSpan? ScheduledStartTime,
    TimeSpan? ScheduledEndTime);

public static class CrmAttendanceWorkLogAnalyzer
{
    public static async Task<CrmWorkLogAnalysis> AnalyzeAsync(
        IHttpClientFactory httpClientFactory,
        OpenAIOptions options,
        string? employeeName,
        string? jobTitle,
        string? clockOutNote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clockOutNote))
        {
            return new CrmWorkLogAnalysis("No checkout work log was provided.", "Unknown", "The employee did not write a checkout note.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new CrmWorkLogAnalysis(Trim(clockOutNote, 260), "Unreviewed", "OpenAI is not configured, so the note was saved without AI analysis.");
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(45);
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = string.IsNullOrWhiteSpace(options.Model) ? "gpt-5.4-mini" : options.Model,
                input = new object[]
                {
                    new
                    {
                        role = "developer",
                        content = """
                            You analyze an employee checkout note for an OA attendance system.
                            Return only compact JSON with:
                            summary: a short practical summary in the same language as the note, max 120 chars.
                            workloadLevel: one of Low, Normal, High, Overloaded, Unclear.
                            reason: one sentence explaining the signal, max 180 chars.
                            Do not judge the person. Focus on workload saturation, task volume, blockers, handovers, and follow-up risk.
                            """
                    },
                    new
                    {
                        role = "user",
                        content = $"""
                            Employee: {employeeName}
                            Job title: {jobTitle}
                            Checkout note:
                            {clockOutNote}
                            """
                    }
                }
            });

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CrmWorkLogAnalysis(Trim(clockOutNote, 260), "Unreviewed", $"OpenAI returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var output = ExtractOutputText(document.RootElement);
            var parsed = TryParseAnalysis(output);
            return parsed ?? new CrmWorkLogAnalysis(Trim(output, 260), "Unclear", "OpenAI returned text that could not be parsed as the expected JSON.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new CrmWorkLogAnalysis(Trim(clockOutNote, 260), "Unreviewed", Trim(ex.Message, 180));
        }
    }

    private static CrmWorkLogAnalysis? TryParseAnalysis(string text)
    {
        var jsonText = text.Trim();
        if (jsonText.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = jsonText.IndexOf('\n');
            var lastFence = jsonText.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak >= 0 && lastFence > firstBreak)
            {
                jsonText = jsonText[(firstBreak + 1)..lastFence].Trim();
            }
        }

        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;
        var summary = ReadString(root, "summary");
        var workloadLevel = NormalizeWorkloadLevel(ReadString(root, "workloadLevel"));
        var reason = ReadString(root, "reason");
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        return new CrmWorkLogAnalysis(
            Trim(summary, 260),
            workloadLevel,
            Trim(reason, 700));
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return string.Join("\n", parts).Trim();
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizeWorkloadLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "low" => "Low",
            "normal" => "Normal",
            "high" => "High",
            "overloaded" => "Overloaded",
            "unclear" => "Unclear",
            _ => "Unclear"
        };
    }

    private static string Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
