using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Win32;
using Nito.AsyncEx;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;

namespace run_test;

internal sealed class TestingService : BackgroundService, IAsyncDisposable
{
    private readonly IOperationHolder<RequestTelemetry> telemetryOperation;
    private readonly ILogger logger;
    private readonly TimeSpan delayPerRun;
    private readonly TelemetryClient telemetryClient;
    private readonly FileInfo csvFile;
    private readonly AsyncLazy<(EdgeDriverService, Action)> lazyEdgeDriverService;

    public TestingService(ILogger<TestingService> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory, TelemetryClient telemetryClient)
    {
        this.telemetryOperation = telemetryClient.StartOperation<RequestTelemetry>("AVD test");
        this.logger = logger;
        this.delayPerRun = GetDelayPerRun(configuration);
        this.telemetryClient = telemetryClient;
        this.csvFile = GetCsvFile(configuration);
        this.lazyEdgeDriverService = GetLazyEdgeDriverService(logger, httpClientFactory, configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Beginning execution...");

                await Run(cancellationToken);

                logger.LogInformation("Finished execution, sleeping until {nextRunTime}...", DateTimeOffset.Now.Add(delayPerRun));
                await Task.Delay(delayPerRun, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Don't throw if operation was canceled
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "");
            throw;
        }
        finally
        {
            var (_, cleanupAction) = await lazyEdgeDriverService;
            cleanupAction();

            telemetryClient.StopOperation(telemetryOperation);
            await telemetryClient.FlushAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private static TimeSpan GetDelayPerRun(IConfiguration configuration)
    {
        var defaultDelay = TimeSpan.FromMinutes(5);
        var section = configuration.GetSection("DELAY_PER_RUN_IN_SECONDS");

        return section.Exists()
            ? int.TryParse(section.Value, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : defaultDelay
            : defaultDelay;
    }

    private static FileInfo GetCsvFile(IConfiguration configuration)
    {
        var section = configuration.GetSection("CSV_OUTPUT_FILE_PATH");
        var path = section.Exists() ? section.Value : Path.Combine(AppContext.BaseDirectory, "output.csv");
        return new FileInfo(path);
    }

    private static AsyncLazy<(EdgeDriverService, Action)> GetLazyEdgeDriverService(ILogger logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        return new AsyncLazy<(EdgeDriverService, Action)>(() => GetLazyEdgeDriverService(logger, httpClientFactory, configuration, CancellationToken.None));
    }

    private static async Task<(EdgeDriverService, Action)> GetLazyEdgeDriverService(ILogger logger, IHttpClientFactory httpClientFactory, IConfiguration configuration, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating Edge driver service...");

        var edgeDriverFolder = GetEdgeDriverFolder(configuration);
        var edgeDriverDownloadUri = GetEdgeDriverDownloadUri(logger, configuration);

        var downloadFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await DownloadEdgeDriver(logger, httpClientFactory, downloadFilePath, edgeDriverDownloadUri, cancellationToken);

        logger.LogInformation("Extracting zip file {downloadFilePath} to directory {edgeDriverFolderPath}...", downloadFilePath, edgeDriverFolder.FullName);
        ZipFile.ExtractToDirectory(downloadFilePath, edgeDriverFolder.FullName);

        logger.LogInformation("Deleting temporary zip file {downloadFilePath}...", downloadFilePath);
        File.Delete(downloadFilePath);

        logger.LogInformation("Creating Edge driver...");

        var cleanupAction = () =>
        {
            logger.LogInformation("Deleting Edge driver directory {edgeDriverFolderPath}...", edgeDriverFolder.FullName);
            edgeDriverFolder.Delete(recursive: true);
        };

        var edgeDriverService = EdgeDriverService.CreateDefaultService(edgeDriverFolder.FullName);
        return (edgeDriverService, cleanupAction);
    }

    private static DirectoryInfo GetEdgeDriverFolder(IConfiguration configuration)
    {
        var section = configuration.GetSection("EDGE_DRIVER_DOWNLOAD_PATH");
        var path = section.Exists() ? section.Value : Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        return new DirectoryInfo(path);
    }

    private static Uri GetEdgeDriverDownloadUri(ILogger logger, IConfiguration configuration)
    {
        var section = configuration.GetSection("EDGE_DRIVER_DOWNLOAD_URI");

        if (section.Exists())
        {
            return
                Uri.TryCreate(section.Value, UriKind.Absolute, out var uri)
                ? uri
                : throw new InvalidOperationException($"'{section.Value}' is not a valid URL.");
        }
        else
        {
            logger.LogInformation("Getting Edge version...");
            var edgeVersion = GetEdgeVersion();
            logger.LogInformation("Found Edge version {edgeVersion}...", edgeVersion);

            return new Uri($"https://msedgedriver.azureedge.net/{edgeVersion}/edgedriver_win{(Environment.Is64BitOperatingSystem ? "64" : "32")}.zip");
        }
    }

    private static async Task DownloadEdgeDriver(ILogger logger, IHttpClientFactory httpClientFactory, string downloadFilePath, Uri edgeDriverDownloadUri, CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading Edge driver from {edgeDriverDownloadUri} to {downloadFilePath}...", edgeDriverDownloadUri, downloadFilePath);

        using var client = httpClientFactory.CreateClient();
        var response = await client.GetAsync(edgeDriverDownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var memoryStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = File.Create(downloadFilePath);
        memoryStream.Seek(0, SeekOrigin.Begin);
        await memoryStream.CopyToAsync(fileStream, cancellationToken);
    }

    private static string GetEdgeVersion()
    {
        return OperatingSystem.IsWindows()
            ? Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Edge\BLBeacon")
                                  ?.GetValue("version")
                                  ?.ToString()
               ?? throw new InvalidOperationException("Could not find Edge version from registry.")
            : throw new InvalidOperationException($"Getting Edge is only supported on Windows. Current operating system is {Environment.OSVersion.VersionString}.");
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        (var driverService, _) = await lazyEdgeDriverService;

        var regionLatencies = GetRegionLatencies(driverService, cancellationToken);
        foreach (var regionLatency in regionLatencies)
        {
            logger.LogInformation("Region: {regionName}, Latency: {latency}", regionLatency.Key, regionLatency.Value);
        }

        logger.LogInformation("Writing records to CSV file {csvFilePath}...", csvFile.FullName);
        await WriteRecordToFile(DateTimeOffset.UtcNow, regionLatencies);
    }

    private ImmutableDictionary<RegionName, Latency> GetRegionLatencies(EdgeDriverService driverService, CancellationToken cancellationToken)
    {
        var options = new EdgeOptions();
        options.AddArgument("headless");
        using var driver = new EdgeDriver(driverService, options);

        try
        {
            logger.LogInformation("Setting up timeout; will navigate to site and wait for 10 seconds before extracting data...");
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);

            logger.LogInformation("Navigating to site...");
            driver.Navigate().GoToUrl("https://azure.microsoft.com/en-us/services/virtual-desktop/assessment/#estimation-tool");

            logger.LogInformation("Getting regions from site...");
            return GetRegionLatencies(driver);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "");
            return ImmutableDictionary<RegionName, Latency>.Empty;
        }
        finally
        {
            driver.Quit();
        }
    }

    private static ImmutableDictionary<RegionName, Latency> GetRegionLatencies(IWebDriver driver) =>
        GetRegionLatencyTableRows(driver).Select(GetRowColumns)
                                         .Choose(GetRegionLatencyFromRowColumns)
                                         .ToImmutableDictionary(x => x.Item1, x => x.Item2);

    private static IEnumerable<IWebElement> GetRegionLatencyTableRows(IWebDriver driver) =>
        driver.FindElement(By.Id("azure-regions"))
              .FindElements(By.TagName("tr"));

    private static IEnumerable<string> GetRowColumns(IWebElement element) =>
        element.FindElements(By.TagName("td"))
               .Select(x => x.Text);

    private static (RegionName, Latency)? GetRegionLatencyFromRowColumns(IEnumerable<string> rowColumns)
    {
        var array = rowColumns.ToImmutableArray();

        var regionName = RegionName.TryFrom(array[0]);
        var latency = Latency.TryFrom(array[1]);

        return regionName is not null && latency is not null ? (regionName, latency) : null;
    }

    private async Task WriteRecordToFile(DateTimeOffset timestamp, IDictionary<RegionName, Latency> regionLatencies)
    {
        if (regionLatencies.Any() is false)
        {
            logger.LogInformation("No region latencies were found, skipping writing to CSV.");
            return;
        }

        if ((await CsvFileAlreadyHasEntries()) is false)
        {
            logger.LogInformation("CSV file {csvFilePath} does not have any entries, creating header row...", csvFile.FullName);
            await WriteCsvHeaderRecord();
        }

        using var streamWriter = new StreamWriter(csvFile.FullName, append: true);
        using var csvWriter = new CsvWriter(streamWriter, CultureInfo.InvariantCulture);

        foreach (var kvp in regionLatencies)
        {
            csvWriter.WriteField(timestamp);
            csvWriter.WriteField(kvp.Key);
            csvWriter.WriteField(kvp.Value.ToInt());

            await csvWriter.NextRecordAsync();
        }

        logger.LogInformation("Finished writing records to CSV file {csvFilePath}.", csvFile.FullName);
    }

    private async Task<bool> CsvFileAlreadyHasEntries()
    {
        if (File.Exists(csvFile.FullName))
        {
            using var reader = new StreamReader(csvFile.FullName);
            using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
            await csvReader.ReadAsync();
            csvReader.ReadHeader();

            return csvReader.HeaderRecord.Any();
        }
        else
        {
            return false;
        }
    }

    private async Task WriteCsvHeaderRecord()
    {
        using var streamWriter = new StreamWriter(csvFile.FullName, append: false);
        using var csvWriter = new CsvWriter(streamWriter, CultureInfo.InvariantCulture);

        csvWriter.WriteField("Timestamp");
        csvWriter.WriteField("Region");
        csvWriter.WriteField("Latency (ms)");

        await csvWriter.NextRecordAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (lazyEdgeDriverService.IsStarted)
        {
            var (service, _) = await lazyEdgeDriverService;
            service.Dispose();
        }

        telemetryOperation.Dispose();

        GC.SuppressFinalize(this);
    }
}
