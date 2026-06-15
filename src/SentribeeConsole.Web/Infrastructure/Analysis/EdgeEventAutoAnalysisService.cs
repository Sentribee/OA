using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Infrastructure.Analysis;

public sealed class EdgeEventAutoAnalysisService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    HttpClient httpClient,
    IOptions<EdgeEventAutoAnalysisOptions> options,
    ILogger<EdgeEventAutoAnalysisService> logger) : IEdgeEventAutoAnalysisService
{
    private readonly EdgeEventAutoAnalysisOptions _options = options.Value;

    public async Task<EdgeEventAutoAnalysisResult?> AnalyzeAsync(
        int eventId,
        int projectId,
        string deviceCode,
        string? imageUrl,
        byte[]? imageBytes,
        string? imageContentType,
        string? detectionJson,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        if (UseRemoteAnalysis())
        {
            var remoteResult = await AnalyzeRemoteAsync(
                eventId,
                projectId,
                deviceCode,
                imageUrl,
                imageBytes,
                imageContentType,
                detectionJson,
                cancellationToken);
            if (remoteResult is not null || !_options.FallbackToLocal)
            {
                return remoteResult;
            }
        }

        return await AnalyzeLocalAsync(
            eventId,
            projectId,
            deviceCode,
            imageUrl,
            imageBytes,
            imageContentType,
            detectionJson,
            cancellationToken);
    }

    private async Task<EdgeEventAutoAnalysisResult?> AnalyzeRemoteAsync(
        int eventId,
        int projectId,
        string deviceCode,
        string? imageUrl,
        byte[]? imageBytes,
        string? imageContentType,
        string? detectionJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.RemoteBaseUrl))
        {
            logger.LogWarning("Skipping remote event auto-analysis for event {EventId}; RemoteBaseUrl is not configured.", eventId);
            return null;
        }

        var apiKey = ResolveRemoteApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("Skipping remote event auto-analysis for event {EventId}; RemoteApiKey is not configured.", eventId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(imageUrl) && (imageBytes is null || imageBytes.Length == 0))
        {
            logger.LogInformation("Skipping remote event auto-analysis for event {EventId}; no image URL or bytes were supplied.", eventId);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildRemoteAnalyzeUri());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                eventId,
                projectId,
                deviceCode,
                imageUrl,
                imageBase64 = imageBytes is { Length: > 0 } ? Convert.ToBase64String(imageBytes) : null,
                imageContentType,
                detectionJson = ParseJsonNode(detectionJson),
                requiredPpe = SplitCsv(_options.RequiredPpe),
                personConfidence = _options.PersonConfidence,
                ppeConfidence = _options.PpeConfidence,
                sceneObjectConfidence = _options.SceneObjectConfidence,
                validationRatio = _options.ValidationRatio,
                personCropScale = _options.PersonCropScale,
                personCropMinSide = _options.PersonCropMinSide,
                outputRelativePath = NormalizeRelativePath(_options.OutputRelativePath),
                riskZones = ParseJsonNode(_options.RiskZonesJson)
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Remote event auto-analysis failed for event {EventId} with HTTP {StatusCode}: {Body}",
                    eventId,
                    (int)response.StatusCode,
                    TrimLog(responseText));
                return null;
            }

            return ParseResult(responseText, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Unable to call remote event auto-analysis for event {EventId}.", eventId);
            return null;
        }
    }

    private async Task<EdgeEventAutoAnalysisResult?> AnalyzeLocalAsync(
        int eventId,
        int projectId,
        string deviceCode,
        string? imageUrl,
        byte[]? imageBytes,
        string? imageContentType,
        string? detectionJson,
        CancellationToken cancellationToken)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            logger.LogInformation("Skipping event auto-analysis for event {EventId}; no local image bytes were supplied.", eventId);
            return null;
        }

        var scriptPath = ResolvePath(_options.ScriptPath);
        if (!File.Exists(scriptPath))
        {
            logger.LogWarning("Skipping event auto-analysis for event {EventId}; script not found at {ScriptPath}.", eventId, scriptPath);
            return null;
        }

        var relativeRoot = NormalizeRelativePath(_options.OutputRelativePath);
        var eventRelativePath = $"{relativeRoot}/{eventId}";
        var outputDirectory = Path.Combine(environment.WebRootPath, relativeRoot, eventId.ToString());
        Directory.CreateDirectory(outputDirectory);

        var extension = NormalizeImageExtension(imageContentType, imageUrl);
        var inputImagePath = Path.Combine(outputDirectory, $"source{extension}");
        await File.WriteAllBytesAsync(inputImagePath, imageBytes, cancellationToken);

        var detectionJsonPath = Path.Combine(outputDirectory, "edge_payload_detection.json");
        if (!string.IsNullOrWhiteSpace(detectionJson))
        {
            await File.WriteAllTextAsync(detectionJsonPath, detectionJson, Encoding.UTF8, cancellationToken);
        }

        var outputJsonPath = Path.Combine(outputDirectory, "analysis_result.json");
        var arguments = BuildArguments(
            scriptPath,
            eventId,
            projectId,
            deviceCode,
            imageUrl,
            inputImagePath,
            outputDirectory,
            eventRelativePath,
            detectionJsonPath,
            outputJsonPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonPath,
            Arguments = arguments,
            WorkingDirectory = environment.ContentRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["OPENAI_API_KEY"] = configuration["OpenAI:ApiKey"] ?? string.Empty;
        startInfo.Environment["OPENAI_BASE_URL"] = configuration["OpenAI:BaseUrl"] ?? string.Empty;
        startInfo.Environment["OPENAI_MODEL"] = configuration["OpenAI:Model"] ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)), cancellationToken);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            logger.LogWarning("Event auto-analysis timed out for event {EventId}.", eventId);
            return null;
        }
        catch (Exception exception)
        {
            TryKill(process);
            logger.LogError(exception, "Unable to run event auto-analysis for event {EventId}.", eventId);
            return null;
        }

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "Event auto-analysis failed for event {EventId} with exit code {ExitCode}. stdout={Stdout} stderr={Stderr}",
                eventId,
                process.ExitCode,
                TrimLog(stdout.ToString()),
                TrimLog(stderr.ToString()));
            return null;
        }

        if (!File.Exists(outputJsonPath))
        {
            logger.LogWarning("Event auto-analysis completed for event {EventId}, but {OutputJsonPath} was not created.", eventId, outputJsonPath);
            return null;
        }

        var json = await File.ReadAllTextAsync(outputJsonPath, Encoding.UTF8, cancellationToken);
        return ParseResult(json, $"/{eventRelativePath}/training_event.json");
    }

    private bool UseRemoteAnalysis()
    {
        return _options.Mode.Equals("Remote", StringComparison.OrdinalIgnoreCase)
            || _options.Mode.Equals("Ins1", StringComparison.OrdinalIgnoreCase)
            || _options.Mode.Equals("Http", StringComparison.OrdinalIgnoreCase);
    }

    private Uri BuildRemoteAnalyzeUri()
    {
        var baseUri = new Uri(_options.RemoteBaseUrl.TrimEnd('/') + "/");
        var path = _options.RemoteAnalyzePath.TrimStart('/');
        return new Uri(baseUri, path);
    }

    private string ResolveRemoteApiKey()
    {
        return string.IsNullOrWhiteSpace(_options.RemoteApiKey)
            ? configuration["EdgeEventAutoAnalysis:RemoteApiKey"] ?? string.Empty
            : _options.RemoteApiKey;
    }

    private string BuildArguments(
        string scriptPath,
        int eventId,
        int projectId,
        string deviceCode,
        string? imageUrl,
        string inputImagePath,
        string outputDirectory,
        string eventRelativePath,
        string detectionJsonPath,
        string outputJsonPath)
    {
        var args = new List<string>
        {
            Quote(scriptPath),
            "--event-id", eventId.ToString(CultureInfo.InvariantCulture),
            "--project-id", projectId.ToString(CultureInfo.InvariantCulture),
            "--device-code", Quote(deviceCode),
            "--image-path", Quote(inputImagePath),
            "--output-dir", Quote(outputDirectory),
            "--public-url-prefix", Quote($"/{eventRelativePath}"),
            "--output-json", Quote(outputJsonPath),
            "--required-ppe", Quote(_options.RequiredPpe),
            "--person-conf", _options.PersonConfidence.ToString(CultureInfo.InvariantCulture),
            "--ppe-conf", _options.PpeConfidence.ToString(CultureInfo.InvariantCulture),
            "--scene-object-conf", _options.SceneObjectConfidence.ToString(CultureInfo.InvariantCulture),
            "--val-ratio", _options.ValidationRatio.ToString(CultureInfo.InvariantCulture),
            "--person-crop-scale", _options.PersonCropScale.ToString(CultureInfo.InvariantCulture),
            "--person-crop-min-side", _options.PersonCropMinSide.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            args.AddRange(["--image-url", Quote(imageUrl)]);
        }

        if (File.Exists(detectionJsonPath))
        {
            args.AddRange(["--detection-json", Quote(detectionJsonPath)]);
        }

        if (!string.IsNullOrWhiteSpace(_options.PersonModelPath))
        {
            args.AddRange(["--person-model", Quote(ResolvePath(_options.PersonModelPath))]);
        }

        foreach (var modelPath in _options.PpeModelPaths.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            args.AddRange(["--ppe-model", Quote(ResolvePath(modelPath))]);
        }

        if (_options.UseOpenAI)
        {
            args.Add("--use-openai");
            var model = string.IsNullOrWhiteSpace(_options.OpenAIModel)
                ? configuration["OpenAI:Model"]
                : _options.OpenAIModel;
            if (!string.IsNullOrWhiteSpace(model))
            {
                args.AddRange(["--openai-model", Quote(model)]);
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.RiskZonesJson))
        {
            args.AddRange(["--risk-zones-json", Quote(_options.RiskZonesJson)]);
        }

        return string.Join(' ', args);
    }

    private EdgeEventAutoAnalysisResult? ParseResult(string json, string? trainingJsonUrl)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null)
        {
            return null;
        }

        var subjects = new List<global::EdgeEventSubjectPayload>();
        foreach (var subject in root["subjects"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            subjects.Add(new global::EdgeEventSubjectPayload(
                GetString(subject, "subjectKey") ?? GetString(subject, "subject_key") ?? $"person-{subjects.Count + 1:000}",
                GetString(subject, "subjectType") ?? GetString(subject, "subject_type") ?? "Person",
                GetString(subject, "trackingLabel") ?? GetString(subject, "tracking_label"),
                GetString(subject, "cropImageUrl") ?? GetString(subject, "crop_image_url"),
                GetString(subject, "previewImageUrl") ?? GetString(subject, "preview_image_url"),
                ToJsonElement(subject["boundingBox"] ?? subject["bounding_box"]),
                ToJsonElement(subject["ppeBoxes"] ?? subject["ppe_boxes"]),
                ToJsonElement(subject["ppeStatus"] ?? subject["ppe_status"]),
                GetBool(subject, "isRisk") ?? GetBool(subject, "is_risk") ?? false,
                GetString(subject, "riskCategory") ?? GetString(subject, "risk_category"),
                GetString(subject, "riskSeverity") ?? GetString(subject, "risk_severity"),
                GetString(subject, "riskReason") ?? GetString(subject, "risk_reason"),
                ToJsonElement(subject["analysisJson"] ?? subject["analysis_json"])));
        }

        var analysisNode = root["analysis"] as JsonObject ?? root;
        var analysisJson = root.DeepClone().ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var analysis = new global::EdgeEventAnalysisResult(
            GetInt(analysisNode, "peopleCount") ?? GetInt(analysisNode, "people_count") ?? subjects.Count(item => IsPersonSubject(item.SubjectType)),
            GetInt(analysisNode, "machineryVehicleCount") ?? GetInt(analysisNode, "machinery_vehicle_count") ?? 0,
            GetInt(analysisNode, "toolCount") ?? GetInt(analysisNode, "tool_count") ?? 0,
            GetInt(analysisNode, "ppeCompliantPeopleCount") ?? GetInt(analysisNode, "ppe_compliant_people_count") ?? Math.Max(0, subjects.Count(item => IsPersonSubject(item.SubjectType) && !item.IsRisk)),
            GetInt(analysisNode, "riskPersonCount") ?? GetInt(analysisNode, "risk_person_count") ?? subjects.Count(item => IsPersonSubject(item.SubjectType) && item.IsRisk),
            GetDecimal(analysisNode, "ppeComplianceRate") ?? GetDecimal(analysisNode, "ppe_compliance_rate"),
            GetString(analysisNode, "riskCategory") ?? GetString(analysisNode, "risk_category"),
            GetString(analysisNode, "riskSeverity") ?? GetString(analysisNode, "risk_severity") ?? "Review",
            GetString(analysisNode, "summary"),
            analysisJson,
            subjects);

        var annotationJson = root["panoramaAnnotation"]?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? root["panorama_annotation"]?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var resolvedTrainingJsonUrl = GetString(root, "trainingJsonUrl")
            ?? GetString(root, "training_json_url")
            ?? trainingJsonUrl;

        return new EdgeEventAutoAnalysisResult(analysis, annotationJson, resolvedTrainingJsonUrl);
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim().Trim('/');
    }

    private static string NormalizeImageExtension(string? contentType, string? imageUrl)
    {
        if (contentType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ".png";
        }

        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
            {
                return extension;
            }
        }

        return ".jpg";
    }

    private static JsonElement? ToJsonElement(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SplitCsv(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static int? GetInt(JsonObject root, string key)
    {
        try
        {
            return root[key]?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private static decimal? GetDecimal(JsonObject root, string key)
    {
        try
        {
            return root[key]?.GetValue<decimal>();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonObject root, string key)
    {
        try
        {
            return root[key]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool? GetBool(JsonObject root, string key)
    {
        try
        {
            return root[key]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPersonSubject(string? subjectType)
    {
        return subjectType?.Contains("person", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string TrimLog(string value)
    {
        return value.Length <= 2000 ? value : value[^2000..];
    }
}
