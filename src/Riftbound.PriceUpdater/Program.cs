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
