namespace run_test;

internal class Worker : BackgroundService
{
    private readonly ILogger logger;
    private readonly TestingService testingService;

    public Worker(ILogger<Worker> logger, TestingService testingService)
    {
        this.logger = logger;
        this.testingService = testingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Registering cancellation task...", "");
        stoppingToken.Register(OnCancellation);

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Beginning execution...");
            await Task.Delay(1000, stoppingToken);

            await testingService.Run(stoppingToken);

            var delayDuration = TimeSpan.FromMinutes(5); ;
            logger.LogInformation("Finished execution, sleeping until {nextRunTime}...", DateTimeOffset.Now.Add(delayDuration));
            await Task.Delay(delayDuration, stoppingToken);
        }
    }

    private void OnCancellation()
    {
        logger.LogInformation("Cancellation requested, stopping...");
    }
}
