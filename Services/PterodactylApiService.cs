using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using PteroUpdateMonitor.Models;
using Tomlyn;
using Tomlyn.Model;

namespace PteroUpdateMonitor.Services;

public class PterodactylApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PterodactylApiService> _logger;

    public PterodactylApiService(IHttpClientFactory httpClientFactory, ILogger<PterodactylApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private HttpClient CreateClient(ServerConfig serverConfig)
    {
        var client = _httpClientFactory.CreateClient("PterodactylApi");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverConfig.PterodactylApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<string?> GetWatchdogTokenAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = CreateClient(serverConfig);

        var encodedFilePath = Uri.EscapeDataString("/datadir/server_config.toml");
        var url = serverConfig.GetPterodactylApiUrl($"files/contents?file={encodedFilePath}");

        _logger.LogDebug("[{ServerName}] Fetching watchdog token from: {Url}", serverConfig.Name, url);

        try
        {
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to fetch server_config.toml. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
                return null;
            }

            var configContent = await response.Content.ReadAsStringAsync(cancellationToken);


            var model = Toml.ToModel(configContent);
            if (model.TryGetValue("watchdog", out var watchdogTable) && watchdogTable is TomlTable watchdogTomlTable &&
                watchdogTomlTable.TryGetValue("token", out var tokenValue) && tokenValue is string token)
            {
                _logger.LogInformation("[{ServerName}] Successfully retrieved watchdog token.", serverConfig.Name);
                return token.Trim('"');
            }

            _logger.LogWarning("[{ServerName}] Could not find 'Watchdog.token' in server_config.toml.", serverConfig.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error fetching or parsing watchdog token from Pterodactyl API.", serverConfig.Name);
            return null;
        }
    }

    public async Task<PteroWebSocketInfo?> GetWebSocketInfoAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = CreateClient(serverConfig);
        var url = serverConfig.GetPterodactylApiUrl("websocket");
        _logger.LogDebug("[{ServerName}] Fetching WebSocket info from: {Url}", serverConfig.Name, url);

        try
        {
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to fetch WebSocket info. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
                return null;
            }

            var wsData = await response.Content.ReadFromJsonAsync<PteroWebSocketData>(cancellationToken: cancellationToken);
            if (wsData?.Data == null || string.IsNullOrEmpty(wsData.Data.Token) || string.IsNullOrEmpty(wsData.Data.SocketUrl))
            {
                _logger.LogError("[{ServerName}] Received invalid WebSocket info from Pterodactyl API.", serverConfig.Name);
                return null;
            }

            _logger.LogInformation("[{ServerName}] Successfully retrieved WebSocket info.", serverConfig.Name);
            return wsData.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error fetching WebSocket info from Pterodactyl API.", serverConfig.Name);
            return null;
        }
    }

    public async Task<bool> SendPowerSignalAsync(ServerConfig serverConfig, string signal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signal) || !new[] { "start", "stop", "restart", "kill" }.Contains(signal.ToLower()))
        {
            _logger.LogError("[{ServerName}] Invalid power signal specified: {Signal}", serverConfig.Name, signal);
            return false;
        }

        var client = CreateClient(serverConfig);
        var url = serverConfig.GetPterodactylApiUrl("power");
        var payload = new { signal = signal.ToLower() };

        _logger.LogInformation("[{ServerName}] Sending power signal '{Signal}' to: {Url}", serverConfig.Name, signal, url);

        try
        {
            var response = await client.PostAsJsonAsync(url, payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to send power signal '{Signal}'. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, signal, response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("[{ServerName}] Power signal '{Signal}' sent successfully.", serverConfig.Name, signal);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error sending power signal '{Signal}' to Pterodactyl API.", serverConfig.Name, signal);
            return false;
        }
    }

    public async Task<bool> SendReinstallCommandAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = CreateClient(serverConfig);
        var url = serverConfig.GetPterodactylApiUrl("settings/reinstall");
        _logger.LogInformation("[{ServerName}] Sending reinstall command to: {Url}", serverConfig.Name, url);

        try
        {

            var response = await client.PostAsync(url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to send reinstall command. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("[{ServerName}] Reinstall command sent successfully.", serverConfig.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error sending reinstall command to Pterodactyl API.", serverConfig.Name);
            return false;
        }
    }
}