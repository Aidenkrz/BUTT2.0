namespace PteroUpdateMonitor.Models;

public class ServerConfig
{
    public required string Name { get; set; }
    public required string ManifestUrl { get; set; }
    public required string ServerIp { get; set; }
    public required string PterodactylApiKey { get; set; }
    public required string PterodactylApiUrl { get; set; }
    public required string PterodactylServerId { get; set; }
    public int CheckIntervalSeconds { get; set; } = 3600;
    public string? DiscordWebhookUrl { get; set; }
    public string DiscordUpdateMessage { get; set; } = "Server '{ServerName}' is updating!";
    public string? LogColor { get; set; }

    public string GetServerApiUrl(string endpoint) => EnsureTrailingSlash(ServerIp) + endpoint;
    public string GetPterodactylApiUrl(string endpoint) => EnsureTrailingSlash(PterodactylApiUrl) + $"api/client/servers/{PterodactylServerId}/{endpoint}";

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

public class AppSettings
{
    public List<ServerConfig> Servers { get; set; } = new();
}