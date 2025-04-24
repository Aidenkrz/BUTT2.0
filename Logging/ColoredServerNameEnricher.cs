using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PteroUpdateMonitor.Logging;

public class ColoredServerNameEnricher : ILogEventEnricher
{
    private static readonly Dictionary<ConsoleColor, string> AnsiColorMap = new()
    {
        { ConsoleColor.Black, "\u001b[30m" },
        { ConsoleColor.DarkBlue, "\u001b[34m" },
        { ConsoleColor.DarkGreen, "\u001b[32m" },
        { ConsoleColor.DarkCyan, "\u001b[36m" },
        { ConsoleColor.DarkRed, "\u001b[31m" },
        { ConsoleColor.DarkMagenta, "\u001b[35m" },
        { ConsoleColor.DarkYellow, "\u001b[33m" },
        { ConsoleColor.Gray, "\u001b[37m" },
        { ConsoleColor.DarkGray, "\u001b[90m" },
        { ConsoleColor.Blue, "\u001b[94m" },
        { ConsoleColor.Green, "\u001b[92m" },
        { ConsoleColor.Cyan, "\u001b[96m" },
        { ConsoleColor.Red, "\u001b[91m" },
        { ConsoleColor.Magenta, "\u001b[95m" },
        { ConsoleColor.Yellow, "\u001b[93m" },
        { ConsoleColor.White, "\u001b[97m" }
    };
    private const string AnsiReset = "\u001b[0m";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("ServerName", out var serverNameValue) &&
            serverNameValue is ScalarValue serverNameScalar &&
            serverNameScalar.Value is string serverName &&
            logEvent.Properties.TryGetValue("LogColor", out var logColorValue) &&
            logColorValue is ScalarValue logColorScalar &&
            logColorScalar.Value is string logColorName)
        {
            string coloredName = $"[{serverName}]";

            if (Enum.TryParse<ConsoleColor>(logColorName, true, out var consoleColor) &&
                AnsiColorMap.TryGetValue(consoleColor, out var ansiCode))
            {
                coloredName = $"{ansiCode}[{serverName}]{AnsiReset}";
            }

            var coloredProperty = propertyFactory.CreateProperty("ColoredServerName", coloredName);
            logEvent.AddOrUpdateProperty(coloredProperty);
        }
        else
        {
             if (!logEvent.Properties.ContainsKey("ColoredServerName") && logEvent.Properties.TryGetValue("ServerName", out serverNameValue) && serverNameValue is ScalarValue sv && sv.Value is string sn)
             {
                  var defaultProperty = propertyFactory.CreateProperty("ColoredServerName", $"[{sn}]");
                  logEvent.AddOrUpdateProperty(defaultProperty);
             }
             else if (!logEvent.Properties.ContainsKey("ColoredServerName"))
             {
                 var fallbackProperty = propertyFactory.CreateProperty("ColoredServerName", "[NoServer]");
                 logEvent.AddOrUpdateProperty(fallbackProperty);
             }
        }
    }
}

public static class ColoredServerNameLoggerConfigurationExtensions
{
    public static LoggerConfiguration WithColoredServerName(this Serilog.Configuration.LoggerEnrichmentConfiguration enrichmentConfiguration)
    {
        ArgumentNullException.ThrowIfNull(enrichmentConfiguration);
        return enrichmentConfiguration.With<ColoredServerNameEnricher>();
    }
}