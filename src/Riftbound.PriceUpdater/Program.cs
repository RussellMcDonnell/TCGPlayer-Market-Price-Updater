using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Riftbound.PriceUpdater.Options;
using Riftbound.PriceUpdater.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables();

    builder.Services
        .Configure<GoogleSheetsOptions>(builder.Configuration.GetSection(GoogleSheetsOptions.SectionName))
        .Configure<InventoryPricingOptions>(builder.Configuration.GetSection(InventoryPricingOptions.SectionName))
        .Configure<ScrapingOptions>(builder.Configuration.GetSection(ScrapingOptions.SectionName))
        .AddSingleton<GoogleSheetsInventoryService>()
        .AddSingleton<TcgPlayerPriceScraper>()
        .AddSingleton<PriceUpdateRunner>();

    builder.Services.AddSerilog((services, loggerConfiguration) =>
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());

    using var host = builder.Build();
    var scraper = host.Services.GetRequiredService<TcgPlayerPriceScraper>();
    try
    {
        var scrapeUrls = GetArgumentValues(args, "--scrape-url");
        if (scrapeUrls.Count > 0)
        {
            Environment.ExitCode = 0;
            foreach (var scrapeUrl in scrapeUrls)
            {
                var result = await scraper.ScrapeMarketPriceAsync(scrapeUrl, CancellationToken.None);
                if (result.Success)
                {
                    Log.Information(
                        "Scraped TCGPlayer Market Price: ${MarketPrice:0.00} from {FinalUrl}",
                        result.MarketPrice,
                        result.FinalUrl);
                }
                else
                {
                    Log.Error(
                        "Failed to scrape TCGPlayer Market Price from {FinalUrl}. Error: {ErrorMessage}",
                        result.FinalUrl,
                        result.ErrorMessage);
                    Environment.ExitCode = 1;
                }
            }

            return;
        }

        var runner = host.Services.GetRequiredService<PriceUpdateRunner>();
        Environment.ExitCode = await runner.RunAsync(CancellationToken.None);
    }
    finally
    {
        await scraper.DisposeAsync();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Price update failed.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static IReadOnlyList<string> GetArgumentValues(string[] args, string name)
{
    var values = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            values.Add(args[i + 1]);
        }
    }

    return values;
}
