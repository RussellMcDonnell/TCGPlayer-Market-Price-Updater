namespace Riftbound.PriceUpdater.Options;

public sealed class GoogleSheetsOptions
{
    public const string SectionName = "GoogleSheets";

    public string SpreadsheetId { get; set; } = "";

    public string WorksheetName { get; set; } = "";

    public string ServiceAccountJsonPath { get; set; } = "service-account.json";
}
