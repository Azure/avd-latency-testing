using Serilog;

namespace run_test;

public static class Program
{
    private static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddHttpClient();
                services.AddSingleton<TestingService>();
                services.AddHostedService<Worker>();
            })
            .ConfigureLogging(builder =>
            {
                builder.ClearProviders();

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
}