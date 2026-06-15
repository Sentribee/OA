namespace SentribeeConsole.Web.Infrastructure.Runtime;

public sealed record EdgeRuntimeEnvironmentMapping(
    string Key,
    string Group,
    string Source,
    string Target,
    bool Required,
    bool Secret,
    string Description);

public static class EdgeRuntimeEnvironmentMap
{
    public static IReadOnlyList<EdgeRuntimeEnvironmentMapping> Items { get; } =
    [
        new("SENTRIBEE_INSTANCE", "Instance", "Edge device code", "Remote process env and .env", true, false, "Identifies the running Edge AI instance."),
        new("SENTRIBEE_INSTANCE_ENV", "Instance", "Generated instance .env path", "Remote process env", true, false, "Forces main.py to load the selected device instance .env."),
        new("DEVICE_CODE", "Device", "Edge device code", ".env", true, false, "Runtime device identifier used by console heartbeat and events."),
        new("DEVICE_ID", "Device", "Edge device code", ".env", true, false, "Compatibility alias for runtime code paths that still read DEVICE_ID."),
        new("CAMERA_ID", "Device", "Edge device code", ".env", true, false, "Compatibility alias used by older camera logic."),
        new("RTSP_URL", "Stream", "Primary RTSP external device URL", ".env", true, false, "Primary camera stream for the device."),
        new("RTSP_FALLBACK_URL", "Stream", "Derived from RTSP_URL stream1 -> stream2", ".env", false, false, "Fallback stream used by reconnection logic."),
        new("GATEWAY_MAC", "Gateway", "Gateway/BLE/MAC external device URL", ".env", false, false, "Bracelet gateway MAC used by proximity logic."),
        new("HLS_OUTPUT_DIR", "Stream", "instances/{deviceCode}/video", ".env", true, false, "Per-device HLS output folder used by public live stream URLs."),
        new("SENTRIBEE_CLIENT_NAME", "Console", "{deviceCode}-edge-ai-client", ".env", true, false, "Readable API client name used during auth."),
        new("SENTRIBEE_CONSOLE_URL", "Console", "Template value or https://console.sentribee.ai", ".env", true, false, "Console endpoint for auth, heartbeat, and event upload."),
        new("SENTRIBEE_CONSOLE_API_KEY", "Managed", "Existing instance/template managed value", ".env", true, true, "Project API key; hidden from device detail and maintained by the console."),
        new("OPENAI_API_KEY", "Managed", "Console OpenAI configuration", ".env", true, true, "OpenAI API key; hidden from device detail and maintained by the console."),
        new("OPENAI_MODEL", "Managed", "Console OpenAI configuration", ".env", true, false, "OpenAI model; hidden from device detail and maintained by the console."),
        new("OPENAI_BASE_URL", "Managed", "Console OpenAI configuration", ".env", false, false, "OpenAI base URL; hidden from device detail and maintained by the console."),
        new("SENTRIBEE_EDGE_AUTH_PATH", "Console", "Template value or /api/edge/auth", ".env", true, false, "Auth endpoint path."),
        new("SENTRIBEE_EDGE_HEARTBEAT_PATH", "Console", "Template value or /api/edge/heartbeat", ".env", true, false, "Heartbeat endpoint path."),
        new("SENTRIBEE_EDGE_EVENTS_PATH", "Console", "Template value or /api/edge/events", ".env", true, false, "Event upload endpoint path."),
        new("SENTRIBEE_HEARTBEAT_INTERVAL", "Console", "Template value or 30", ".env", true, false, "Heartbeat interval in seconds."),
        new("SENTRIBEE_DEVICE_ID", "Compatibility", "Edge device code", ".env", false, false, "Legacy SentriBee device id alias."),
        new("SENTRIBEE_DEVICE_DB_ID", "Compatibility", "Console database id", ".env", false, false, "Console database id for debugging and future linking."),
        new("SENTRIBEE_RTSP_URL", "Compatibility", "Primary RTSP external device URL", ".env", false, false, "Legacy RTSP alias."),
        new("SENTRIBEE_GATEWAY_MAC", "Compatibility", "Gateway/BLE/MAC external device URL", ".env", false, false, "Legacy gateway MAC alias.")
    ];
}
