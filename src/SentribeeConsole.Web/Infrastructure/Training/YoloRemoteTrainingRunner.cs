using System.Diagnostics;
using System.Text;

namespace SentribeeConsole.Web.Infrastructure.Training;

public sealed class YoloRemoteTrainingRunner(
    IConfiguration configuration,
    ILogger<YoloRemoteTrainingRunner> logger)
{
    private readonly string _modelHost = configuration["AiModel:SshHost"] ?? "3.27.97.172";
    private readonly string _modelSshUser = configuration["AiModel:SshUser"] ?? "ubuntu";
    private readonly string _modelSshKeyPath = configuration["AiModel:SshKeyPath"]
        ?? configuration["EdgeRuntime:SshKeyPath"]
        ?? "/home/ubuntu/.ssh/id_ed25519";
    private readonly string _runtimeRoot = configuration["AiModel:RuntimeRoot"]
        ?? configuration["EdgeRuntime:Root"]
        ?? "/opt/sentribee-edge-ai";
    private readonly string _venvPath = configuration["AiModel:VenvPath"] ?? string.Empty;
    private readonly string _panoramaBaseModelPath = configuration["AiModel:PanoramaBaseModelPath"] ?? string.Empty;
    private readonly string _personSlicePpeBaseModelPath = configuration["AiModel:PersonSlicePpeBaseModelPath"] ?? string.Empty;
    private readonly string _panoramaDataYamlPath = configuration["AiModel:PanoramaDataYamlPath"]
        ?? "/home/ubuntu/sentribee/hobson/data.yaml";
    private readonly string _personSlicePpeDataYamlPath = configuration["AiModel:PersonSlicePpeDataYamlPath"]
        ?? "/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml";
    private readonly string _device = configuration["AiModel:Device"] ?? configuration["DEVICE"] ?? "0";
    private readonly string _workers = configuration["AiModel:Workers"] ?? configuration["WORKERS"] ?? "2";
    private readonly string _fullEpochs = configuration["AiModel:FullEpochs"] ?? configuration["FULL_EPOCHS"] ?? "60";
    private readonly string _fullImageSize = configuration["AiModel:FullImageSize"] ?? configuration["FULL_IMGSZ"] ?? "960";
    private readonly string _fullBatch = configuration["AiModel:FullBatch"] ?? configuration["FULL_BATCH"] ?? "8";
    private readonly string _cropEpochs = configuration["AiModel:CropEpochs"] ?? configuration["CROP_EPOCHS"] ?? "60";
    private readonly string _cropImageSize = configuration["AiModel:CropImageSize"] ?? configuration["CROP_IMGSZ"] ?? "640";
    private readonly string _cropBatch = configuration["AiModel:CropBatch"] ?? configuration["CROP_BATCH"] ?? "16";
    private readonly string _yoloConfigDirectory = configuration["AiModel:YoloConfigDirectory"]
        ?? configuration["YOLO_CONFIG_DIR"]
        ?? string.Empty;

    public async Task<YoloTrainingArtifact> RunAsync(string modelKind, CancellationToken cancellationToken)
    {
        modelKind = YoloTrainingKinds.Normalize(modelKind);
        var remoteCommand = modelKind == YoloTrainingKinds.PersonSlicePpe
            ? BuildPersonSlicePpeCommand()
            : BuildPanoramaCommand();
        logger.LogInformation("Starting remote {ModelKind} YOLO training on {ModelHost}.", modelKind, _modelHost);
        var output = await RunSshAsync(remoteCommand, cancellationToken);
        return ParseArtifact(output, modelKind);
    }

    private string BuildPanoramaCommand()
    {
        var root = QuoteBashDouble(_runtimeRoot);
        var venv = QuoteBashDouble(string.IsNullOrWhiteSpace(_venvPath) ? $"{_runtimeRoot}/venv" : _venvPath);
        var model = QuoteBashDouble(string.IsNullOrWhiteSpace(_panoramaBaseModelPath) ? $"{_runtimeRoot}/yolo11n.pt" : _panoramaBaseModelPath);
        var data = QuoteBashDouble(_panoramaDataYamlPath);
        var device = QuoteBashDouble(_device);
        var workers = QuoteBashDouble(_workers);
        var epochs = QuoteBashDouble(_fullEpochs);
        var imageSize = QuoteBashDouble(_fullImageSize);
        var batch = QuoteBashDouble(_fullBatch);
        var yoloConfigDirectory = string.IsNullOrWhiteSpace(_yoloConfigDirectory)
            ? string.Empty
            : $"export YOLO_CONFIG_DIR={QuoteBashDouble(_yoloConfigDirectory)}";
        return $$"""
            bash -lc 'set -euo pipefail
            ROOT={{root}}
            VENV={{venv}}
            MODEL={{model}}
            DATA_YAML={{data}}
            DEVICE_VALUE={{device}}
            WORKERS_VALUE={{workers}}
            EPOCHS_VALUE={{epochs}}
            IMGSZ_VALUE={{imageSize}}
            BATCH_VALUE={{batch}}
            RUN_ROOT=$ROOT/runs/sentribee_training
            LOG_DIR=$ROOT/training_logs
            STAMP=$(date -u +%Y%m%d_%H%M%S)
            mkdir -p "$RUN_ROOT" "$LOG_DIR"
            {{yoloConfigDirectory}}
            cd "$ROOT"
            test -x "$VENV/bin/yolo"
            test -f "$MODEL"
            test -f "$DATA_YAML"
            "$VENV/bin/yolo" detect train model="$MODEL" data="$DATA_YAML" epochs="$EPOCHS_VALUE" imgsz="$IMGSZ_VALUE" batch="$BATCH_VALUE" workers="$WORKERS_VALUE" device="$DEVICE_VALUE" project="$RUN_ROOT" name="full_image_$STAMP" patience=15 plots=True exist_ok=True 2>&1 | tee "$LOG_DIR/train_panorama_$STAMP.log"
            BEST="$RUN_ROOT/full_image_$STAMP/weights/best.pt"
            TARGET="$MODEL"
            test -f "$BEST"
            cp "$TARGET" "$TARGET.bak_$STAMP" 2>/dev/null || true
            cp "$BEST" "$TARGET"
            sudo systemctl restart sentribee-edge.service 2>/dev/null || true
            sudo systemctl restart sentribee-edge-event-analysis.service 2>/dev/null || true
            echo "SENTRIBEE_TRAINING_VERSION=panorama_$STAMP"
            echo "SENTRIBEE_TRAINING_BEST=$BEST"
            echo "SENTRIBEE_TRAINING_DEPLOYED=$TARGET"'
            """;
    }

    private string BuildPersonSlicePpeCommand()
    {
        var root = QuoteBashDouble(_runtimeRoot);
        var venv = QuoteBashDouble(string.IsNullOrWhiteSpace(_venvPath) ? $"{_runtimeRoot}/venv" : _venvPath);
        var model = QuoteBashDouble(string.IsNullOrWhiteSpace(_personSlicePpeBaseModelPath) ? $"{_runtimeRoot}/best2.pt" : _personSlicePpeBaseModelPath);
        var data = QuoteBashDouble(_personSlicePpeDataYamlPath);
        var device = QuoteBashDouble(_device);
        var workers = QuoteBashDouble(_workers);
        var epochs = QuoteBashDouble(_cropEpochs);
        var imageSize = QuoteBashDouble(_cropImageSize);
        var batch = QuoteBashDouble(_cropBatch);
        var yoloConfigDirectory = string.IsNullOrWhiteSpace(_yoloConfigDirectory)
            ? string.Empty
            : $"export YOLO_CONFIG_DIR={QuoteBashDouble(_yoloConfigDirectory)}";
        return $$"""
            bash -lc 'set -euo pipefail
            ROOT={{root}}
            VENV={{venv}}
            MODEL={{model}}
            DATA_YAML={{data}}
            DEVICE_VALUE={{device}}
            WORKERS_VALUE={{workers}}
            EPOCHS_VALUE={{epochs}}
            IMGSZ_VALUE={{imageSize}}
            BATCH_VALUE={{batch}}
            RUN_ROOT=$ROOT/runs/sentribee_training
            LOG_DIR=$ROOT/training_logs
            STAMP=$(date -u +%Y%m%d_%H%M%S)
            mkdir -p "$RUN_ROOT" "$LOG_DIR"
            {{yoloConfigDirectory}}
            cd "$ROOT"
            test -x "$VENV/bin/yolo"
            test -f "$MODEL"
            test -f "$DATA_YAML"
            "$VENV/bin/yolo" detect train model="$MODEL" data="$DATA_YAML" epochs="$EPOCHS_VALUE" imgsz="$IMGSZ_VALUE" batch="$BATCH_VALUE" workers="$WORKERS_VALUE" device="$DEVICE_VALUE" project="$RUN_ROOT" name="person_crop_ppe_$STAMP" patience=15 plots=True exist_ok=True 2>&1 | tee "$LOG_DIR/train_person_crop_ppe_$STAMP.log"
            BEST="$RUN_ROOT/person_crop_ppe_$STAMP/weights/best.pt"
            TARGET="$MODEL"
            test -f "$BEST"
            cp "$TARGET" "$TARGET.bak_$STAMP" 2>/dev/null || true
            cp "$BEST" "$TARGET"
            sudo systemctl restart sentribee-edge.service 2>/dev/null || true
            sudo systemctl restart sentribee-edge-event-analysis.service 2>/dev/null || true
            echo "SENTRIBEE_TRAINING_VERSION=person_crop_ppe_$STAMP"
            echo "SENTRIBEE_TRAINING_BEST=$BEST"
            echo "SENTRIBEE_TRAINING_DEPLOYED=$TARGET"'
            """;
    }

    private static string QuoteBashDouble(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)}\"";
    }

    private async Task<string> RunSshAsync(string remoteCommand, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start SSH process.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        return output;
    }

    private static YoloTrainingArtifact ParseArtifact(string output, string modelKind)
    {
        var values = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("SENTRIBEE_TRAINING_", StringComparison.Ordinal))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        var versionName = values.TryGetValue("SENTRIBEE_TRAINING_VERSION", out var version)
            ? version
            : $"{YoloTrainingKinds.Normalize(modelKind).ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        values.TryGetValue("SENTRIBEE_TRAINING_BEST", out var bestPath);
        values.TryGetValue("SENTRIBEE_TRAINING_DEPLOYED", out var deployedPath);
        return new YoloTrainingArtifact(versionName, bestPath, deployedPath);
    }
}

public sealed record YoloTrainingArtifact(
    string VersionName,
    string? BestModelPath,
    string? DeployedModelPath);
