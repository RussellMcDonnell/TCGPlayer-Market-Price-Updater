namespace Riftbound.PriceUpdater.Models;

public sealed record TcgPriceResult(
    bool Success,
    decimal? MarketPrice,
    string? ErrorMessage,
    string FinalUrl
);
