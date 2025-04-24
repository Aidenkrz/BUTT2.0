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
            _logger.LogDebug("Discord webhook URL is not configured. Skipping notification.");
            return;
        }


        if (!Uri.TryCreate(serverConfig.DiscordWebhookUrl, UriKind.Absolute, out var webhookUri) ||
            !(webhookUri.Scheme == Uri.UriSchemeHttp || webhookUri.Scheme == Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Invalid Discord webhook URL format: {WebhookUrl}", serverConfig.DiscordWebhookUrl);
            return;
        }


        var client = _httpClientFactory.CreateClient("DiscordWebhook");
        var message = serverConfig.DiscordUpdateMessage.Replace("{ServerName}", serverConfig.Name);
        var payload = new DiscordWebhookPayload { Content = message };

        _logger.LogInformation("Sending Discord notification to webhook.");

        try
        {
            var response = await client.PostAsJsonAsync(serverConfig.DiscordWebhookUrl, payload, cancellationToken);


            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord notification sent successfully.");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to send Discord notification. Status: {StatusCode}. Response: {ErrorContent}", response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Discord notification.");
        }
    }
}