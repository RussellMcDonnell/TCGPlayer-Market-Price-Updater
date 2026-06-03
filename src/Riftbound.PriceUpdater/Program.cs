using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Riftbound.PriceUpdater.Models;
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
        if (args.Any(arg => string.Equals(arg, "--inspect-sheet", StringComparison.OrdinalIgnoreCase)))
        {
            var inventoryService = host.Services.GetRequiredService<GoogleSheetsInventoryService>();
            var pricingOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<InventoryPricingOptions>>().Value;
            var rows = await inventoryService.ReadInventoryRowsAsync(CancellationToken.None);
            var rowsWithLinks = rows.Where(row => !string.IsNullOrWhiteSpace(row.TcgPlayerUrl)).ToList();
            var rowsWithPositiveQuantity = rows.Where(row => row.ProductQuantity.GetValueOrDefault() > 0).ToList();
            var eligibleRows = rowsWithLinks
                .Where(row => pricingOptions.UpdateAllProducts || row.ProductQuantity.GetValueOrDefault() > 0)
                .ToList();

            Log.Information("Inspect Sheet: total rows read: {TotalRows}", rows.Count);
            Log.Information("Inspect Sheet: rows with TCGPlayer links in market price column: {RowsWithLinks}", rowsWithLinks.Count);
            Log.Information("Inspect Sheet: rows with Product Qty > 0: {RowsWithPositiveQuantity}", rowsWithPositiveQuantity.Count);
            Log.Information("Inspect Sheet: rows eligible under current config: {EligibleRows}", eligibleRows.Count);
            Log.Information("Inspect Sheet: UpdateAllProducts: {UpdateAllProducts}", pricingOptions.UpdateAllProducts);

            foreach (var row in rows.Take(40))
            {
                var productId = GoogleSheetsInventoryService.ExtractTcgPlayerProductId(row.TcgPlayerUrl);
                var skipReason = GetSkipReason(row, pricingOptions);
                Log.Information(
                    "Inspect Row {RowNumber}: Qty={Quantity}, HasLink={HasLink}, ProductId={ProductId}, SkipReason={SkipReason}, Product='{ProductName}', MarketCell='{MarketCell}'",
                    row.RowNumber,
                    row.ProductQuantity,
                    !string.IsNullOrWhiteSpace(row.TcgPlayerUrl),
                    productId,
                    skipReason,
                    row.ProductName,
                    Truncate(row.ExistingMarketPriceFormulaOrValue, 140));
            }

            Environment.ExitCode = 0;
            return;
        }

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

static string GetSkipReason(InventoryProductRow row, InventoryPricingOptions options)
{
    if (string.IsNullOrWhiteSpace(row.TcgPlayerUrl))
    {
        return "No TCGPlayer link found in market price column";
    }

    if (!options.UpdateAllProducts && row.ProductQuantity.GetValueOrDefault() <= 0)
    {
        return "Product Qty is not greater than 0";
    }

    return "Eligible";
}

static string? Truncate(string? value, int maxLength)
{
    if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
    {
        return value;
    }

    return value[..maxLength] + "...";
}
