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
        "Market\\s+Price(?!\\s+History)(?<nearby>.{0,300})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private const string NoPricePointsSection = "__NO_PRICE_POINTS_SECTION__";
    private const string NoMarketPriceValue = "__NO_MARKET_PRICE_VALUE__";

    private const string PricePointsGeometryScript =
        """
        () => {
            const noPricePointsSection = '__NO_PRICE_POINTS_SECTION__';
            const noMarketPriceValue = '__NO_MARKET_PRICE_VALUE__';
            const currencyPattern = /\$\s*(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{2})?/;
            const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
            const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = window.getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const exactText = (element, text) => normalize(element.innerText || element.textContent) === text;
            const all = [...document.querySelectorAll('body *')].filter(visible);
            const pricePointsLabels = all
                .filter(element => exactText(element, 'Price Points'))
                .map(element => ({ element, rect: element.getBoundingClientRect() }))
                .sort((a, b) => a.rect.top - b.rect.top);

            if (pricePointsLabels.length === 0) {
                return noPricePointsSection;
            }

            for (const pricePoints of pricePointsLabels) {
                const marketLabels = all
                    .filter(element => exactText(element, 'Market Price'))
                    .map(element => ({ element, rect: element.getBoundingClientRect() }))
                    .filter(label =>
                        label.rect.top >= pricePoints.rect.top &&
                        label.rect.top <= pricePoints.rect.top + 320 &&
                        Math.abs(label.rect.left - pricePoints.rect.left) < 700)
                    .sort((a, b) => a.rect.top - b.rect.top);

                for (const label of marketLabels) {
                    const labelCenterY = label.rect.top + (label.rect.height / 2);
                    const sameRowCurrency = all
                        .filter(element => {
                            const text = normalize(element.innerText || element.textContent);
                            if (!currencyPattern.test(text)) {
                                return false;
                            }

                            const children = [...element.children];
                            if (children.some(child => currencyPattern.test(normalize(child.innerText || child.textContent)))) {
                                return false;
                            }

                            const rect = element.getBoundingClientRect();
                            const centerY = rect.top + (rect.height / 2);
                            return rect.left >= label.rect.right - 4 && Math.abs(centerY - labelCenterY) <= 28;
                        })
                        .map(element => {
                            const rect = element.getBoundingClientRect();
                            return {
                                text: normalize(element.innerText || element.textContent),
                                rect,
                                centerY: rect.top + (rect.height / 2)
                            };
                        })
                        .sort((a, b) =>
                            Math.abs(a.centerY - labelCenterY) - Math.abs(b.centerY - labelCenterY) ||
                            a.rect.left - b.rect.left);

                    if (sameRowCurrency.length > 0) {
                        return sameRowCurrency[0].text.match(currencyPattern)?.[0] || null;
                    }
                }
            }

            return noMarketPriceValue;
        }
        """;

    private static readonly string MarketPriceAvailableScript =
        "() => { " +
        $"const result = ({PricePointsGeometryScript})(); " +
        $"return result && result !== '{NoPricePointsSection}' && result !== '{NoMarketPriceValue}'; " +
        "}";

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
                page = await NewPageAsync();
                page.SetDefaultNavigationTimeout(_options.NavigationTimeoutMs);
                page.SetDefaultTimeout(_options.NavigationTimeoutMs);

                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _options.NavigationTimeoutMs
                });

                await WaitForProductPageContentAsync(page);

                finalUrl = page.Url;
                var marketPrice = await TryExtractMarketPriceAsync(page, cancellationToken);
                if (marketPrice.HasValue)
                {
                    return new TcgPriceResult(true, marketPrice.Value, null, finalUrl);
                }

                await LogExtractionFailureDetailsAsync(page);
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
                    var context = page.Context;
                    await page.CloseAsync();
                    await context.CloseAsync();
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
        var pricePointsResult = await ExtractFromPricePointsGeometryAsync(page, cancellationToken);
        if (pricePointsResult.SectionFound)
        {
            return pricePointsResult.MarketPrice;
        }

        var strategies = new Func<IPage, CancellationToken, Task<decimal?>>[]
        {
            ExtractFromPricePointsSectionAsync,
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

    private static async Task<PricePointsExtractionResult> ExtractFromPricePointsGeometryAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var amountText = await page.EvaluateAsync<string?>(PricePointsGeometryScript);
        if (string.Equals(amountText, NoPricePointsSection, StringComparison.Ordinal))
        {
            return new PricePointsExtractionResult(false, null);
        }

        if (string.Equals(amountText, NoMarketPriceValue, StringComparison.Ordinal))
        {
            return new PricePointsExtractionResult(true, null);
        }

        return TryParseCurrency(amountText, out var amount)
            ? new PricePointsExtractionResult(true, amount)
            : new PricePointsExtractionResult(true, null);
    }

    private static async Task<decimal?> ExtractFromMarketPriceTextNodeAsync(IPage page, CancellationToken cancellationToken)
    {
        var marketPriceLabels = await page.GetByText("Market Price", new PageGetByTextOptions { Exact = true })
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

    private static async Task<decimal?> ExtractFromPricePointsSectionAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pricePointsLabels = await page.GetByText("Price Points", new PageGetByTextOptions { Exact = true })
            .AllAsync();

        foreach (var label in pricePointsLabels)
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
                        element.parentElement?.parentElement?.parentElement,
                        element.closest('[class]'),
                        element.closest('section'),
                        element.closest('div')
                    ].filter(Boolean);
                    return candidates.map(x => x.innerText || x.textContent || '').find(x => /Market\s+Price/i.test(x)) || '';
                }");

            var parsed = ParseMarketPriceFromText(surroundingText);
            if (parsed.HasValue)
            {
                return parsed.Value;
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

        var lines = NormalizeTextLines(text);
        var fromPricePoints = ParsePricePointsLines(lines);
        if (fromPricePoints.HasValue)
        {
            return fromPricePoints.Value;
        }

        var fromExactMarketPriceLine = ParseExactMarketPriceLines(lines);
        if (fromExactMarketPriceLine.HasValue)
        {
            return fromExactMarketPriceLine.Value;
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

    private static decimal? ParsePricePointsLines(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i], "Price Points", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var searchLimit = Math.Min(lines.Count, i + 30);
            for (var j = i + 1; j < searchLimit; j++)
            {
                if (IsMarketPriceLine(lines[j]))
                {
                    return ParsePriceAfterLine(lines, j, searchLimit);
                }
            }
        }

        return null;
    }

    private static decimal? ParseExactMarketPriceLines(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsMarketPriceLine(lines[i]))
            {
                return ParsePriceAfterLine(lines, i, Math.Min(lines.Count, i + 10));
            }
        }

        return null;
    }

    private static decimal? ParsePriceAfterLine(IReadOnlyList<string> lines, int marketPriceLineIndex, int searchLimit)
    {
        var sameLineMatch = CurrencyRegex.Match(lines[marketPriceLineIndex]);
        if (sameLineMatch.Success && TryParseCurrency(sameLineMatch.Groups["amount"].Value, out var sameLineAmount))
        {
            return sameLineAmount;
        }

        for (var i = marketPriceLineIndex + 1; i < searchLimit; i++)
        {
            var currency = CurrencyRegex.Match(lines[i]);
            if (currency.Success && TryParseCurrency(currency.Groups["amount"].Value, out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private static bool IsMarketPriceLine(string line)
    {
        return Regex.IsMatch(line, "^Market\\s+Price(?:\\s+\\$\\s*\\d.*)?$", RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string> NormalizeTextLines(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private async Task WaitForProductPageContentAsync(IPage page)
    {
        try
        {
            await page.WaitForFunctionAsync(
                @"() => /Price Points/i.test(document.body?.innerText || '')",
                null,
                new PageWaitForFunctionOptions { Timeout = _options.NavigationTimeoutMs });

            await page.WaitForFunctionAsync(
                MarketPriceAvailableScript,
                null,
                new PageWaitForFunctionOptions { Timeout = Math.Min(_options.NavigationTimeoutMs, 12000) });
        }
        catch (TimeoutException)
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5000 });
            _logger.LogWarning(
                "Timed out waiting for visible TCGPlayer Market Price. Body text length: {BodyTextLength}",
                bodyText.Length);
        }
    }

    private async Task LogExtractionFailureDetailsAsync(IPage page)
    {
        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5000 });
            var lines = NormalizeTextLines(bodyText);
            var interestingLines = lines
                .Where(line =>
                    line.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("$", StringComparison.Ordinal))
                .Take(80)
                .ToList();

            _logger.LogWarning(
                "Could not extract market price. URL: {Url}. Body text length: {BodyTextLength}. Interesting text: {InterestingText}",
                page.Url,
                bodyText.Length,
                string.Join(" | ", interestingLines));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not log TCGPlayer extraction failure details.");
        }
    }

    private async Task<IPage> NewPageAsync()
    {
        var browser = await GetBrowserAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
            Locale = "en-US",
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ViewportSize = new ViewportSize { Width = 1365, Height = 900 },
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Cache-Control"] = "no-cache"
            }
        });

        return await context.NewPageAsync();
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
            Headless = _options.Headless,
            Args =
            [
                "--disable-blink-features=AutomationControlled"
            ]
        });

        return _browser;
    }

    private static bool TryParseCurrency(string? value, out decimal amount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            amount = 0;
            return false;
        }

        var normalized = value
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal)
            .Trim();

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private sealed record PricePointsExtractionResult(bool SectionFound, decimal? MarketPrice);

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
