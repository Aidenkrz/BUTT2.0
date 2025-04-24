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
        _logger.LogDebug("Fetching manifest data from: {Url}", serverConfig.ManifestUrl);

        try
        {
            var response = await client.GetAsync(serverConfig.ManifestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch manifest data. Status: {StatusCode}. Response: {ErrorContent}", response.StatusCode, errorContent);
                return null;
            }

            var manifestData = await response.Content.ReadFromJsonAsync<ManifestData>(cancellationToken: cancellationToken);
            if (manifestData == null || manifestData.Builds == null || !manifestData.Builds.Any())
            {
                _logger.LogWarning("Manifest data fetched successfully but was empty or invalid.");
                return null;
            }

            _logger.LogInformation("Successfully fetched manifest data.");
            return manifestData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching manifest data.");
            return null;
        }
    }

    public async Task<string?> GetCurrentBuildVersionAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("Ss14Api");
        var url = serverConfig.GetServerApiUrl("info");
        _logger.LogDebug("Fetching current build version from: {Url}", url);

        try
        {
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch server info. Status: {StatusCode}. Response: {ErrorContent}. Is the server running?", response.StatusCode, errorContent);
                return null;
            }

            var serverInfo = await response.Content.ReadFromJsonAsync<ServerInfoData>(cancellationToken: cancellationToken);
            if (serverInfo?.Build == null || string.IsNullOrEmpty(serverInfo.Build.Version))
            {
                _logger.LogWarning("Server info fetched successfully but build version was missing or invalid.");
                return null;
            }

            _logger.LogInformation("Successfully fetched current build version: {Version}", serverInfo.Build.Version);
            return serverInfo.Build.Version;
        }
        catch (HttpRequestException httpEx) when (httpEx.InnerException is System.Net.Sockets.SocketException)
        {
            _logger.LogError(httpEx, "Error fetching server info. Could not connect to {Url}. Is the server running and the address correct?", url);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching server info.");
            return null;
        }
    }

    public async Task<bool> SendUpdateCommandAsync(ServerConfig serverConfig, string watchdogToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(watchdogToken))
        {
            _logger.LogError("Cannot send update command without a watchdog token.");
            return false;
        }

        var client = _httpClientFactory.CreateClient("Ss14Api");
        var url = serverConfig.GetServerApiUrl("update");
        _logger.LogInformation("Sending update notification to: {Url}", url);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("WatchdogToken", watchdogToken);

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to send update command. Status: {StatusCode}. Response: {ErrorContent}", response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Update command sent successfully to SS14 server.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending update command to SS14 server.");
            return false;
        }
    }
}