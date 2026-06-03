namespace Riftbound.PriceUpdater.Options;

public sealed class ScrapingOptions
{
    public const string SectionName = "Scraping";

    public bool Headless { get; set; } = true;

    public int DelayBetweenRequestsMs { get; set; } = 2500;

    public int MaxRetries { get; set; } = 2;

    public int NavigationTimeoutMs { get; set; } = 30000;
}
