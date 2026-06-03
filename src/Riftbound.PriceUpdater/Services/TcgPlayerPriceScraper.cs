using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Riftbound.PriceUpdater.Models;
using Riftbound.PriceUpdater.Options;

namespace Riftbound.PriceUpdater.Services;

public sealed class TcgPlayerPriceScraper : IAsyncDisposable
{
    private static readonly Regex CurrencyRegex = new(
        "\\$\\s*(?<amount>\\d{1,3}(?:,\\d{3})*(?:\\.\\d{2})?|\\d+(?:\\.\\d{2})?)",
        RegexOptions.Compiled);

    private static readonly Regex MarketPriceNearbyRegex = new(
        "Market\\s+Price(?<nearby>.{0,300})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ScrapingOptions _options;
    private readonly ILogger<TcgPlayerPriceScraper> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public TcgPlayerPriceScraper(
        IOptions<ScrapingOptions> options,
        ILogger<TcgPlayerPriceScraper> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TcgPriceResult> ScrapeMarketPriceAsync(string url, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.MaxRetries + 1);
        Exception? lastException = null;
        string finalUrl = url;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IPage? page = null;
            try
            {
                var browser = await GetBrowserAsync();
                page = await browser.NewPageAsync();
                page.SetDefaultNavigationTimeout(_options.NavigationTimeoutMs);
                page.SetDefaultTimeout(_options.NavigationTimeoutMs);

                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = _options.NavigationTimeoutMs
                });

                finalUrl = page.Url;
                var marketPrice = await TryExtractMarketPriceAsync(page, cancellationToken);
                if (marketPrice.HasValue)
                {
                    return new TcgPriceResult(true, marketPrice.Value, null, finalUrl);
                }

                lastException = new InvalidOperationException("Market Price could not be found on the page.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Attempt {Attempt}/{Attempts} failed while scraping {Url}",
                    attempt,
                    attempts,
                    url);
            }
            finally
            {
                if (page is not null)
                {
                    await page.CloseAsync();
                }
            }

            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }
        }

        return new TcgPriceResult(
            false,
            null,
            lastException?.Message ?? "Unknown scraping failure.",
            finalUrl);
    }

    private async Task<decimal?> TryExtractMarketPriceAsync(IPage page, CancellationToken cancellationToken)
    {
        var strategies = new Func<IPage, CancellationToken, Task<decimal?>>[]
        {
            ExtractFromMarketPriceTextNodeAsync,
            ExtractFromLabelBasedLocatorAsync,
            ExtractFromPageTextAsync
        };

        foreach (var strategy in strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var price = await strategy(page, cancellationToken);
            if (price.HasValue)
            {
                return price.Value;
            }
        }

        return null;
    }

    private static async Task<decimal?> ExtractFromMarketPriceTextNodeAsync(IPage page, CancellationToken cancellationToken)
    {
        var marketPriceLabels = await page.GetByText("Market Price", new PageGetByTextOptions { Exact = false })
            .AllAsync();

        foreach (var label in marketPriceLabels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await label.IsVisibleAsync())
            {
                continue;
            }

            var surroundingText = await label.EvaluateAsync<string>(
                @"element => {
                    const candidates = [
                        element.parentElement,
                        element.parentElement?.parentElement,
                        element.closest('[class]'),
                        element.closest('section'),
                        element.closest('div')
                    ].filter(Boolean);
                    const text = candidates.map(x => x.innerText || x.textContent || '').find(x => /\$\s*\d/.test(x));
                    return text || element.innerText || element.textContent || '';
                }");

            var parsed = ParseMarketPriceFromText(surroundingText);
            if (parsed.HasValue)
            {
                return parsed.Value;
            }
        }

        return null;
    }

    private static async Task<decimal?> ExtractFromLabelBasedLocatorAsync(IPage page, CancellationToken cancellationToken)
    {
        var locators = new[]
        {
            page.Locator("text=/Market\\s+Price/i").Locator("xpath=.."),
            page.Locator("text=/Market\\s+Price/i").Locator("xpath=../.."),
            page.Locator("[aria-label*='Market Price' i]"),
            page.Locator("[data-testid*='market' i], [class*='market' i]")
        };

        foreach (var locator in locators)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = await locator.CountAsync();
            for (var i = 0; i < Math.Min(count, 10); i++)
            {
                var item = locator.Nth(i);
                if (!await item.IsVisibleAsync())
                {
                    continue;
                }

                var text = await item.InnerTextAsync();
                var parsed = ParseMarketPriceFromText(text);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }
        }

        return null;
    }

    private static async Task<decimal?> ExtractFromPageTextAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions
        {
            Timeout = 5000
        });

        return ParseMarketPriceFromText(bodyText);
    }

    private static decimal? ParseMarketPriceFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match marketPriceMatch in MarketPriceNearbyRegex.Matches(text))
        {
            var nearby = marketPriceMatch.Groups["nearby"].Value;
            var currency = CurrencyRegex.Match(nearby);
            if (currency.Success && TryParseCurrency(currency.Groups["amount"].Value, out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null)
        {
            return _browser;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless
        });

        return _browser;
    }

    private static bool TryParseCurrency(string value, out decimal amount)
    {
        var normalized = value.Replace(",", "", StringComparison.Ordinal);
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
