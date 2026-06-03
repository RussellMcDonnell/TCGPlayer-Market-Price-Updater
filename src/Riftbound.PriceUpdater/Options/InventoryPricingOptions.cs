namespace Riftbound.PriceUpdater.Options;

public sealed class InventoryPricingOptions
{
    public const string SectionName = "InventoryPricing";

    public int HeaderRow { get; set; } = 1;

    public int FirstDataRow { get; set; } = 2;

    public string ProductNameColumn { get; set; } = "A";

    public string InventoryQuantityColumn { get; set; } = "C";

    public string MarketPriceColumn { get; set; } = "I";

    public string LastUpdatedColumn { get; set; } = "J";

    public bool UpdateAllProducts { get; set; }
}
