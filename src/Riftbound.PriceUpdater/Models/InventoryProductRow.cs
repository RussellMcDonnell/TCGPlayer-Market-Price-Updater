namespace Riftbound.PriceUpdater.Models;

public sealed record InventoryProductRow(
    int RowNumber,
    string ProductName,
    decimal? ProductQuantity,
    string? ExistingMarketPriceFormulaOrValue,
    string? TcgPlayerUrl
);
