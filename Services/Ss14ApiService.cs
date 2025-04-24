using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PteroUpdateMonitor.Models;

namespace PteroUpdateMonitor.Services;

public class Ss14ApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Ss14ApiService> _logger;

    public Ss14ApiService(IHttpClientFactory httpClientFactory, ILogger<Ss14ApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ManifestData?> FetchManifestDataAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("Ss14Api");
        _logger.LogDebug("[{ServerName}] Fetching manifest data from: {Url}", serverConfig.Name, serverConfig.ManifestUrl);

        try
        {
            var response = await client.GetAsync(serverConfig.ManifestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to fetch manifest data. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
                return null;
            }

            var manifestData = await response.Content.ReadFromJsonAsync<ManifestData>(cancellationToken: cancellationToken);
            if (manifestData == null || manifestData.Builds == null || !manifestData.Builds.Any())
            {
                _logger.LogWarning("[{ServerName}] Manifest data fetched successfully but was empty or invalid.", serverConfig.Name);
                return null;
            }

            _logger.LogInformation("[{ServerName}] Successfully fetched manifest data.", serverConfig.Name);
            return manifestData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error fetching manifest data.", serverConfig.Name);
            return null;
        }
    }

    public async Task<string?> GetCurrentBuildVersionAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("Ss14Api");
        var url = serverConfig.GetServerApiUrl("info");
        _logger.LogDebug("[{ServerName}] Fetching current build version from: {Url}", serverConfig.Name, url);

        try
        {
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to fetch server info. Status: {StatusCode}. Response: {ErrorContent}. Is the server running?",
                    serverConfig.Name, response.StatusCode, errorContent);
                return null;
            }

            var serverInfo = await response.Content.ReadFromJsonAsync<ServerInfoData>(cancellationToken: cancellationToken);
            if (serverInfo?.Build == null || string.IsNullOrEmpty(serverInfo.Build.Version))
            {
                _logger.LogWarning("[{ServerName}] Server info fetched successfully but build version was missing or invalid.", serverConfig.Name);
                return null;
            }

            _logger.LogInformation("[{ServerName}] Successfully fetched current build version: {Version}", serverConfig.Name, serverInfo.Build.Version);
            return serverInfo.Build.Version;
        }
        catch (HttpRequestException httpEx) when (httpEx.InnerException is System.Net.Sockets.SocketException)
        {
            _logger.LogError(httpEx, "[{ServerName}] Error fetching server info. Could not connect to {Url}. Is the server running and the address correct?", serverConfig.Name, url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error fetching server info.", serverConfig.Name);
            return null;
        }
    }

    public async Task<bool> SendUpdateCommandAsync(ServerConfig serverConfig, string watchdogToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(watchdogToken))
        {
            _logger.LogError("[{ServerName}] Cannot send update command without a watchdog token.", serverConfig.Name);
            return false;
        }

        var client = _httpClientFactory.CreateClient("Ss14Api");
        var url = serverConfig.GetServerApiUrl("update");
        _logger.LogInformation("[{ServerName}] Sending update notification to: {Url}", serverConfig.Name, url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("WatchdogToken", watchdogToken);

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to send update command. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("[{ServerName}] Update command sent successfully to SS14 server.", serverConfig.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error sending update command to SS14 server.", serverConfig.Name);
            return false;
        }
    }
}