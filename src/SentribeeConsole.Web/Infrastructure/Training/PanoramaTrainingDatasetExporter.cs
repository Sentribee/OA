using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Infrastructure.Storage;

namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class PanoramaTrainingDatasetExporter(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IHttpClientFactory httpClientFactory,
    IOptions<S3StorageOptions> s3Options,
    ILogger<PanoramaTrainingDatasetExporter> logger)
{
    private readonly S3StorageOptions _s3Options = s3Options.Value;
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");
    private readonly string _modelHost = configuration["AiModel:SshHost"] ?? "3.27.97.172";
    private readonly string _modelSshUser = configuration["AiModel:SshUser"] ?? "ubuntu";
    private readonly string _modelSshKeyPath = configuration["AiModel:SshKeyPath"]
        ?? configuration["EdgeRuntime:SshKeyPath"]
        ?? "/home/ubuntu/.ssh/id_ed25519";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<PanoramaTrainingDatasetExportResult> ExportPendingAsync(
        int projectId,
        string modelKind,
        CancellationToken cancellationToken)
    {
        modelKind = YoloTrainingKinds.Normalize(modelKind);
        var project = await LoadProjectAsync(projectId, modelKind, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");
        var items = string.Equals(modelKind, YoloTrainingKinds.PersonSlicePpe, StringComparison.Ordinal)
            ? await LoadPendingSubjectItemsAsync(projectId, cancellationToken)
            : await LoadPendingEventItemsAsync(projectId, cancellationToken);
        if (items.Count == 0)
        {
            logger.LogInformation("No pending {ModelKind} learning items to export for project {ProjectId}.", modelKind, projectId);
            return new PanoramaTrainingDatasetExportResult(0, 0, 0, []);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"sentribee-panorama-training-{projectId}-{Guid.NewGuid():N}");
        var imagesDir = Path.Combine(tempRoot, "images", "train");
        var labelsDir = Path.Combine(tempRoot, "labels", "train");
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(labelsDir);

        var exportedIds = new List<long>();
        var skipped = 0;
        var httpClient = httpClientFactory.CreateClient();
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.ImageUrl)
                    || string.IsNullOrWhiteSpace(item.YoloText) && string.IsNullOrWhiteSpace(item.BoxJson))
                {
                    skipped++;
                    continue;
                }

                var extension = GetImageExtension(item.ImageUrl);
                var baseName = item.FileBaseName;
                var imagePath = Path.Combine(imagesDir, $"{baseName}{extension}");
                var remoteLabelPath = Path.Combine(labelsDir, $"{baseName}.txt");
                try
                {
                    var imageSize = await DownloadImageAsync(httpClient, item.ImageUrl, imagePath, cancellationToken);
                    var yoloText = item.YoloText;
                    if (string.IsNullOrWhiteSpace(yoloText) && imageSize is not null)
                    {
                        yoloText = BuildSubjectYoloText(item.BoxJson, imageSize.Value.Width, imageSize.Value.Height);
                    }

                    if (string.IsNullOrWhiteSpace(yoloText))
                    {
                        skipped++;
                        continue;
                    }

                    await File.WriteAllTextAsync(remoteLabelPath, NormalizeYoloText(yoloText), Utf8NoBom, cancellationToken);
                    exportedIds.Add(item.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        exception,
                        "Skipping {ModelKind} training item {EventId}; unable to download image or write label.",
                        modelKind,
                        item.Id);
                    skipped++;
                }
            }

            if (exportedIds.Count > 0)
            {
                await UploadDirectoryAsync(tempRoot, project.TrainingRoot, cancellationToken);
            }

            logger.LogInformation(
                "Exported {ExportedCount} pending panorama learning events for project {ProjectId} to {TrainingRoot}; skipped {SkippedCount}.",
                exportedIds.Count,
                projectId,
                project.TrainingRoot,
                skipped);
            return new PanoramaTrainingDatasetExportResult(items.Count, exportedIds.Count, skipped, exportedIds);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to delete temporary panorama training export directory {TempRoot}.", tempRoot);
            }
        }
    }

    private async Task<ProjectExportContext?> LoadProjectAsync(
        int projectId,
        string modelKind,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, AiModelYamlPath, PersonPpeModelYamlPath
            FROM bee_Project
            WHERE id = @ProjectId
            LIMIT 1;
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var yamlPath = string.Equals(modelKind, YoloTrainingKinds.PersonSlicePpe, StringComparison.Ordinal)
            ? reader["PersonPpeModelYamlPath"] as string ?? "/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml"
            : reader["AiModelYamlPath"] as string ?? "/home/ubuntu/sentribee/hobson/data.yaml";
        var trainingRoot = Path.GetDirectoryName(yamlPath)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(trainingRoot))
        {
            trainingRoot = "/home/ubuntu/sentribee/hobson";
        }

        return new ProjectExportContext(projectId, trainingRoot);
    }

    private async Task<IReadOnlyList<PendingTrainingItem>> LoadPendingEventItemsAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT evt.id, evt.ImageUrl, evt.YoloLabelUrl
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            WHERE device.ProjectId = @ProjectId
              AND COALESCE(evt.LearningStatus, 'None') = 'Pending Learning'
            ORDER BY evt.EventTimeUtc, evt.id;
            """;
        var events = new List<PendingTrainingItem>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(reader.GetOrdinal("id"));
            var yoloLabelUrl = reader["YoloLabelUrl"] as string;
            var yoloText = await ReadLocalYoloTextAsync(yoloLabelUrl, cancellationToken);
            events.Add(new PendingTrainingItem(id, $"event_{id}", reader["ImageUrl"] as string, yoloText));
        }

        return events;
    }

    private async Task<IReadOnlyList<PendingTrainingItem>> LoadPendingSubjectItemsAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT subject.id, subject.CropImageUrl, subject.PpeBoxJson
            FROM bee_EdgeEventSubject AS subject
            INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
            INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
            WHERE device.ProjectId = @ProjectId
              AND subject.SubjectType = 'Person'
              AND COALESCE(subject.LearningStatus, 'None') = 'Pending Learning'
            ORDER BY evt.EventTimeUtc, evt.id, subject.id;
            """;
        var items = new List<PendingTrainingItem>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(reader.GetOrdinal("id"));
            var ppeBoxJson = reader["PpeBoxJson"] as string;
            var yoloText = BuildSubjectYoloText(ppeBoxJson);
            items.Add(new PendingTrainingItem(id, $"subject_{id}", reader["CropImageUrl"] as string, yoloText, ppeBoxJson));
        }

        return items;
    }

    private async Task<string?> ReadLocalYoloTextAsync(
        string? yoloLabelUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(yoloLabelUrl))
        {
            return null;
        }

        var path = ResolveLocalLabelPath(yoloLabelUrl);
        if (path is null || !File.Exists(path))
        {
            logger.LogWarning("YOLO label file was not found at {YoloLabelUrl}.", yoloLabelUrl);
            return null;
        }

        return NormalizeYoloText(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static string NormalizeYoloText(string value)
    {
        return value.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
    }

    private string? ResolveLocalLabelPath(string yoloLabelUrl)
    {
        var relative = yoloLabelUrl.Trim();
        if (Uri.TryCreate(relative, UriKind.Absolute, out var uri))
        {
            relative = uri.AbsolutePath;
        }

        relative = relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var currentPath = Path.Combine(environment.WebRootPath, relative);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var releaseRoot = TryFindReleaseRoot(environment.ContentRootPath);
        if (releaseRoot is null)
        {
            return currentPath;
        }

        foreach (var releaseDirectory in Directory.EnumerateDirectories(releaseRoot).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var candidate = Path.Combine(releaseDirectory, "wwwroot", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return currentPath;
    }

    private static string? TryFindReleaseRoot(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "releases", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            if (directory.Parent is { Name: "releases" } parent)
            {
                return parent.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private async Task<ImageSize?> DownloadImageAsync(
        HttpClient httpClient,
        string imageUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var request = BuildImageGetRequest(imageUrl);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
        return TryGetImageSize(bytes);
    }

    private HttpRequestMessage BuildImageGetRequest(string imageUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        if (!TryGetS3ObjectKey(imageUrl, out var key)
            || string.IsNullOrWhiteSpace(_s3Options.AccessKeyId)
            || string.IsNullOrWhiteSpace(_s3Options.SecretAccessKey)
            || string.IsNullOrWhiteSpace(_s3Options.Region)
            || string.IsNullOrWhiteSpace(_s3Options.Bucket))
        {
            return request;
        }

        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = $"{_s3Options.Bucket}.s3.{_s3Options.Region}.amazonaws.com";
        var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        request.RequestUri = new Uri($"https://{host}/{escapedKey}");
        var payloadHash = ToHex(SHA256.HashData(Array.Empty<byte>()));
        var credentialScope = $"{dateStamp}/{_s3Options.Region}/s3/aws4_request";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = string.Create(
            CultureInfo.InvariantCulture,
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n");
        var canonicalRequest = string.Join('\n',
            "GET",
            $"/{escapedKey}",
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = ToHex(Sign(GetSigningKey(dateStamp), stringToSign));
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={_s3Options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private bool TryGetS3ObjectKey(string imageUrl, out string key)
    {
        key = string.Empty;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        var s3Host = $"{_s3Options.Bucket}.s3.{_s3Options.Region}.amazonaws.com";
        if (string.Equals(host, s3Host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(_s3Options.PublicBaseUrl)
                && Uri.TryCreate(_s3Options.PublicBaseUrl, UriKind.Absolute, out var publicBaseUri)
                && string.Equals(host, publicBaseUri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return !string.IsNullOrWhiteSpace(key);
        }

        return false;
    }

    private byte[] GetSigningKey(string dateStamp)
    {
        var dateKey = Sign(Encoding.UTF8.GetBytes($"AWS4{_s3Options.SecretAccessKey}"), dateStamp);
        var dateRegionKey = Sign(dateKey, _s3Options.Region);
        var dateRegionServiceKey = Sign(dateRegionKey, "s3");
        return Sign(dateRegionServiceKey, "aws4_request");
    }

    private static byte[] Sign(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetImageExtension(string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            return IsKnownImageExtension(extension) ? extension.ToLowerInvariant() : ".jpg";
        }

        return ".jpg";
    }

    private static bool IsKnownImageExtension(string? extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".webp";
    }

    private static string? BuildSubjectYoloText(string? ppeBoxJson)
    {
        return BuildSubjectYoloText(ppeBoxJson, null, null);
    }

    private static string? BuildSubjectYoloText(string? ppeBoxJson, int? fallbackImageWidth, int? fallbackImageHeight)
    {
        if (string.IsNullOrWhiteSpace(ppeBoxJson))
        {
            return null;
        }

        if (JsonNode.Parse(ppeBoxJson) is not JsonArray boxes)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var item in boxes.OfType<JsonObject>())
        {
            var box = item["cropBox"] as JsonObject
                ?? item["crop_box"] as JsonObject
                ?? item["box"] as JsonObject;
            if (box is null)
            {
                continue;
            }

            var classId = GetDecimal(box, "classId") ?? GetDecimal(box, "class_id");
            var x = GetDecimal(box, "x");
            var y = GetDecimal(box, "y");
            var w = GetDecimal(box, "w") ?? GetDecimal(box, "width");
            var h = GetDecimal(box, "h") ?? GetDecimal(box, "height");
            var imageWidth = GetDecimal(item, "imageWidth") ?? GetDecimal(item, "image_width") ?? fallbackImageWidth;
            var imageHeight = GetDecimal(item, "imageHeight") ?? GetDecimal(item, "image_height") ?? fallbackImageHeight;
            if (!classId.HasValue || !x.HasValue || !y.HasValue || !w.HasValue || !h.HasValue)
            {
                continue;
            }

            if (!imageWidth.HasValue || !imageHeight.HasValue)
            {
                return null;
            }

            var xCenter = (x.Value + w.Value / 2m) / imageWidth.Value;
            var yCenter = (y.Value + h.Value / 2m) / imageHeight.Value;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)classId.Value} {xCenter:0.000000} {yCenter:0.000000} {(w.Value / imageWidth.Value):0.000000} {(h.Value / imageHeight.Value):0.000000}"));
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static decimal? GetDecimal(JsonObject node, string propertyName)
    {
        var value = node[propertyName];
        if (value is null)
        {
            return null;
        }

        return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static ImageSize? TryGetImageSize(byte[] bytes)
    {
        if (bytes.Length >= 24
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            var width = ReadBigEndianInt32(bytes, 16);
            var height = ReadBigEndianInt32(bytes, 20);
            return width > 0 && height > 0 ? new ImageSize(width, height) : null;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                var length = (bytes[index + 2] << 8) + bytes[index + 3];
                if (length < 2 || index + length + 2 > bytes.Length)
                {
                    return null;
                }

                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                {
                    var height = (bytes[index + 5] << 8) + bytes[index + 6];
                    var width = (bytes[index + 7] << 8) + bytes[index + 8];
                    return width > 0 && height > 0 ? new ImageSize(width, height) : null;
                }

                index += length + 2;
            }
        }

        return null;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private async Task UploadDirectoryAsync(
        string localRoot,
        string remoteRoot,
        CancellationToken cancellationToken)
    {
        await RunSshAsync(
            $"mkdir -p {QuoteShell(remoteRoot + "/images/train")} {QuoteShell(remoteRoot + "/labels/train")}",
            cancellationToken);

        var tarStartInfo = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = localRoot,
            StandardErrorEncoding = Encoding.UTF8
        };
        tarStartInfo.ArgumentList.Add("-czf");
        tarStartInfo.ArgumentList.Add("-");
        tarStartInfo.ArgumentList.Add(".");

        var sshStartInfo = BuildSshStartInfo($"tar -xzf - -C {QuoteShell(remoteRoot)}");
        sshStartInfo.RedirectStandardInput = true;
        sshStartInfo.RedirectStandardError = true;

        using var tar = Process.Start(tarStartInfo) ?? throw new InvalidOperationException("Unable to start tar process.");
        using var ssh = Process.Start(sshStartInfo) ?? throw new InvalidOperationException("Unable to start SSH process.");
        await tar.StandardOutput.BaseStream.CopyToAsync(ssh.StandardInput.BaseStream, cancellationToken);
        ssh.StandardInput.Close();
        var tarErrorTask = tar.StandardError.ReadToEndAsync(cancellationToken);
        var sshErrorTask = ssh.StandardError.ReadToEndAsync(cancellationToken);
        await tar.WaitForExitAsync(cancellationToken);
        await ssh.WaitForExitAsync(cancellationToken);
        var tarError = await tarErrorTask;
        var sshError = await sshErrorTask;
        if (tar.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(tarError) ? "Unable to package training dataset." : tarError.Trim());
        }

        if (ssh.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(sshError) ? "Unable to upload training dataset to model host." : sshError.Trim());
        }
    }

    private async Task RunSshAsync(string remoteCommand, CancellationToken cancellationToken)
    {
        var startInfo = BuildSshStartInfo(remoteCommand);
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start SSH process.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Unable to prepare remote training directory." : error.Trim());
        }
    }

    private ProcessStartInfo BuildSshStartInfo(string remoteCommand)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
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

    private sealed record ProjectExportContext(int ProjectId, string TrainingRoot);

    private sealed record PendingTrainingItem(long Id, string FileBaseName, string? ImageUrl, string? YoloText, string? BoxJson = null);

    private readonly record struct ImageSize(int Width, int Height);
}

public sealed record PanoramaTrainingDatasetExportResult(
    int TotalCount,
    int ExportedCount,
    int SkippedCount,
    IReadOnlyList<long> ExportedIds);
