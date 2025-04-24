using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PteroUpdateMonitor.Models;

namespace PteroUpdateMonitor.Services;

public class DiscordNotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(IHttpClientFactory httpClientFactory, ILogger<DiscordNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendUpdateNotificationAsync(ServerConfig serverConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverConfig.DiscordWebhookUrl))
        {
            _logger.LogDebug("[{ServerName}] Discord webhook URL is not configured. Skipping notification.", serverConfig.Name);
            return;
        }


        if (!Uri.TryCreate(serverConfig.DiscordWebhookUrl, UriKind.Absolute, out var webhookUri) ||
            !(webhookUri.Scheme == Uri.UriSchemeHttp || webhookUri.Scheme == Uri.UriSchemeHttps))
        {
            _logger.LogWarning("[{ServerName}] Invalid Discord webhook URL format: {WebhookUrl}", serverConfig.Name, serverConfig.DiscordWebhookUrl);
            return;
        }


        var client = _httpClientFactory.CreateClient("DiscordWebhook");
        var message = serverConfig.DiscordUpdateMessage.Replace("{ServerName}", serverConfig.Name);
        var payload = new DiscordWebhookPayload { Content = message };

        _logger.LogInformation("[{ServerName}] Sending Discord notification to webhook.", serverConfig.Name);

        try
        {
            var response = await client.PostAsJsonAsync(serverConfig.DiscordWebhookUrl, payload, cancellationToken);


            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[{ServerName}] Discord notification sent successfully.", serverConfig.Name);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[{ServerName}] Failed to send Discord notification. Status: {StatusCode}. Response: {ErrorContent}",
                    serverConfig.Name, response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ServerName}] Error sending Discord notification.", serverConfig.Name);
        }
    }
}