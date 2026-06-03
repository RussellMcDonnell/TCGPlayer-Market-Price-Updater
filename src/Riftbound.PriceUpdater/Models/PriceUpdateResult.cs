namespace Riftbound.PriceUpdater.Models;

public sealed record PriceUpdateResult(
    int RowNumber,
    string ProductName,
    bool Updated,
    decimal? OldPrice,
    decimal? NewPrice,
    string? ErrorMessage
);
