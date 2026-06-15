using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.Storage;

namespace SentribeeConsole.Web.Infrastructure.Aws;

public sealed class AwsServerResourceService(
    HttpClient httpClient,
    IOptions<S3StorageOptions> s3Options) : IServerResourceService
{
    private const string InstanceName = "i-05a6a5077f2ee8dd4";
    private const string PublicDomain = "ins1.sentribee.ai";
    private const int Capacity = 30;
    private const string Ec2ApiVersion = "2016-11-15";
    private readonly S3StorageOptions _awsOptions = s3Options.Value;

    public async Task<IReadOnlyList<ServerResourceSnapshot>> ListAsync(
        int usedInstanceCount,
        CancellationToken cancellationToken)
    {
        var awsInfo = await ReadConfiguredAwsInstanceAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(awsInfo.InstanceId))
        {
            awsInfo = await ReadAwsMetadataAsync(cancellationToken);
        }

        return
        [
            new ServerResourceSnapshot
            {
                InstanceName = string.IsNullOrWhiteSpace(awsInfo.InstanceId) ? InstanceName : awsInfo.InstanceId,
                PublicDomain = PublicDomain,
                DisplayName = "PREVENX Edge AI Runtime Server",
                Status = NormalizeStatus(awsInfo.InstanceState),
                Capacity = Capacity,
                UsedInstances = Math.Min(usedInstanceCount, Capacity),
                Description = "Primary public runtime host for AI Code instances.",
                InstanceType = awsInfo.InstanceType,
                Region = awsInfo.Region,
                AvailabilityZone = awsInfo.AvailabilityZone,
                PublicIpAddress = awsInfo.PublicIpAddress ?? await ResolvePublicDomainAsync(cancellationToken),
                PrivateIpAddress = awsInfo.PrivateIpAddress,
                AmiId = awsInfo.AmiId,
                AccountId = awsInfo.AccountId,
                GpuSummary = string.IsNullOrWhiteSpace(awsInfo.GpuSummary)
                    ? await ReadGpuSummaryAsync(cancellationToken)
                    : awsInfo.GpuSummary,
                MemorySummary = string.IsNullOrWhiteSpace(awsInfo.MemorySummary)
                    ? ReadMemorySummary()
                    : awsInfo.MemorySummary,
                DiskSummary = string.IsNullOrWhiteSpace(awsInfo.DiskSummary)
                    ? ReadDiskSummary()
                    : awsInfo.DiskSummary,
                LoadSummary = ReadLoadSummary(),
                LoadPercent = ReadLoadPercent(),
                UpdatedAtUtc = DateTime.UtcNow,
                MetadataStatus = awsInfo.MetadataStatus
            }
        ];
    }

    public async Task<ServerResourceControlResult> StartAsync(
        string instanceName,
        CancellationToken cancellationToken)
    {
        return await SendInstanceControlAsync(
            instanceName,
            "StartInstances",
            "Server start has been requested.",
            cancellationToken);
    }

    public async Task<ServerResourceControlResult> StopAsync(
        string instanceName,
        CancellationToken cancellationToken)
    {
        return await SendInstanceControlAsync(
            instanceName,
            "StopInstances",
            "Server stop has been requested.",
            cancellationToken);
    }

    private async Task<ServerResourceControlResult> SendInstanceControlAsync(
        string instanceName,
        string action,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(instanceName, InstanceName, StringComparison.OrdinalIgnoreCase))
        {
            return new ServerResourceControlResult(false, "Server resource was not found.");
        }

        if (string.IsNullOrWhiteSpace(_awsOptions.AccessKeyId) ||
            string.IsNullOrWhiteSpace(_awsOptions.SecretAccessKey) ||
            string.IsNullOrWhiteSpace(_awsOptions.Region))
        {
            return new ServerResourceControlResult(false, "AWS credentials are not configured.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await SendEc2QueryAsync(new Dictionary<string, string>
            {
                ["Action"] = action,
                ["Version"] = Ec2ApiVersion,
                ["InstanceId.1"] = InstanceName
            }, timeout.Token);
            return new ServerResourceControlResult(true, successMessage);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or CryptographicException)
        {
            return new ServerResourceControlResult(false, "AWS EC2 API did not accept the server control request.");
        }
    }

    private async Task<AwsInstanceMetadata> ReadConfiguredAwsInstanceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_awsOptions.AccessKeyId) ||
            string.IsNullOrWhiteSpace(_awsOptions.SecretAccessKey) ||
            string.IsNullOrWhiteSpace(_awsOptions.Region))
        {
            return new AwsInstanceMetadata(MetadataStatus: "AWS credentials are not configured.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));

            var instanceDocument = await SendEc2QueryAsync(new Dictionary<string, string>
            {
                ["Action"] = "DescribeInstances",
                ["Version"] = Ec2ApiVersion,
                ["InstanceId.1"] = InstanceName
            }, timeout.Token);

            var instanceItem = instanceDocument
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "instancesSet")
                ?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "item");

            if (instanceItem is null)
            {
                return new AwsInstanceMetadata(MetadataStatus: "AWS EC2 instance was not found.");
            }

            var instanceType = ValueOf(instanceItem, "instanceType");
            var volumeIds = instanceItem
                .Descendants()
                .Where(element => element.Name.LocalName == "volumeId")
                .Select(element => element.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var typeInfo = string.IsNullOrWhiteSpace(instanceType)
                ? new AwsInstanceTypeInfo()
                : await ReadInstanceTypeInfoAsync(instanceType, timeout.Token);
            var diskSummary = volumeIds.Count == 0
                ? typeInfo.DiskSummary
                : await ReadVolumeSummaryAsync(volumeIds, timeout.Token) ?? typeInfo.DiskSummary;

            return new AwsInstanceMetadata(
                InstanceId: ValueOf(instanceItem, "instanceId") ?? InstanceName,
                InstanceType: instanceType,
                Region: _awsOptions.Region,
                AvailabilityZone: ValueOf(instanceItem, "availabilityZone"),
                PrivateIpAddress: ValueOf(instanceItem, "privateIpAddress"),
                PublicIpAddress: ValueOf(instanceItem, "ipAddress") ?? await ResolvePublicDomainAsync(timeout.Token),
                AmiId: ValueOf(instanceItem, "imageId"),
                InstanceState: instanceItem
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "instanceState")
                    is { } stateElement
                        ? ValueOf(stateElement, "name")
                        : null,
                GpuSummary: typeInfo.GpuSummary,
                MemorySummary: typeInfo.MemorySummary,
                DiskSummary: diskSummary,
                MetadataStatus: "AWS EC2 API loaded.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or CryptographicException)
        {
            return new AwsInstanceMetadata(MetadataStatus: "AWS EC2 API did not respond.");
        }
    }

    private async Task<AwsInstanceTypeInfo> ReadInstanceTypeInfoAsync(
        string instanceType,
        CancellationToken cancellationToken)
    {
        var document = await SendEc2QueryAsync(new Dictionary<string, string>
        {
            ["Action"] = "DescribeInstanceTypes",
            ["Version"] = Ec2ApiVersion,
            ["InstanceType.1"] = instanceType
        }, cancellationToken);

        var item = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "instanceTypeSet")
            ?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "item");
        if (item is null)
        {
            return new AwsInstanceTypeInfo();
        }

        var memoryMiB = item
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "memoryInfo")
            is { } memoryElement
                ? ValueOf(memoryElement, "sizeInMiB")
                : null;
        var memorySummary = decimal.TryParse(memoryMiB, out var memory)
            ? $"{memory / 1024m:0.#} GiB"
            : null;

        var gpuItems = item
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "gpuInfo")
            ?.Descendants()
            .Where(element => element.Name.LocalName == "gpus")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "item"))
            .ToList() ?? [];
        var gpuSummary = gpuItems.Count == 0
            ? "No GPU detected"
            : string.Join("; ", gpuItems.Select(gpu =>
            {
                var count = ValueOf(gpu, "count");
                var name = ValueOf(gpu, "name") ?? "GPU";
                var gpuMemory = gpu
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "memoryInfo")
                    is { } gpuMemoryElement
                        ? ValueOf(gpuMemoryElement, "sizeInMiB")
                        : null;
                var memoryText = decimal.TryParse(gpuMemory, out var gpuMemoryMiB)
                    ? $" {gpuMemoryMiB / 1024m:0.#} GiB"
                    : string.Empty;
                return $"{count ?? "1"} x {name}{memoryText}";
            }));

        var diskSummary = item
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "instanceStorageInfo")
            is { } storageElement
                ? BuildInstanceStorageSummary(storageElement)
                : "EBS volumes";

        return new AwsInstanceTypeInfo(gpuSummary, memorySummary, diskSummary);
    }

    private async Task<string?> ReadVolumeSummaryAsync(
        IReadOnlyList<string> volumeIds,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Action"] = "DescribeVolumes",
            ["Version"] = Ec2ApiVersion
        };
        for (var index = 0; index < volumeIds.Count; index++)
        {
            parameters[$"VolumeId.{index + 1}"] = volumeIds[index];
        }

        var document = await SendEc2QueryAsync(parameters, cancellationToken);
        var volumes = document
            .Descendants()
            .Where(element => element.Name.LocalName == "volumeSet")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "item"))
            .ToList();
        if (volumes.Count == 0)
        {
            return null;
        }

        var totalGiB = volumes
            .Select(volume => decimal.TryParse(ValueOf(volume, "size"), out var size) ? size : 0m)
            .Sum();
        var types = volumes
            .Select(volume => ValueOf(volume, "volumeType"))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return $"{totalGiB:0.#} GiB EBS ({string.Join(", ", types)})";
    }

    private async Task<XDocument> SendEc2QueryAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var credentialScope = $"{dateStamp}/{_awsOptions.Region}/ec2/aws4_request";
        var host = $"ec2.{_awsOptions.Region}.amazonaws.com";

        var queryParameters = new SortedDictionary<string, string>(
            parameters.ToDictionary(pair => pair.Key, pair => pair.Value),
            StringComparer.Ordinal);

        var canonicalQueryString = string.Join('&', queryParameters.Select(pair =>
            $"{EscapeAws(pair.Key)}={EscapeAws(pair.Value)}"));
        var payloadHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var signedHeaders = "host;x-amz-date";
        var canonicalRequest = string.Join('\n',
        [
            "GET",
            "/",
            canonicalQueryString,
            $"host:{host}",
            $"x-amz-date:{amzDate}",
            string.Empty,
            signedHeaders,
            payloadHash
        ]);
        var stringToSign = string.Join('\n',
        [
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant()
        ]);
        var signature = Convert.ToHexString(Sign(GetEc2SigningKey(dateStamp), stringToSign)).ToLowerInvariant();
        var authorization = $"AWS4-HMAC-SHA256 Credential={_awsOptions.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/?{canonicalQueryString}");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return XDocument.Parse(xml);
    }

    private byte[] GetEc2SigningKey(string dateStamp)
    {
        var dateKey = Sign(Encoding.UTF8.GetBytes($"AWS4{_awsOptions.SecretAccessKey}"), dateStamp);
        var dateRegionKey = Sign(dateKey, _awsOptions.Region);
        var dateRegionServiceKey = Sign(dateRegionKey, "ec2");
        return Sign(dateRegionServiceKey, "aws4_request");
    }

    private static byte[] Sign(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string EscapeAws(string value)
    {
        return Uri.EscapeDataString(value)
            .Replace("%7E", "~", StringComparison.Ordinal);
    }

    private static string? ValueOf(XElement element, string localName)
    {
        return element
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == localName)
            ?.Value;
    }

    private static string BuildInstanceStorageSummary(XElement storageElement)
    {
        var totalSize = ValueOf(storageElement, "totalSizeInGB");
        var disks = storageElement
            .Descendants()
            .Where(element => element.Name.LocalName == "disks")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "item"))
            .Select(disk =>
            {
                var count = ValueOf(disk, "count");
                var size = ValueOf(disk, "sizeInGB");
                var type = ValueOf(disk, "type");
                return $"{count ?? "1"} x {size ?? "?"} GiB {type}".Trim();
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return disks.Count == 0
            ? $"{totalSize ?? "Unknown"} GiB instance storage"
            : $"{totalSize ?? "Unknown"} GiB instance storage ({string.Join(", ", disks)})";
    }

    private static string NormalizeStatus(string? instanceState)
    {
        return instanceState switch
        {
            "running" => "Available",
            "pending" => "Starting",
            "stopping" => "Stopping",
            "stopped" => "Stopped",
            "shutting-down" => "Shutting Down",
            "terminated" => "Terminated",
            _ => "Available"
        };
    }

    private async Task<AwsInstanceMetadata> ReadAwsMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var tokenRequest = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
            tokenRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "60");
            using var tokenResponse = await httpClient.SendAsync(tokenRequest, timeout.Token);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                return new AwsInstanceMetadata(MetadataStatus: $"AWS metadata unavailable ({(int)tokenResponse.StatusCode}).");
            }

            var token = await tokenResponse.Content.ReadAsStringAsync(timeout.Token);
            var documentRequest = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/dynamic/instance-identity/document");
            documentRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
            using var documentResponse = await httpClient.SendAsync(documentRequest, timeout.Token);
            if (!documentResponse.IsSuccessStatusCode)
            {
                return new AwsInstanceMetadata(MetadataStatus: $"AWS metadata document unavailable ({(int)documentResponse.StatusCode}).");
            }

            var json = await documentResponse.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var publicIp = await ReadMetadataTextAsync("public-ipv4", token, timeout.Token);
            return new AwsInstanceMetadata(
                InstanceId: ReadString(root, "instanceId"),
                InstanceType: ReadString(root, "instanceType"),
                Region: ReadString(root, "region"),
                AvailabilityZone: ReadString(root, "availabilityZone"),
                PrivateIpAddress: ReadString(root, "privateIp"),
                PublicIpAddress: publicIp,
                AmiId: ReadString(root, "imageId"),
                AccountId: ReadString(root, "accountId"),
                MetadataStatus: "AWS EC2 metadata loaded.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new AwsInstanceMetadata(MetadataStatus: "AWS EC2 metadata endpoint did not respond.");
        }
    }

    private async Task<string?> ReadMetadataTextAsync(
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://169.254.169.254/latest/meta-data/{path}");
            request.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static async Task<string?> ResolvePublicDomainAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(PublicDomain, cancellationToken);
            return addresses.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadGpuSummaryAsync(CancellationToken cancellationToken)
    {
        var nvidia = await RunProcessAsync(
            "nvidia-smi",
            ["--query-gpu=name,memory.total", "--format=csv,noheader"],
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(nvidia))
        {
            return nvidia.ReplaceLineEndings("; ");
        }

        var lspci = await RunProcessAsync("lspci", [], cancellationToken);
        var gpuLines = lspci
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("3D controller", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("GPU", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return gpuLines.Count == 0 ? "No GPU detected" : string.Join("; ", gpuLines);
    }

    private static string ReadMemorySummary()
    {
        const string memInfoPath = "/proc/meminfo";
        if (!File.Exists(memInfoPath))
        {
            return "Unavailable";
        }

        var memTotalLine = File.ReadLines(memInfoPath)
            .FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(memTotalLine))
        {
            return "Unavailable";
        }

        var parts = memTotalLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb)
            ? $"{kb / 1024d / 1024d:0.#} GiB"
            : "Unavailable";
    }

    private static string ReadDiskSummary()
    {
        var root = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .OrderByDescending(drive => drive.Name == "/" || drive.Name.Equals("C:\\", StringComparison.OrdinalIgnoreCase))
            .ThenBy(drive => drive.Name)
            .FirstOrDefault();
        if (root is null)
        {
            return "Unavailable";
        }

        var total = root.TotalSize / 1024d / 1024d / 1024d;
        var available = root.AvailableFreeSpace / 1024d / 1024d / 1024d;
        return $"{total:0.#} GiB total, {available:0.#} GiB available";
    }

    private static string ReadLoadSummary()
    {
        const string loadAveragePath = "/proc/loadavg";
        if (!File.Exists(loadAveragePath))
        {
            return "Unavailable";
        }

        var parts = File.ReadAllText(loadAveragePath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return "Unavailable";
        }

        return $"{parts[0]} / {parts[1]} / {parts[2]} load avg";
    }

    private static int ReadLoadPercent()
    {
        const string loadAveragePath = "/proc/loadavg";
        if (!File.Exists(loadAveragePath))
        {
            return 0;
        }

        var firstLoad = File.ReadAllText(loadAveragePath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!double.TryParse(firstLoad, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var load))
        {
            return 0;
        }

        var cpuCount = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp((int)Math.Round(load * 100d / cpuCount), 0, 100);
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            return string.Empty;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (await outputTask).Trim();
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore cleanup failures for optional hardware probing.
            }

            return string.Empty;
        }
    }

    private sealed record AwsInstanceMetadata(
        string? InstanceId = null,
        string? InstanceType = null,
        string? Region = null,
        string? AvailabilityZone = null,
        string? PrivateIpAddress = null,
        string? PublicIpAddress = null,
        string? AmiId = null,
        string? AccountId = null,
        string? InstanceState = null,
        string? GpuSummary = null,
        string? MemorySummary = null,
        string? DiskSummary = null,
        string MetadataStatus = "AWS metadata not available");

    private sealed record AwsInstanceTypeInfo(
        string? GpuSummary = null,
        string? MemorySummary = null,
        string? DiskSummary = null);
}
