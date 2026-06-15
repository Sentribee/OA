namespace SentribeeConsole.Web.Infrastructure.Analysis;

public sealed class EdgeEventAutoAnalysisOptions
{
    public const string SectionName = "EdgeEventAutoAnalysis";

    public bool Enabled { get; set; } = true;

    public string Mode { get; set; } = "Remote";

    public string RemoteBaseUrl { get; set; } = "http://ins1.sentribee.ai:8097/";

    public string RemoteAnalyzePath { get; set; } = "/api/edge-event-analysis/analyze";

    public string RemoteApiKey { get; set; } = "";

    public bool FallbackToLocal { get; set; }

    public string PythonPath { get; set; } = "python3";

    public string ScriptPath { get; set; } = "Tools/console_event_analyzer.py";

    public string? PersonModelPath { get; set; }

    public List<string> PpeModelPaths { get; set; } = [];

    public decimal PersonConfidence { get; set; } = 0.35m;

    public decimal PpeConfidence { get; set; } = 0.20m;

    public decimal SceneObjectConfidence { get; set; } = 0.25m;

    public decimal ValidationRatio { get; set; } = 0.20m;

    public decimal PersonCropScale { get; set; } = 1.10m;

    public int PersonCropMinSide { get; set; } = 96;

    public string RequiredPpe { get; set; } = "helmet,vest";

    public string OutputRelativePath { get; set; } = "edge-event-analysis/events";

    public int TimeoutSeconds { get; set; } = 300;

    public bool UseOpenAI { get; set; }

    public string OpenAIModel { get; set; } = "";

    public string RiskZonesJson { get; set; } = "";
}
