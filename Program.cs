using PteroUpdateMonitor.Models;
using PteroUpdateMonitor.Services;
using PteroUpdateMonitor.Workers;
using System.Net.Http.Headers;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddEnvironmentVariables();
        config.AddCommandLine(args);
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConfiguration(context.Configuration.GetSection("Logging"));
        logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
            options.UseUtcTimestamp = false;
        });
    })
    .ConfigureServices((hostContext, services) =>
    {
        var appSettings = hostContext.Configuration.Get<AppSettings>() ?? new AppSettings();
        if (appSettings.Servers == null || !appSettings.Servers.Any())
        {
            var logger = services.BuildServiceProvider().GetRequiredService<ILogger<Program>>();
            logger.LogCritical("No servers configured in appsettings.json. Exiting.");

            var lifetime = services.BuildServiceProvider().GetRequiredService<IHostApplicationLifetime>();
            lifetime.StopApplication();
            return;
        }
        services.AddSingleton(appSettings);

        services.AddHttpClient("PterodactylApi", client =>
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddHttpClient("Ss14Api", client =>{});
        services.AddHttpClient("DiscordWebhook", client =>{});

        services.AddSingleton<PterodactylApiService>();
        services.AddSingleton<Ss14ApiService>();
        services.AddSingleton<DiscordNotificationService>();

        foreach (var serverConfig in appSettings.Servers)
        {
             if (string.IsNullOrWhiteSpace(serverConfig.Name) ||
                 string.IsNullOrWhiteSpace(serverConfig.ManifestUrl) ||
                 string.IsNullOrWhiteSpace(serverConfig.ServerIp) ||
                 string.IsNullOrWhiteSpace(serverConfig.PterodactylApiKey) ||
                 string.IsNullOrWhiteSpace(serverConfig.PterodactylApiUrl) ||
                 string.IsNullOrWhiteSpace(serverConfig.PterodactylServerId))
            {
                 var logger = services.BuildServiceProvider().GetRequiredService<ILogger<Program>>();
                 logger.LogWarning("Skipping registration for server '{ServerName}' due to missing essential configuration.", serverConfig.Name ?? "[Unnamed Server]");
                 continue;
            }

            services.AddSingleton<IHostedService>(provider => new ServerMonitorWorker(
                provider.GetRequiredService<ILogger<ServerMonitorWorker>>(),
                serverConfig,
                provider.GetRequiredService<Ss14ApiService>(),
                provider.GetRequiredService<PterodactylApiService>(),
                provider.GetRequiredService<DiscordNotificationService>(),
                provider.GetRequiredService<IHostApplicationLifetime>()
            ));
        }
    })
    .Build();

await host.RunAsync();