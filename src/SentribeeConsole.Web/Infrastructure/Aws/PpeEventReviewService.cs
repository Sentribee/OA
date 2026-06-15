using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.OpenAI;
using SentribeeConsole.Web.Infrastructure.Storage;

namespace SentribeeConsole.Web.Infrastructure.Aws;

public sealed partial class PpeEventReviewService(
    IConfiguration configuration,
    HttpClient httpClient,
    IOptions<S3StorageOptions> s3Options,
    IOptions<OpenAIOptions> openAIOptions,
    IWebHostEnvironment environment) : IPpeEventReviewService
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    private readonly S3StorageOptions _awsOptions = s3Options.Value;
    private readonly OpenAIOptions _openAIOptions = openAIOptions.Value;

    public async Task ReviewEventAsync(
        int eventId,
        byte[]? imageBytes,
        string? imageContentType,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var context = await LoadContextAsync(connection, eventId, cancellationToken);
        if (context is null || string.IsNullOrWhiteSpace(context.ImageUrl) && imageBytes is null)
        {
            return;
        }

        try
        {
            imageBytes ??= await httpClient.GetByteArrayAsync(context.ImageUrl, cancellationToken);
            imageContentType = NormalizeContentType(imageContentType, context.ImageUrl);
            var (imageWidth, imageHeight) = ReadImageSize(imageBytes);
            var yoloYaml = context.YamlDescription;
            var classes = ParseYoloClasses(yoloYaml);
            var openAI = await AnalyzeWithOpenAIAsync(
                imageBytes,
                imageContentType,
                yoloYaml,
                OpenAIPpeScope.NonAwsPpeOnly,
                cancellationToken);
            var aws = await AnalyzeWithAwsAsync(imageBytes, imageWidth, imageHeight, cancellationToken);
            var openAIAwsFallback = IsAwsUnavailable(aws)
                ? await AnalyzeWithOpenAIAsync(
                    imageBytes,
                    imageContentType,
                    yoloYaml,
                    OpenAIPpeScope.AwsPpeOnly,
                    cancellationToken)
                : new OpenAIPpeReview(false, [], "AWS PPE detection completed, so no OpenAI fallback was needed.");

            var awsBoxes = FilterAwsPpeBoxes(aws.Boxes);
            var openAIBoxes = FilterOpenAIPpeBoxes(openAI.Boxes);
            var fallbackBoxes = FilterAwsPpeBoxes(openAIAwsFallback.Boxes);
            var confirmedMissingPpe = aws.HasMissingPpe || openAI.HasMissingPpe || openAIAwsFallback.HasMissingPpe;
            var boxes = confirmedMissingPpe
                ? new List<EventAnnotationBox>()
                : MergeBoxes(awsBoxes.Concat(fallbackBoxes).ToList(), openAIBoxes, classes, imageWidth, imageHeight);
            var status = confirmedMissingPpe ? "Real Risk" : "Pending Review";
            var annotationJson = boxes.Count == 0
                ? null
                : JsonSerializer.Serialize(
                    new EventAnnotationDocument(
                        context.ImageUrl,
                        imageWidth,
                        imageHeight,
                        classes,
                        boxes),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var yoloLabelUrl = boxes.Count == 0
                ? null
                : await SaveYoloFilesAsync(eventId, context.ImageUrl, imageWidth, imageHeight, classes, boxes, cancellationToken);
            var reviewJson = JsonSerializer.Serialize(
                new
                {
                    reviewedAtUtc = DateTime.UtcNow,
                    currentYoloYaml = yoloYaml,
                    status,
                    aws,
                    openAI,
                    openAIAwsFallback
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await UpdateReviewAsync(connection, eventId, status, annotationJson, yoloLabelUrl, reviewJson, cancellationToken);
        }
        catch (Exception ex)
        {
            var reviewJson = JsonSerializer.Serialize(
                new
                {
                    reviewedAtUtc = DateTime.UtcNow,
                    status = "Review Failed",
                    error = ex.Message
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await UpdateReviewAsync(connection, eventId, "Real Risk", null, null, reviewJson, cancellationToken);
        }
    }

    private static async Task<EventReviewContext?> LoadContextAsync(
        MySqlConnection connection,
        int eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT evt.id, evt.ImageUrl, model.YamlDescription
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            LEFT JOIN bee_YoloModelVersion AS model ON model.ProjectId = device.ProjectId AND model.IsCurrent = 1
            WHERE evt.id = @EventId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EventReviewContext(
            reader.GetInt32(reader.GetOrdinal("id")),
            reader["ImageUrl"] as string,
            reader["YamlDescription"] as string);
    }

    private async Task<AwsPpeReview> AnalyzeWithAwsAsync(
        byte[] imageBytes,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_awsOptions.AccessKeyId) ||
            string.IsNullOrWhiteSpace(_awsOptions.SecretAccessKey) ||
            string.IsNullOrWhiteSpace(_awsOptions.Region))
        {
            return new AwsPpeReview(false, false, [], "AWS credentials are not configured.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            Image = new { Bytes = Convert.ToBase64String(imageBytes) },
            SummarizationAttributes = new
            {
                MinConfidence = 70,
                RequiredEquipmentTypes = new[] { "FACE_COVER", "HAND_COVER", "HEAD_COVER" }
            }
        });
        using var request = BuildAwsJsonRequest(
            "rekognition",
            $"rekognition.{_awsOptions.Region}.amazonaws.com",
            "/",
            "RekognitionService.DetectProtectiveEquipment",
            payload);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AwsPpeReview(
                false,
                false,
                [],
                $"AWS Rekognition returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(errorBody)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var persons = document.RootElement.TryGetProperty("Persons", out var people)
            ? people.EnumerateArray().ToList()
            : [];
        var boxes = new List<NamedBox>();
        var missing = false;
        foreach (var person in persons)
        {
            var personHasMissing = person.TryGetProperty("BodyParts", out var bodyParts) &&
                bodyParts.EnumerateArray().Any(part =>
                    !part.TryGetProperty("EquipmentDetections", out var equipment) ||
                    !equipment.EnumerateArray().Any());
            missing |= personHasMissing;
            if (!person.TryGetProperty("BodyParts", out bodyParts))
            {
                continue;
            }

            foreach (var part in bodyParts.EnumerateArray())
            {
                if (!part.TryGetProperty("EquipmentDetections", out var equipment))
                {
                    continue;
                }

                foreach (var item in equipment.EnumerateArray())
                {
                    var name = item.TryGetProperty("Type", out var type) ? type.GetString() ?? "ppe" : "ppe";
                    if (item.TryGetProperty("BoundingBox", out var box))
                    {
                        boxes.Add(ToNamedBox(name, box, imageWidth, imageHeight));
                    }
                }
            }
        }

        return new AwsPpeReview(persons.Count > 0, missing, boxes, "AWS Rekognition PPE completed.");
    }

    private async Task<OpenAIPpeReview> AnalyzeWithOpenAIAsync(
        byte[] imageBytes,
        string imageContentType,
        string? yamlDescription,
        OpenAIPpeScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_openAIOptions.ApiKey))
        {
            return new OpenAIPpeReview(false, [], "OpenAI API key is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAIOptions.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = _openAIOptions.Model,
            input = new object[]
            {
                new
                {
                    role = "developer",
                    content = BuildOpenAIPpePrompt(scope)
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = $"Current YOLO YAML:\n{yamlDescription}" },
                        new { type = "input_image", image_url = $"data:{imageContentType};base64,{Convert.ToBase64String(imageBytes)}" }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "ppe_review",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            hasMissingPpe = new { type = "boolean" },
                            reason = new { type = "string" },
                            boxes = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        className = new { type = "string" },
                                        x = new { type = "number" },
                                        y = new { type = "number" },
                                        w = new { type = "number" },
                                        h = new { type = "number" }
                                    },
                                    required = new[] { "className", "x", "y", "w", "h" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "hasMissingPpe", "reason", "boxes" },
                        additionalProperties = false
                    }
                }
            }
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new OpenAIPpeReview(false, [], $"OpenAI returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseJson = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var outputText = ExtractOutputText(responseJson.RootElement);
        var review = JsonSerializer.Deserialize<OpenAIReviewResponse>(
            outputText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return new OpenAIPpeReview(
            review.HasMissingPpe,
            review.Boxes.Select(box => new NamedBox(box.ClassName, box.X, box.Y, box.W, box.H)).ToList(),
            review.Reason);
    }

    private async Task<string> SaveYoloFilesAsync(
        int eventId,
        string? imageUrl,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<EventAnnotationClass> classes,
        IReadOnlyList<EventAnnotationBox> boxes,
        CancellationToken cancellationToken)
    {
        var annotation = new EventAnnotationDocument(imageUrl, imageWidth, imageHeight, classes, boxes);
        var yoloText = string.Join(
            "\n",
            boxes.Select(box =>
            {
                var xCenter = (box.X + box.W / 2) / imageWidth;
                var yCenter = (box.Y + box.H / 2) / imageHeight;
                return string.Join(
                    " ",
                    box.ClassId.ToString(CultureInfo.InvariantCulture),
                    xCenter.ToString("0.000000", CultureInfo.InvariantCulture),
                    yCenter.ToString("0.000000", CultureInfo.InvariantCulture),
                    (box.W / imageWidth).ToString("0.000000", CultureInfo.InvariantCulture),
                    (box.H / imageHeight).ToString("0.000000", CultureInfo.InvariantCulture));
            }));
        var relativeFolder = $"/annotations/events/{eventId}";
        var outputFolder = Path.Combine(environment.WebRootPath, "annotations", "events", eventId.ToString());
        Directory.CreateDirectory(outputFolder);
        await File.WriteAllTextAsync(Path.Combine(outputFolder, "labels.txt"), yoloText, Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputFolder, "annotation.json"),
            JsonSerializer.Serialize(annotation, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Encoding.UTF8,
            cancellationToken);
        return $"{relativeFolder}/labels.txt";
    }

    private static async Task UpdateReviewAsync(
        MySqlConnection connection,
        int eventId,
        string status,
        string? annotationJson,
        string? yoloLabelUrl,
        string reviewJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_EdgeEvent
            SET Status = @Status,
                AnnotationJson = COALESCE(@AnnotationJson, AnnotationJson),
                YoloLabelUrl = COALESCE(@YoloLabelUrl, YoloLabelUrl),
                PpeReviewJson = @PpeReviewJson,
                AnnotatedAtUtc = CASE WHEN @AnnotationJson IS NULL THEN AnnotatedAtUtc ELSE UTC_TIMESTAMP(6) END
            WHERE id = @EventId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
        command.Parameters.Add("@AnnotationJson", MySqlDbType.MediumText).Value = (object?)annotationJson ?? DBNull.Value;
        command.Parameters.Add("@YoloLabelUrl", MySqlDbType.VarChar, 500).Value = (object?)yoloLabelUrl ?? DBNull.Value;
        command.Parameters.Add("@PpeReviewJson", MySqlDbType.JSON).Value = reviewJson;
        command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private HttpRequestMessage BuildAwsJsonRequest(
        string service,
        string host,
        string path,
        string target,
        string body)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var canonicalHeaders = $"content-type:application/x-amz-json-1.1\nhost:{host}\nx-amz-date:{amzDate}\nx-amz-target:{target}\n";
        var signedHeaders = "content-type;host;x-amz-date;x-amz-target";
        var canonicalRequest = $"POST\n{path}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var credentialScope = $"{dateStamp}/{_awsOptions.Region}/{service}/aws4_request";
        var stringToSign = string.Join(
            "\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
        var signature = Convert.ToHexString(Sign(GetSigningKey(service, dateStamp), stringToSign)).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Target", target);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={_awsOptions.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-amz-json-1.1");
        return request;
    }

    private byte[] GetSigningKey(string service, string dateStamp)
    {
        var dateKey = Sign(Encoding.UTF8.GetBytes($"AWS4{_awsOptions.SecretAccessKey}"), dateStamp);
        var regionKey = Sign(dateKey, _awsOptions.Region);
        var serviceKey = Sign(regionKey, service);
        return Sign(serviceKey, "aws4_request");
    }

    private static byte[] Sign(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static IReadOnlyList<EventAnnotationBox> MergeBoxes(
        IReadOnlyList<NamedBox> awsBoxes,
        IReadOnlyList<NamedBox> openAIBoxes,
        IReadOnlyList<EventAnnotationClass> classes,
        int imageWidth,
        int imageHeight)
    {
        return awsBoxes
            .Concat(openAIBoxes.Select(box => ScaleNormalizedBox(box, imageWidth, imageHeight)))
            .Select(box => new EventAnnotationBox(
                ResolveClassId(classes, box.Name),
                Math.Clamp(box.X, 0, imageWidth),
                Math.Clamp(box.Y, 0, imageHeight),
                Math.Clamp(box.W, 1, imageWidth),
                Math.Clamp(box.H, 1, imageHeight)))
            .ToList();
    }

    private static string BuildOpenAIPpePrompt(OpenAIPpeScope scope)
    {
        return scope == OpenAIPpeScope.AwsPpeOnly
            ? """
                AWS Rekognition PPE detection failed, so review only safety helmets, masks/face covers,
                and gloves/hand covers. Use the supplied YOLO YAML class list as the preferred label set,
                including no_helmet, no_gloves, helmet, gloves, and similar entries when available.
                Return hasMissingPpe=true when any visible person is clearly missing a required helmet,
                mask/face cover, or gloves/hand covers. If all visible people appear compliant for these
                AWS-owned PPE categories, return normalized YOLO-style boxes for the visible helmet,
                mask/face cover, or gloves/hand cover items only.
                """
            : """
                Review construction PPE compliance in the image. Use the supplied YOLO YAML class list as
                the preferred label set. Do not evaluate or return boxes for safety helmets, masks/face covers,
                or gloves/hand covers; those are handled separately by AWS Rekognition. Return true only when
                a visible person is missing another required PPE item, such as vest, boots, eyewear, harness,
                clothing, or site-specific protective equipment. If all visible people appear compliant for
                those non-AWS PPE categories, return normalized YOLO-style boxes for those visible items only.
                """;
    }

    private static bool IsAwsUnavailable(AwsPpeReview review)
    {
        return review.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
            review.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimDiagnostic(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static IReadOnlyList<NamedBox> FilterAwsPpeBoxes(IReadOnlyList<NamedBox> boxes)
    {
        return boxes
            .Where(box => IsAwsOwnedPpe(box.Name))
            .Select(box => box with { Name = NormalizeAwsPpeName(box.Name) })
            .ToList();
    }

    private static IReadOnlyList<NamedBox> FilterOpenAIPpeBoxes(IReadOnlyList<NamedBox> boxes)
    {
        return boxes
            .Where(box => !IsAwsOwnedPpe(box.Name))
            .ToList();
    }

    private static bool IsAwsOwnedPpe(string name)
    {
        var normalized = NormalizeClassName(name);
        return normalized.Contains("helmet", StringComparison.Ordinal) ||
            normalized.Contains("hardhat", StringComparison.Ordinal) ||
            normalized.Contains("headcover", StringComparison.Ordinal) ||
            normalized.Contains("mask", StringComparison.Ordinal) ||
            normalized.Contains("facecover", StringComparison.Ordinal) ||
            normalized.Contains("respirator", StringComparison.Ordinal) ||
            normalized.Contains("glove", StringComparison.Ordinal) ||
            normalized.Contains("handcover", StringComparison.Ordinal);
    }

    private static string NormalizeAwsPpeName(string name)
    {
        var normalized = NormalizeClassName(name);
        if (normalized.Contains("headcover", StringComparison.Ordinal) ||
            normalized.Contains("helmet", StringComparison.Ordinal) ||
            normalized.Contains("hardhat", StringComparison.Ordinal))
        {
            return "helmet";
        }

        if (normalized.Contains("facecover", StringComparison.Ordinal) ||
            normalized.Contains("mask", StringComparison.Ordinal) ||
            normalized.Contains("respirator", StringComparison.Ordinal))
        {
            return "mask";
        }

        if (normalized.Contains("handcover", StringComparison.Ordinal) ||
            normalized.Contains("glove", StringComparison.Ordinal))
        {
            return "gloves";
        }

        return name;
    }

    private static NamedBox ScaleNormalizedBox(NamedBox box, int imageWidth, int imageHeight)
    {
        return new NamedBox(
            box.Name,
            box.X * imageWidth,
            box.Y * imageHeight,
            box.W * imageWidth,
            box.H * imageHeight);
    }

    private static NamedBox ToNamedBox(string name, JsonElement box, int imageWidth, int imageHeight)
    {
        var left = ReadDecimal(box, "Left") * imageWidth;
        var top = ReadDecimal(box, "Top") * imageHeight;
        var width = ReadDecimal(box, "Width") * imageWidth;
        var height = ReadDecimal(box, "Height") * imageHeight;
        return new NamedBox(name, left, top, width, height);
    }

    private static decimal ReadDecimal(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number) ? number : 0;
    }

    private static int ResolveClassId(IReadOnlyList<EventAnnotationClass> classes, string name)
    {
        var normalized = NormalizeClassName(name);
        return classes.FirstOrDefault(item => NormalizeClassName(item.Name).Contains(normalized) || normalized.Contains(NormalizeClassName(item.Name)))?.Id
            ?? classes.FirstOrDefault()?.Id
            ?? 0;
    }

    private static string NormalizeClassName(string value)
    {
        return value.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static IReadOnlyList<EventAnnotationClass> ParseYoloClasses(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return DefaultClasses();
        }

        var classes = new List<EventAnnotationClass>();
        var inNames = false;
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("names:", StringComparison.OrdinalIgnoreCase))
            {
                inNames = true;
                var inline = trimmed["names:".Length..].Trim();
                if (inline.StartsWith('[') && inline.EndsWith(']'))
                {
                    var values = inline.Trim('[', ']')
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    for (var index = 0; index < values.Length; index++)
                    {
                        classes.Add(new EventAnnotationClass(index, values[index].Trim().Trim('"', '\'')));
                    }

                    break;
                }

                if (inline.StartsWith('{') && inline.EndsWith('}'))
                {
                    foreach (var value in inline.Trim('{', '}').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var inlineMatch = ClassLineRegex().Match(value.Trim());
                        if (inlineMatch.Success && int.TryParse(inlineMatch.Groups[1].Value.Trim('"', '\''), out var inlineId))
                        {
                            classes.Add(new EventAnnotationClass(inlineId, inlineMatch.Groups[2].Value.Trim().Trim('"', '\'')));
                        }
                    }

                    break;
                }

                continue;
            }

            if (inNames)
            {
                if (!line.StartsWith(' ') && !line.StartsWith('\t'))
                {
                    break;
                }

                var listMatch = ListClassLineRegex().Match(trimmed);
                if (listMatch.Success)
                {
                    classes.Add(new EventAnnotationClass(classes.Count, listMatch.Groups[1].Value.Trim().Trim('"', '\'')));
                    continue;
                }
            }

            var match = ClassLineRegex().Match(trimmed);
            if (match.Success && int.TryParse(match.Groups[1].Value.Trim('"', '\''), out var id))
            {
                classes.Add(new EventAnnotationClass(id, match.Groups[2].Value.Trim().Trim('"', '\'')));
            }
        }

        return classes.Count == 0 ? DefaultClasses() : classes.OrderBy(item => item.Id).ToList();
    }

    private static IReadOnlyList<EventAnnotationClass> DefaultClasses()
    {
        return YoloYamlFile.DefaultModelClasses()
            .Select(item => new EventAnnotationClass(item.Index, item.Name))
            .ToList();
    }

    private static (int Width, int Height) ReadImageSize(byte[] bytes)
    {
        if (bytes.Length > 24 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47)
        {
            return (
                ReadBigEndianInt32(bytes.AsSpan(16, 4)),
                ReadBigEndianInt32(bytes.AsSpan(20, 4)));
        }

        for (var i = 2; i + 9 < bytes.Length;)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = bytes[i + 1];
            var length = (bytes[i + 2] << 8) + bytes[i + 3];
            if (marker is >= 0xC0 and <= 0xC3)
            {
                return ((bytes[i + 7] << 8) + bytes[i + 8], (bytes[i + 5] << 8) + bytes[i + 6]);
            }

            i += Math.Max(2, length + 2);
        }

        return (960, 540);
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
    {
        return (bytes[0] << 24) + (bytes[1] << 16) + (bytes[2] << 8) + bytes[3];
    }

    private static string NormalizeContentType(string? contentType, string? imageUrl)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return contentType;
        }

        return imageUrl?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ? "image/png" : "image/jpeg";
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
                if (item.GetProperty("type").GetString() == "output_text")
                {
                    return item.GetProperty("text").GetString() ?? "{}";
                }
            }
        }

        return "{}";
    }

    [GeneratedRegex(@"^['""]?(\d+)['""]?\s*:\s*(.+)$")]
    private static partial Regex ClassLineRegex();

    [GeneratedRegex(@"^-\s*(.+)$")]
    private static partial Regex ListClassLineRegex();

    private sealed record EventReviewContext(int Id, string? ImageUrl, string? YamlDescription);

    private sealed record NamedBox(string Name, decimal X, decimal Y, decimal W, decimal H);

    private sealed record AwsPpeReview(bool HasPerson, bool HasMissingPpe, IReadOnlyList<NamedBox> Boxes, string Message);

    private sealed record OpenAIPpeReview(bool HasMissingPpe, IReadOnlyList<NamedBox> Boxes, string Reason);

    private enum OpenAIPpeScope
    {
        NonAwsPpeOnly,
        AwsPpeOnly
    }

    private sealed class OpenAIReviewResponse
    {
        public bool HasMissingPpe { get; set; }

        public string Reason { get; set; } = string.Empty;

        public List<OpenAIBoxResponse> Boxes { get; set; } = [];
    }

    private sealed class OpenAIBoxResponse
    {
        public string ClassName { get; set; } = string.Empty;

        public decimal X { get; set; }

        public decimal Y { get; set; }

        public decimal W { get; set; }

        public decimal H { get; set; }
    }
}
