using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using PteroUpdateMonitor.Models;
using PteroUpdateMonitor.Services;
using Websocket.Client;
using System.Net.WebSockets;
using Serilog.Context;

namespace PteroUpdateMonitor.Workers;

public class ServerMonitorWorker : BackgroundService
{
    private readonly ILogger<ServerMonitorWorker> _logger;
    private readonly ServerConfig _serverConfig;
    private readonly Ss14ApiService _ss14ApiService;
    private readonly PterodactylApiService _pteroApiService;
    private readonly DiscordNotificationService _discordService;
    private readonly IHostApplicationLifetime _appLifetime;

    private WebsocketClient? _wsClient;
    private string? _currentWsToken;

    private bool _isUpdateInProgress = false;
    private TaskCompletionSource<bool>? _updateCompletionSource;

    public ServerMonitorWorker(
        ILogger<ServerMonitorWorker> logger,
        ServerConfig serverConfig,
        Ss14ApiService ss14ApiService,
        PterodactylApiService pteroApiService,
        DiscordNotificationService discordService,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _serverConfig = serverConfig;
        _ss14ApiService = ss14ApiService;
        _pteroApiService = pteroApiService;
        _discordService = discordService;
        _appLifetime = appLifetime;

        _logger.LogInformation("Initializing worker for server: {ServerName}", _serverConfig.Name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (LogContext.PushProperty("ServerName", _serverConfig.Name))
        using (LogContext.PushProperty("LogColor", _serverConfig.LogColor ?? "White"))
        {
            _logger.LogInformation("Starting worker");

            if (!IsConfigValid())
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_isUpdateInProgress)
                {
                    _logger.LogDebug("Update process is ongoing. Waiting...");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                _logger.LogInformation("Checking for updates...");

                try
                {
                    await CheckForAndProcessUpdatesAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Worker cancellation requested.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An unexpected error occurred during the update check cycle.");
                }

                var delay = TimeSpan.FromSeconds(_serverConfig.CheckIntervalSeconds);
                _logger.LogInformation("Next check in {Delay}", delay);
                await Task.Delay(delay, stoppingToken);

            }
        }

        _logger.LogInformation("Worker {ServerName} stopping.", _serverConfig.Name);
        await CleanupWebSocketAsync();
    }

    private bool IsConfigValid()
    {
        bool isValid = true;
        if (string.IsNullOrWhiteSpace(_serverConfig.ManifestUrl)) { _logger.LogError("Server '{ServerName}': ManifestUrl is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.ServerIp)) { _logger.LogError("Server '{ServerName}': ServerIp is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylApiKey)) { _logger.LogError("Server '{ServerName}': PterodactylApiKey is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylApiUrl)) { _logger.LogError("Server '{ServerName}': PterodactylApiUrl is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylServerId)) { _logger.LogError("Server '{ServerName}': PterodactylServerId is missing.", _serverConfig.Name); isValid = false; }
        if (_serverConfig.CheckIntervalSeconds <= 0) { _logger.LogError("Server '{ServerName}': CheckIntervalSeconds must be positive.", _serverConfig.Name); isValid = false; }
        return isValid;
    }

