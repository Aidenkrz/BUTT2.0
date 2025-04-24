using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using PteroUpdateMonitor.Models;
using PteroUpdateMonitor.Services;
using Websocket.Client;
using System.Net.WebSockets;

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
        _logger.LogInformation("Starting worker for server: {ServerName}", _serverConfig.Name);


        if (!IsConfigValid())
        {
            _logger.LogCritical("[{ServerName}] Configuration is invalid. Worker cannot start.", _serverConfig.Name);


            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_isUpdateInProgress)
            {


                _logger.LogDebug("[{ServerName}] Update process is ongoing. Waiting...", _serverConfig.Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            _logger.LogInformation("[{ServerName}] Checking for updates...", _serverConfig.Name);

            try
            {
                await CheckForAndProcessUpdatesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[{ServerName}] Worker cancellation requested.", _serverConfig.Name);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ServerName}] An unexpected error occurred during the update check cycle.", _serverConfig.Name);

            }


            var delay = TimeSpan.FromSeconds(_serverConfig.CheckIntervalSeconds);
            _logger.LogInformation("[{ServerName}] Next check in {Delay}", _serverConfig.Name, delay);
            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("Stopping worker for server: {ServerName}", _serverConfig.Name);
        await CleanupWebSocketAsync();
    }

    private bool IsConfigValid()
    {
        bool isValid = true;
        if (string.IsNullOrWhiteSpace(_serverConfig.ManifestUrl)) { _logger.LogError("[{ServerName}] ManifestUrl is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.ServerIp)) { _logger.LogError("[{ServerName}] ServerIp is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylApiKey)) { _logger.LogError("[{ServerName}] PterodactylApiKey is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylApiUrl)) { _logger.LogError("[{ServerName}] PterodactylApiUrl is missing.", _serverConfig.Name); isValid = false; }
        if (string.IsNullOrWhiteSpace(_serverConfig.PterodactylServerId)) { _logger.LogError("[{ServerName}] PterodactylServerId is missing.", _serverConfig.Name); isValid = false; }
        if (_serverConfig.CheckIntervalSeconds <= 0) { _logger.LogError("[{ServerName}] CheckIntervalSeconds must be positive.", _serverConfig.Name); isValid = false; }
        return isValid;
    }


    private async Task CheckForAndProcessUpdatesAsync(CancellationToken stoppingToken)
    {

        var manifestData = await _ss14ApiService.FetchManifestDataAsync(_serverConfig, stoppingToken);
        if (manifestData?.Builds == null || !manifestData.Builds.Any())
        {
            _logger.LogWarning("[{ServerName}] Could not retrieve valid manifest data. Skipping check.", _serverConfig.Name);
            return;
        }


        var latestBuild = manifestData.Builds.OrderByDescending(kvp => kvp.Value.Time).FirstOrDefault();
        if (string.IsNullOrEmpty(latestBuild.Key))
        {
            _logger.LogWarning("[{ServerName}] Could not determine the latest build from the manifest.", _serverConfig.Name);
            return;
        }
        string latestBuildId = latestBuild.Key;
        _logger.LogInformation("[{ServerName}] Latest manifest build ID: {LatestBuildId}", _serverConfig.Name, latestBuildId);



        var currentBuildId = await _ss14ApiService.GetCurrentBuildVersionAsync(_serverConfig, stoppingToken);
        if (string.IsNullOrEmpty(currentBuildId))
        {
            _logger.LogWarning("[{ServerName}] Could not retrieve current server build version. Skipping check.", _serverConfig.Name);
            return;
        }
        _logger.LogInformation("[{ServerName}] Current server build ID: {CurrentBuildId}", _serverConfig.Name, currentBuildId);


        if (currentBuildId.Equals(latestBuildId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[{ServerName}] Server is up-to-date.", _serverConfig.Name);
            return;
        }

        _logger.LogWarning("[{ServerName}] Server build ({CurrentBuildId}) is outdated. Latest is {LatestBuildId}. Starting update process.",
            _serverConfig.Name, currentBuildId, latestBuildId);

        _isUpdateInProgress = true;
        _updateCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);


        try
        {
            var watchdogToken = await _pteroApiService.GetWatchdogTokenAsync(_serverConfig, stoppingToken);
            if (string.IsNullOrEmpty(watchdogToken))
            {
                _logger.LogError("[{ServerName}] Failed to obtain watchdog token. Aborting update.", _serverConfig.Name);
                _updateCompletionSource.TrySetResult(false);
                return;
            }
            _logger.LogInformation("[{ServerName}] Obtained watchdog token.", _serverConfig.Name);

            bool updateCommandSent = await _ss14ApiService.SendUpdateCommandAsync(_serverConfig, watchdogToken, stoppingToken);
            if (!updateCommandSent)
            {
                _logger.LogError("[{ServerName}] Failed to send update command to SS14 server. Aborting update.", _serverConfig.Name);
                _updateCompletionSource.TrySetResult(false);
                return;
            }
            _logger.LogInformation("[{ServerName}] Update command sent to SS14 server. Waiting for server restart via Pterodactyl WebSocket.", _serverConfig.Name);

            var wsInfo = await _pteroApiService.GetWebSocketInfoAsync(_serverConfig, stoppingToken);
            if (wsInfo == null)
            {
                _logger.LogError("[{ServerName}] Failed to obtain Pterodactyl WebSocket info. Aborting update.", _serverConfig.Name);
                _updateCompletionSource.TrySetResult(false);
                return;
            }

            await ConnectAndMonitorWebSocketAsync(wsInfo, stoppingToken);

            var completedTask = await Task.WhenAny(_updateCompletionSource.Task, Task.Delay(TimeSpan.FromMinutes(15), stoppingToken));

            if (completedTask != _updateCompletionSource.Task || !_updateCompletionSource.Task.Result)
            {
                _logger.LogError("[{ServerName}] Update process did not complete successfully via WebSocket or timed out.", _serverConfig.Name);
            }
            else
            {
                _logger.LogInformation("[{ServerName}] Update process completed successfully via WebSocket.", _serverConfig.Name);
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error during update process execution.", _serverConfig.Name);
            _updateCompletionSource?.TrySetResult(false);
        }
        finally
        {
            _isUpdateInProgress = false;
            await CleanupWebSocketAsync();
            _updateCompletionSource = null;
            _logger.LogInformation("[{ServerName}] Update process finished.", _serverConfig.Name);
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
        _wsClient.DisconnectionHappened.Subscribe(info => _logger.LogWarning("[{ServerName}] WebSocket disconnected: {Type}", _serverConfig.Name, info.Type));
        _wsClient.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("[{ServerName}] WebSocket reconnected: {Type}. Re-authenticating.", _serverConfig.Name, info.Type);
                SendWebSocketAuth();
            });

        _logger.LogInformation("[{ServerName}] Connecting to Pterodactyl WebSocket: {Url}", _serverConfig.Name, url);
        await _wsClient.StartOrFail();

        if (_wsClient.IsRunning)
        {
            _logger.LogInformation("[{ServerName}] WebSocket connected. Sending authentication.", _serverConfig.Name);
            SendWebSocketAuth();
        }
        else
        {
            _logger.LogError("[{ServerName}] Failed to start WebSocket connection.", _serverConfig.Name);
            _updateCompletionSource?.TrySetResult(false);
        }
    }

    private void SendWebSocketAuth()
    {
        if (_wsClient == null || !_wsClient.IsRunning || string.IsNullOrEmpty(_currentWsToken))
        {
            _logger.LogWarning("[{ServerName}] Cannot send WebSocket auth: client not running or token missing.", _serverConfig.Name);
            return;
        }

        var authPayload = new { @event = "auth", args = new[] { _currentWsToken } };
        var authJson = JsonSerializer.Serialize(authPayload);
        _wsClient.Send(authJson);
        _logger.LogDebug("[{ServerName}] Sent WebSocket authentication message.", _serverConfig.Name);
    }

    private async void HandleWebSocketMessage(string message, CancellationToken stoppingToken)
    {
        _logger.LogDebug("[{ServerName}] WebSocket message received: {Message}", _serverConfig.Name, message);

        try
        {
            var pteroMessage = JsonSerializer.Deserialize<PteroWebSocketMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (pteroMessage == null) return;

            switch (pteroMessage.Event?.ToLowerInvariant())
            {
                case "auth success":
                    _logger.LogInformation("[{ServerName}] WebSocket authentication successful.", _serverConfig.Name);
                    break;

                case "status":
                    var status = pteroMessage.Args?.FirstOrDefault() ?? "unknown";
                    _logger.LogInformation("[{ServerName}] Server status update: {Status}", _serverConfig.Name, status);
                    if (status == "starting" && _isUpdateInProgress)
                    {
                        _logger.LogWarning("[{ServerName}] Server is 'starting'. Initiating post-update sequence (kill -> reinstall -> start).", _serverConfig.Name);
                        _ = Task.Run(() => PerformPostUpdateSequence(stoppingToken), stoppingToken);
                    }
                    break;

                case "token expiring":
                    _logger.LogWarning("[{ServerName}] WebSocket token expiring. Requesting new token.", _serverConfig.Name);
                    _ = Task.Run(async () =>
                    {
                        var newWsInfo = await _pteroApiService.GetWebSocketInfoAsync(_serverConfig, stoppingToken);
                        if (newWsInfo != null)
                        {
                            _currentWsToken = newWsInfo.Token;
                            SendWebSocketAuth();
                        }
                        else
                        {
                            _logger.LogError("[{ServerName}] Failed to get new WebSocket token after expiry warning.", _serverConfig.Name);
                            _updateCompletionSource?.TrySetResult(false);
                            await CleanupWebSocketAsync();
                        }
                    }, stoppingToken);
                    break;

                case "token expired":
                    _logger.LogError("[{ServerName}] WebSocket token expired. Closing connection.", _serverConfig.Name);
                    _updateCompletionSource?.TrySetResult(false);
                    await CleanupWebSocketAsync();
                    break;

                case "error":
                case "daemon error":
                    _logger.LogError("[{ServerName}] Received error via WebSocket: {Args}", _serverConfig.Name, string.Join(", ", pteroMessage.Args ?? new List<string>()));
                    break;

                default:
                    break;
            }
        }
        catch (JsonException jsonEx)
        {
            _logger.LogWarning(jsonEx, "[{ServerName}] Failed to deserialize WebSocket message: {Message}", _serverConfig.Name, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error handling WebSocket message.", _serverConfig.Name);
        }
    }

    private async Task PerformPostUpdateSequence(CancellationToken stoppingToken)
    {
        bool success = false;
        try
        {
            _logger.LogInformation("[{ServerName}] Starting post-update sequence...", _serverConfig.Name);

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            _logger.LogInformation("[{ServerName}] Sending KILL signal...", _serverConfig.Name);
            if (!await _pteroApiService.SendPowerSignalAsync(_serverConfig, "kill", stoppingToken))
            {
                _logger.LogError("[{ServerName}] Failed to send KILL signal. Aborting sequence.", _serverConfig.Name);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            _logger.LogInformation("[{ServerName}] Sending REINSTALL command...", _serverConfig.Name);
            if (!await _pteroApiService.SendReinstallCommandAsync(_serverConfig, stoppingToken))
            {
                _logger.LogError("[{ServerName}] Failed to send REINSTALL command. Aborting sequence.", _serverConfig.Name);
                return;
            }
            _logger.LogInformation("[{ServerName}] Waiting for reinstall process to complete (15 seconds)...", _serverConfig.Name);
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            _logger.LogInformation("[{ServerName}] Sending START signal...", _serverConfig.Name);
            if (!await _pteroApiService.SendPowerSignalAsync(_serverConfig, "start", stoppingToken))
            {
                _logger.LogError("[{ServerName}] Failed to send START signal.", _serverConfig.Name);
            }
            else
            {
                _logger.LogInformation("[{ServerName}] Server start command sent.", _serverConfig.Name);
            }

            await _discordService.SendUpdateNotificationAsync(_serverConfig, stoppingToken);

            success = true;
            _logger.LogInformation("[{ServerName}] Post-update sequence completed.", _serverConfig.Name);

        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[{ServerName}] Post-update sequence cancelled.", _serverConfig.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error during post-update sequence.", _serverConfig.Name);
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
                _logger.LogInformation("[{ServerName}] Closing WebSocket connection.", _serverConfig.Name);
                await _wsClient.Stop(WebSocketCloseStatus.NormalClosure, "Worker stopping");
            }
            _wsClient.Dispose();
            _wsClient = null;
            _currentWsToken = null;
            _logger.LogDebug("[{ServerName}] WebSocket client disposed.", _serverConfig.Name);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping worker for server: {ServerName}", _serverConfig.Name);
        await CleanupWebSocketAsync();
        await base.StopAsync(cancellationToken);
    }
}