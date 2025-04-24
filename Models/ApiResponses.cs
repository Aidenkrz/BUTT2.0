using System.Text.Json.Serialization;

namespace PteroUpdateMonitor.Models;

public class ManifestData
{
    [JsonPropertyName("builds")]
    public Dictionary<string, BuildInfo> Builds { get; set; } = new();
}

public class BuildInfo
{
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }
    
}

public class ServerInfoData
{
    [JsonPropertyName("build")]
    public ServerBuildInfo Build { get; set; } = new();
    
}

public class ServerBuildInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

public class PteroWebSocketData
{
    [JsonPropertyName("data")]
    public PteroWebSocketInfo Data { get; set; } = new();
}

public class PteroWebSocketInfo
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("socket")]
    public string SocketUrl { get; set; } = string.Empty;
}

public class PteroWebSocketMessage
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }
}

public class DiscordWebhookPayload
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    
}