    private async Task CheckForAndProcessUpdatesAsync(CancellationToken stoppingToken)
    {
        var manifestData = await _ss14ApiService.FetchManifestDataAsync(_serverConfig, stoppingToken);
        if (manifestData?.Builds == null || !manifestData.Builds.Any())
        {
            _logger.LogWarning("Could not retrieve valid manifest data. Skipping check.");
            return;
        }

        var latestBuild = manifestData.Builds.OrderByDescending(kvp => kvp.Value.Time).FirstOrDefault();
        if (string.IsNullOrEmpty(latestBuild.Key))
        {
             _logger.LogWarning("Could not determine the latest build from the manifest.");
             return;
        }
        string latestBuildId = latestBuild.Key;
        _logger.LogInformation("Latest manifest build ID: {LatestBuildId}", latestBuildId);

        var currentBuildId = await _ss14ApiService.GetCurrentBuildVersionAsync(_serverConfig, stoppingToken);
        if (string.IsNullOrEmpty(currentBuildId))
        {
            _logger.LogWarning("Could not retrieve current server build version. Skipping check.");
            return;
        }
         _logger.LogInformation("Current server build ID: {CurrentBuildId}", currentBuildId);

        if (currentBuildId.Equals(latestBuildId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Server is up-to-date.");
            return;
        }

        _logger.LogWarning("Server build ({CurrentBuildId}) is outdated. Latest is {LatestBuildId}. Starting update process.",
             currentBuildId, latestBuildId);

        _isUpdateInProgress = true;
        _updateCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var watchdogToken = await _pteroApiService.GetWatchdogTokenAsync(_serverConfig, stoppingToken);
            if (string.IsNullOrEmpty(watchdogToken))
            {
                _logger.LogError("Failed to obtain watchdog token. Aborting update.");
                _updateCompletionSource.TrySetResult(false);
                return;
            }
            _logger.LogInformation("Obtained watchdog token.");

            bool updateCommandSent = await _ss14ApiService.SendUpdateCommandAsync(_serverConfig, watchdogToken, stoppingToken);
            if (!updateCommandSent)
            {
                 _logger.LogError("Failed to send update command to SS14 server. Aborting update.");
                 _updateCompletionSource.TrySetResult(false);
                 return;
            }
             _logger.LogInformation("Update command sent to SS14 server. Waiting for server restart via Pterodactyl WebSocket.");

            var wsInfo = await _pteroApiService.GetWebSocketInfoAsync(_serverConfig, stoppingToken);
            if (wsInfo == null)
            {
                _logger.LogError("Failed to obtain Pterodactyl WebSocket info. Aborting update.");
                _updateCompletionSource.TrySetResult(false);
                return;
            }

            await ConnectAndMonitorWebSocketAsync(wsInfo, stoppingToken);

            var completedTask = await Task.WhenAny(_updateCompletionSource.Task, Task.Delay(TimeSpan.FromMinutes(15), stoppingToken));

            if (completedTask != _updateCompletionSource.Task || !_updateCompletionSource.Task.Result)
                 _logger.LogError("Update process did not complete successfully via WebSocket or timed out.");
            else
                 _logger.LogInformation("Update process completed successfully via WebSocket.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error during update process execution.");
             _updateCompletionSource?.TrySetResult(false);
        }
        finally
        {
            _isUpdateInProgress = false;
            await CleanupWebSocketAsync();
            _updateCompletionSource = null;
             _logger.LogInformation("Update process finished.");
        }
    }

