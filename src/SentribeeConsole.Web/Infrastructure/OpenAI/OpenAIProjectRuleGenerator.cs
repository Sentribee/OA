using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Infrastructure.OpenAI;

public sealed class OpenAIProjectRuleGenerator(
    HttpClient httpClient,
    IOptions<OpenAIOptions> options) : IProjectRuleGenerator
{
    private readonly OpenAIOptions _options = options.Value;

    public async Task<IReadOnlyList<GeneratedProjectRule>> GenerateAsync(
        string projectName,
        string? projectDescription,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = _options.Model,
            input = new object[]
            {
                new
                {
                    role = "developer",
                    content = """
                        Turn the administrator's natural-language description into concise Edge AI rules.
                        Chinese and English inputs are both valid.
                        Produce between zero and six rules. Return zero rules when the input is empty,
                        meaningless, filler text, or cannot be mapped to the allowed dimensions.
                        Each rule must use exactly one of these dimensions:
                        Environment Recognition, Recognition Logic, Event Recognition, Response Method.
                        Split compound requests by meaning, for example event severity belongs to
                        Event Recognition, delivery channels and cooldowns belong to Response Method,
                        thresholds and if/when policies belong to Recognition Logic, and sensor/scene
                        detection belongs to Environment Recognition.
                        Do not duplicate the same meaning across multiple dimensions.
                        """
                },
                new
                {
                    role = "user",
                    content = $"Project: {projectName}\nDescription: {projectDescription}\nRequested policy: {prompt}"
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "project_rules",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            rules = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        dimension = new { type = "string" },
                                        rule = new { type = "string" }
                                    },
                                    required = new[] { "dimension", "rule" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "rules" },
                        additionalProperties = false
                    }
                }
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI rule generation failed with HTTP status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseJson = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var outputText = ExtractOutputText(responseJson.RootElement);
        var rulesJson = JsonSerializer.Deserialize<GeneratedRulesResponse>(
            outputText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("OpenAI returned no structured project rules.");

        return rulesJson.Rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Dimension) && !string.IsNullOrWhiteSpace(rule.Rule))
            .Select(rule => new GeneratedProjectRule(
                Truncate(rule.Dimension.Trim(), 100),
                Truncate(rule.Rule.Trim(), 1000)))
            .ToList();
    }

    private static string ExtractOutputText(JsonElement root)
    {
        foreach (var output in root.GetProperty("output").EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var content))
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "refusal")
                {
                    throw new InvalidOperationException("OpenAI declined to generate rules for this description.");
                }

                if (item.GetProperty("type").GetString() == "output_text")
                {
                    return item.GetProperty("text").GetString()
                        ?? throw new InvalidOperationException("OpenAI returned an empty rule response.");
                }
            }
        }

        throw new InvalidOperationException("OpenAI returned no rule output.");
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed class GeneratedRulesResponse
    {
        public List<RuleResponse> Rules { get; set; } = [];
    }

    private sealed class RuleResponse
    {
        public string Dimension { get; set; } = string.Empty;

        public string Rule { get; set; } = string.Empty;
    }
}
