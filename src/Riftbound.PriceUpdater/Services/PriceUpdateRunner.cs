using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Riftbound.PriceUpdater.Models;
using Riftbound.PriceUpdater.Options;

namespace Riftbound.PriceUpdater.Services;

public sealed class PriceUpdateRunner
{
    private readonly GoogleSheetsInventoryService _inventoryService;
    private readonly TcgPlayerPriceScraper _scraper;
    private readonly InventoryPricingOptions _pricingOptions;
    private readonly ScrapingOptions _scrapingOptions;
    private readonly ILogger<PriceUpdateRunner> _logger;

    public PriceUpdateRunner(
        GoogleSheetsInventoryService inventoryService,
        TcgPlayerPriceScraper scraper,
        IOptions<InventoryPricingOptions> pricingOptions,
        IOptions<ScrapingOptions> scrapingOptions,
        ILogger<PriceUpdateRunner> logger)
    {
        _inventoryService = inventoryService;
        _scraper = scraper;
        _pricingOptions = pricingOptions.Value;
        _scrapingOptions = scrapingOptions.Value;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = await _inventoryService.ReadInventoryRowsAsync(cancellationToken);

        var rowsWithLinks = rows.Where(row => !string.IsNullOrWhiteSpace(row.TcgPlayerUrl)).ToList();
        var eligibleRows = rowsWithLinks
            .Where(row => _pricingOptions.UpdateAllProducts || row.ProductQuantity.GetValueOrDefault() > 0)
            .ToList();
        var skipped = rows.Count - eligibleRows.Count;
        var results = new List<PriceUpdateResult>(eligibleRows.Count);

        _logger.LogInformation("Total rows scanned: {TotalRows}", rows.Count);
        _logger.LogInformation("Rows eligible for update: {EligibleRows}", eligibleRows.Count);
        _logger.LogInformation("Rows skipped: {SkippedRows}", skipped);

        foreach (var row in eligibleRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var productId = GoogleSheetsInventoryService.ExtractTcgPlayerProductId(row.TcgPlayerUrl);
            var oldPrice = GoogleSheetsInventoryService.ExtractPrice(row.ExistingMarketPriceFormulaOrValue);

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["RowNumber"] = row.RowNumber,
                ["ProductName"] = row.ProductName,
                ["ProductId"] = productId
            });

            _logger.LogInformation(
                "Scraping row {RowNumber}: {ProductName} (ProductId: {ProductId}, OldPrice: {OldPrice})",
                row.RowNumber,
                row.ProductName,
                productId,
                oldPrice);

            var scrapeResult = await _scraper.ScrapeMarketPriceAsync(row.TcgPlayerUrl!, cancellationToken);
            if (!scrapeResult.Success || !scrapeResult.MarketPrice.HasValue)
            {
                results.Add(new PriceUpdateResult(
                    row.RowNumber,
                    row.ProductName,
                    false,
                    oldPrice,
                    null,
                    scrapeResult.ErrorMessage));

                _logger.LogWarning(
                    "Failed row {RowNumber}: {ProductName} (ProductId: {ProductId}). Error: {ErrorMessage}",
                    row.RowNumber,
                    row.ProductName,
                    productId,
                    scrapeResult.ErrorMessage);

                await DelayBetweenRowsAsync(cancellationToken);
                continue;
            }

            try
            {
                await _inventoryService.UpdateMarketPriceAsync(
                    row,
                    scrapeResult.MarketPrice.Value,
                    DateTimeOffset.Now,
                    cancellationToken);

                results.Add(new PriceUpdateResult(
                    row.RowNumber,
                    row.ProductName,
                    true,
                    oldPrice,
                    scrapeResult.MarketPrice.Value,
                    null));

                _logger.LogInformation(
                    "Updated row {RowNumber}: {ProductName} (ProductId: {ProductId}, OldPrice: {OldPrice}, NewPrice: {NewPrice})",
                    row.RowNumber,
                    row.ProductName,
                    productId,
                    oldPrice,
                    scrapeResult.MarketPrice.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new PriceUpdateResult(
                    row.RowNumber,
                    row.ProductName,
                    false,
                    oldPrice,
                    scrapeResult.MarketPrice.Value,
                    ex.Message));

                _logger.LogError(
                    ex,
                    "Failed to update row {RowNumber}: {ProductName} (ProductId: {ProductId}, OldPrice: {OldPrice}, NewPrice: {NewPrice})",
                    row.RowNumber,
                    row.ProductName,
                    productId,
                    oldPrice,
                    scrapeResult.MarketPrice.Value);
            }

            await DelayBetweenRowsAsync(cancellationToken);
        }

        stopwatch.Stop();

        var updated = results.Count(result => result.Updated);
        var failed = results.Count(result => !result.Updated);
        _logger.LogInformation("Rows updated: {RowsUpdated}", updated);
        _logger.LogInformation("Rows failed: {RowsFailed}", failed);
        _logger.LogInformation("Total execution time: {Elapsed}", stopwatch.Elapsed);

        return failed == 0 ? 0 : 1;
    }

    private async Task DelayBetweenRowsAsync(CancellationToken cancellationToken)
    {
        if (_scrapingOptions.DelayBetweenRequestsMs > 0)
        {
            await Task.Delay(_scrapingOptions.DelayBetweenRequestsMs, cancellationToken);
        }
    }
}