    private async Task ConnectAndMonitorWebSocketAsync(PteroWebSocketInfo wsInfo, CancellationToken stoppingToken)
    {
        await CleanupWebSocketAsync();

        var url = new Uri(wsInfo.SocketUrl);
        _currentWsToken = wsInfo.Token;

        var panelUri = new Uri(_serverConfig.PterodactylApiUrl);
        var origin = panelUri.GetLeftPart(UriPartial.Authority);

        Func<ClientWebSocket> clientFactory = () =>
        {
            var clientWebSocket = new ClientWebSocket();
            clientWebSocket.Options.SetRequestHeader("Origin", origin);
            return clientWebSocket;
        };

        _wsClient = new WebsocketClient(url, clientFactory);

        _wsClient.ReconnectTimeout = TimeSpan.FromSeconds(30);
        _wsClient.ErrorReconnectTimeout = TimeSpan.FromSeconds(30);

        _wsClient.MessageReceived.Subscribe(msg => HandleWebSocketMessage(msg.Text ?? string.Empty, stoppingToken));
        _wsClient.DisconnectionHappened.Subscribe(info => _logger.LogWarning("WebSocket disconnected: {Type}", info.Type));
        _wsClient.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("WebSocket reconnected: {Type}. Re-authenticating.", info.Type);
                SendWebSocketAuth();
            });

        _logger.LogInformation("Connecting to Pterodactyl WebSocket: {Url}", url);
        await _wsClient.StartOrFail();

        if (_wsClient.IsRunning)
        {
            _logger.LogInformation("WebSocket connected. Sending authentication.");
            SendWebSocketAuth();
        }
        else
        {
             _logger.LogError("Failed to start WebSocket connection.");
             _updateCompletionSource?.TrySetResult(false);
        }
    }

    private void SendWebSocketAuth()
    {
        if (_wsClient == null || !_wsClient.IsRunning || string.IsNullOrEmpty(_currentWsToken))
        {
            _logger.LogWarning("Cannot send WebSocket auth: client not running or token missing.");
            return;
        }

        var authPayload = new { @event = "auth", args = new[] { _currentWsToken } };
        var authJson = JsonSerializer.Serialize(authPayload);
        _wsClient.Send(authJson);
        _logger.LogDebug("Sent WebSocket authentication message.");
    }

    private async void HandleWebSocketMessage(string message, CancellationToken stoppingToken)
    {
        _logger.LogDebug("WebSocket message received: {Message}", message);

        try
        {
            var pteroMessage = JsonSerializer.Deserialize<PteroWebSocketMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (pteroMessage == null) return;

            switch (pteroMessage.Event?.ToLowerInvariant())
            {
                case "auth success":
                    _logger.LogInformation("WebSocket authentication successful.");
                    break;

                case "status":
                    var status = pteroMessage.Args?.FirstOrDefault() ?? "unknown";
                    _logger.LogInformation("Server status update: {Status}", status);
                    if (status == "starting" && _isUpdateInProgress)
                    {
                        _logger.LogWarning("Server is 'starting'. Initiating post-update sequence (kill -> reinstall -> start).");
                        _ = Task.Run(() => PerformPostUpdateSequence(stoppingToken), stoppingToken);
                    }
                    break;

                case "token expiring":
                    _logger.LogWarning("WebSocket token expiring. Requesting new token.");
                    _ = Task.Run(async () => {
                        using (LogContext.PushProperty("ServerName", _serverConfig.Name))
                        using (LogContext.PushProperty("LogColor", _serverConfig.LogColor ?? "White"))
                        {
                            var newWsInfo = await _pteroApiService.GetWebSocketInfoAsync(_serverConfig, stoppingToken);
                            if (newWsInfo != null)
                            {
                                _currentWsToken = newWsInfo.Token;
                                SendWebSocketAuth();
                            }
                            else
                            {
                                _logger.LogError("Failed to get new WebSocket token after expiry warning.");
                                _updateCompletionSource?.TrySetResult(false);
                                await CleanupWebSocketAsync();
                            }
                        }
                    }, stoppingToken);
                    break;

                case "token expired":
                    _logger.LogError("WebSocket token expired. Closing connection.");
                    _updateCompletionSource?.TrySetResult(false);
                    await CleanupWebSocketAsync();
                    break;

                case "error":
                case "daemon error":
                    _logger.LogError("Received error via WebSocket: {Args}", string.Join(", ", pteroMessage.Args ?? new List<string>()));
                    break;

                default:
                    break;
            }
        }
        catch (JsonException jsonEx)
        {
            _logger.LogWarning(jsonEx, "Failed to deserialize WebSocket message: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket message.");
        }
    }

    private async Task PerformPostUpdateSequence(CancellationToken stoppingToken)
    {
        bool success = false;
        try
        {
            _logger.LogInformation("Starting post-update sequence...");

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            _logger.LogInformation("Sending KILL signal...");
            if (!await _pteroApiService.SendPowerSignalAsync(_serverConfig, "kill", stoppingToken))
            {
                _logger.LogError("Failed to send KILL signal. Aborting sequence.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            _logger.LogInformation("Sending REINSTALL command...");
            if (!await _pteroApiService.SendReinstallCommandAsync(_serverConfig, stoppingToken))
            {
                _logger.LogError("Failed to send REINSTALL command. Aborting sequence.");
                return;
            }
            _logger.LogInformation("Waiting for reinstall process to complete (15 seconds)...");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            _logger.LogInformation("Sending START signal...");
            if (!await _pteroApiService.SendPowerSignalAsync(_serverConfig, "start", stoppingToken))
            {
                _logger.LogError("Failed to send START signal.");
            }
            else
            {
                _logger.LogInformation("Server start command sent.");
            }

            await _discordService.SendUpdateNotificationAsync(_serverConfig, stoppingToken);

            success = true;
            _logger.LogInformation("Post-update sequence completed.");

        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Post-update sequence cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during post-update sequence.");
        }
        finally
        {
            _updateCompletionSource?.TrySetResult(success);
            await CleanupWebSocketAsync();
        }
    }

    private async Task CleanupWebSocketAsync()
    {
        if (_wsClient != null)
        {
            if (_wsClient.IsRunning)
            {
                _logger.LogInformation("Server '{ServerName}': Closing WebSocket connection.", _serverConfig.Name);
                await _wsClient.Stop(WebSocketCloseStatus.NormalClosure, "Worker stopping");
            }
            _wsClient.Dispose();
            _wsClient = null;
            _currentWsToken = null;
            _logger.LogDebug("Server '{ServerName}': WebSocket client disposed.", _serverConfig.Name);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping worker for server: {ServerName}", _serverConfig.Name);
        await CleanupWebSocketAsync();
        await base.StopAsync(cancellationToken);
    }
}
