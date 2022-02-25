using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.ApplicationInsights;
using Serilog;

namespace run_test;

public static class Program
{
    private static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
                services.AddApplicationInsightsTelemetryWorkerService(options => options.EnableAdaptiveSampling = false);
                services.AddApplicationInsightsTelemetryProcessor<LocalhostDependencyFilter>();
                services.AddHttpClient();
                services.AddHostedService<TestingService>();
            })
            .ConfigureLogging(builder =>
            {
                builder.ClearProviders();

                builder.AddApplicationInsights();
                builder.AddFilter<ApplicationInsightsLoggerProvider>("", LogLevel.Information);
                builder.AddDebug();

                var logger = new LoggerConfiguration().MinimumLevel.Debug()
                                                      .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                                                      .WriteTo.File(GetLogFile().FullName, rollingInterval: RollingInterval.Infinite)
                                                      .CreateLogger();
                builder.AddSerilog(logger);
            })
            .Build();

        await host.RunAsync();
    }

    private static FileInfo GetLogFile()
    {
        var logFilePath = Path.Combine(AppContext.BaseDirectory, "log.txt");

        return new FileInfo(logFilePath);
    }

    private class LocalhostDependencyFilter : ITelemetryProcessor
    {
        private readonly ITelemetryProcessor next;

        public LocalhostDependencyFilter(ITelemetryProcessor next)
        {
            this.next = next;
        }

        public void Process(ITelemetry item)
        {
            if (item is DependencyTelemetry dependencyTelemetry
                && dependencyTelemetry.Type.Equals("Http", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(dependencyTelemetry.Data, UriKind.Absolute, out var targetUri)
                && targetUri.IsLoopback)
            {
                return;
            }

            next.Process(item);
        }
    }
}