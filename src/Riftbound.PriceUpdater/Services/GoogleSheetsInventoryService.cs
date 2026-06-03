using System.Globalization;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Riftbound.PriceUpdater.Models;
using Riftbound.PriceUpdater.Options;

namespace Riftbound.PriceUpdater.Services;

public sealed class GoogleSheetsInventoryService
{
    private static readonly Regex HyperlinkFormulaRegex = new(
        "^=HYPERLINK\\(\\s*\"(?<url>(?:[^\"]|\"\")+)\"\\s*,\\s*\"(?<label>(?:[^\"]|\"\")*)\"\\s*\\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        "https?://(?:www\\.)?tcgplayer\\.com/[^\\s\"')]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrencyRegex = new(
        "\\$\\s*(?<amount>\\d{1,3}(?:,\\d{3})*(?:\\.\\d{2})?|\\d+(?:\\.\\d{2})?)",
        RegexOptions.Compiled);

    private readonly GoogleSheetsOptions _googleOptions;
    private readonly InventoryPricingOptions _pricingOptions;
    private readonly ILogger<GoogleSheetsInventoryService> _logger;
    private readonly Lazy<SheetsService> _sheetsService;

    public GoogleSheetsInventoryService(
        IOptions<GoogleSheetsOptions> googleOptions,
        IOptions<InventoryPricingOptions> pricingOptions,
        ILogger<GoogleSheetsInventoryService> logger)
    {
        _googleOptions = googleOptions.Value;
        _pricingOptions = pricingOptions.Value;
        _logger = logger;
        _sheetsService = new Lazy<SheetsService>(CreateSheetsService);
    }

    public async Task<IReadOnlyList<InventoryProductRow>> ReadInventoryRowsAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();

        var lastColumn = MaxColumn(
            _pricingOptions.ProductNameColumn,
            _pricingOptions.InventoryQuantityColumn,
            _pricingOptions.MarketPriceColumn);

        var range = ToSheetRange(_pricingOptions.FirstDataRow, lastColumn);
        var request = _sheetsService.Value.Spreadsheets.Get(_googleOptions.SpreadsheetId);
        request.IncludeGridData = true;
        request.Ranges = new Google.Apis.Util.Repeatable<string>([range]);

        var response = await request.ExecuteAsync(cancellationToken);
        var rowData = response.Sheets?
            .SelectMany(sheet => sheet.Data ?? [])
            .SelectMany(data => data.RowData ?? [])
            .ToList() ?? [];
        var rows = new List<InventoryProductRow>(rowData.Count);

        var productIndex = ColumnToIndex(_pricingOptions.ProductNameColumn);
        var quantityIndex = ColumnToIndex(_pricingOptions.InventoryQuantityColumn);
        var marketPriceIndex = ColumnToIndex(_pricingOptions.MarketPriceColumn);

        for (var i = 0; i < rowData.Count; i++)
        {
            var sheetRowNumber = _pricingOptions.FirstDataRow + i;
            var cells = rowData[i].Values ?? [];
            var marketPriceCell = GetCell(cells, marketPriceIndex);
            var marketPriceFormulaOrValue = GetCellFormulaOrDisplayValue(marketPriceCell);
            var cellHyperlink = GetCellTcgPlayerHyperlink(marketPriceCell);
            var formulaOrRawUrl = ExtractTcgPlayerUrl(marketPriceFormulaOrValue);
            var tcgPlayerUrl = cellHyperlink ?? formulaOrRawUrl;

            rows.Add(new InventoryProductRow(
                sheetRowNumber,
                GetCellFormulaOrDisplayValue(GetCell(cells, productIndex))?.Trim() ?? "",
                ParseDecimal(GetCellFormulaOrDisplayValue(GetCell(cells, quantityIndex))),
                marketPriceFormulaOrValue,
                tcgPlayerUrl));
        }

        _logger.LogInformation("Read {RowCount} inventory rows from Google Sheets", rows.Count);
        return rows;
    }

    public async Task UpdateMarketPriceAsync(
        InventoryProductRow row,
        decimal marketPrice,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.TcgPlayerUrl))
        {
            throw new InvalidOperationException($"Row {row.RowNumber} does not have a TCGPlayer URL.");
        }

        var escapedUrl = EscapeFormulaString(row.TcgPlayerUrl);
        var priceLabel = FormatCurrency(marketPrice);
        var timestamp = updatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var marketPriceCell = ToA1(_pricingOptions.MarketPriceColumn, row.RowNumber);
        var lastUpdatedCell = ToA1(_pricingOptions.LastUpdatedColumn, row.RowNumber);
        var data = new List<ValueRange>
        {
            new()
            {
                Range = ToSheetRange(marketPriceCell),
                Values = [[string.Create(CultureInfo.InvariantCulture, $"=HYPERLINK(\"{escapedUrl}\", \"{priceLabel}\")")]]
            },
            new()
            {
                Range = ToSheetRange(lastUpdatedCell),
                Values = [[timestamp]]
            }
        };

        var batch = new BatchUpdateValuesRequest
        {
            ValueInputOption = "USER_ENTERED",
            Data = data
        };

        var request = _sheetsService.Value.Spreadsheets.Values.BatchUpdate(batch, _googleOptions.SpreadsheetId);
        await request.ExecuteAsync(cancellationToken);
    }

    public static string? ExtractTcgPlayerUrl(string? formulaOrValue)
    {
        if (string.IsNullOrWhiteSpace(formulaOrValue))
        {
            return null;
        }

        var hyperlinkMatch = HyperlinkFormulaRegex.Match(formulaOrValue.Trim());
        if (hyperlinkMatch.Success)
        {
            return UnescapeFormulaString(hyperlinkMatch.Groups["url"].Value);
        }

        var urlMatch = UrlRegex.Match(formulaOrValue);
        return urlMatch.Success ? urlMatch.Value : null;
    }

    public static decimal? ExtractPrice(string? formulaOrValue)
    {
        if (string.IsNullOrWhiteSpace(formulaOrValue))
        {
            return null;
        }

        var hyperlinkMatch = HyperlinkFormulaRegex.Match(formulaOrValue.Trim());
        if (hyperlinkMatch.Success)
        {
            var label = UnescapeFormulaString(hyperlinkMatch.Groups["label"].Value);
            return ParseCurrency(label) ?? ParseDecimal(label);
        }

        return ParseCurrency(formulaOrValue) ?? ParseDecimal(formulaOrValue);
    }

    public static string? ExtractTcgPlayerProductId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var productIndex = Array.FindIndex(segments, segment =>
            string.Equals(segment, "product", StringComparison.OrdinalIgnoreCase));

        return productIndex >= 0 && productIndex + 1 < segments.Length
            ? segments[productIndex + 1]
            : null;
    }

    private SheetsService CreateSheetsService()
    {
        var credentialsPath = Path.IsPathRooted(_googleOptions.ServiceAccountJsonPath)
            ? _googleOptions.ServiceAccountJsonPath
            : Path.Combine(AppContext.BaseDirectory, _googleOptions.ServiceAccountJsonPath);

        if (!File.Exists(credentialsPath))
        {
            throw new FileNotFoundException(
                $"Google service account JSON was not found at '{credentialsPath}'.",
                credentialsPath);
        }

        GoogleCredential credential;
        using (var stream = File.OpenRead(credentialsPath))
        {
            credential = GoogleCredential.FromStream(stream)
                .CreateScoped(SheetsService.Scope.Spreadsheets);
        }

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Riftbound TCGPlayer Market Price Updater"
        });
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_googleOptions.SpreadsheetId))
        {
            throw new InvalidOperationException("GoogleSheets:SpreadsheetId is required.");
        }

        if (string.IsNullOrWhiteSpace(_googleOptions.WorksheetName))
        {
            throw new InvalidOperationException("GoogleSheets:WorksheetName is required.");
        }
    }

    private string ToSheetRange(int firstRow, string lastColumn)
    {
        var sheet = EscapeSheetName(_googleOptions.WorksheetName);
        return $"{sheet}!A{firstRow}:{lastColumn}";
    }

    private string ToSheetRange(string cell)
    {
        var sheet = EscapeSheetName(_googleOptions.WorksheetName);
        return $"{sheet}!{cell}";
    }

    private static string EscapeSheetName(string worksheetName)
    {
        return $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string ToA1(string column, int rowNumber)
    {
        return $"{column.ToUpperInvariant()}{rowNumber}";
    }

    private static CellData? GetCell(IList<CellData> row, int zeroBasedColumnIndex)
    {
        return zeroBasedColumnIndex < row.Count ? row[zeroBasedColumnIndex] : null;
    }

    private static string? GetCellFormulaOrDisplayValue(CellData? cell)
    {
        if (cell is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cell.UserEnteredValue?.FormulaValue))
        {
            return cell.UserEnteredValue.FormulaValue;
        }

        if (!string.IsNullOrWhiteSpace(cell.FormattedValue))
        {
            return cell.FormattedValue;
        }

        if (!string.IsNullOrWhiteSpace(cell.UserEnteredValue?.StringValue))
        {
            return cell.UserEnteredValue.StringValue;
        }

        if (cell.UserEnteredValue?.NumberValue is not null)
        {
            return cell.UserEnteredValue.NumberValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (cell.EffectiveValue?.NumberValue is not null)
        {
            return cell.EffectiveValue.NumberValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(cell.EffectiveValue?.StringValue))
        {
            return cell.EffectiveValue.StringValue;
        }

        return null;
    }

    private static string? GetCellTcgPlayerHyperlink(CellData? cell)
    {
        if (cell is null)
        {
            return null;
        }

        var candidateLinks = new List<string?>();

        candidateLinks.Add(cell.Hyperlink);
        candidateLinks.AddRange(cell.TextFormatRuns?
            .Select(run => run.Format?.Link?.Uri) ?? []);
        candidateLinks.Add(cell.UserEnteredFormat?.TextFormat?.Link?.Uri);

        foreach (var candidateLink in candidateLinks)
        {
            var tcgPlayerUrl = ExtractTcgPlayerUrl(candidateLink);
            if (!string.IsNullOrWhiteSpace(tcgPlayerUrl))
            {
                return tcgPlayerUrl;
            }
        }

        return null;
    }

    private static int ColumnToIndex(string columnName)
    {
        var result = 0;
        foreach (var c in columnName.Trim().ToUpperInvariant())
        {
            if (c is < 'A' or > 'Z')
            {
                throw new ArgumentException($"Invalid column name '{columnName}'.", nameof(columnName));
            }

            result = (result * 26) + c - 'A' + 1;
        }

        return result - 1;
    }

    private static string MaxColumn(params string[] columns)
    {
        return columns
            .OrderByDescending(ColumnToIndex)
            .First()
            .ToUpperInvariant();
    }

    private static decimal? ParseCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = CurrencyRegex.Match(value);
        return match.Success ? ParseDecimal(match.Groups["amount"].Value) : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Trim()
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string FormatCurrency(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string EscapeFormulaString(string value)
    {
        return value.Replace("\"", "\"\"", StringComparison.Ordinal);
    }

    private static string UnescapeFormulaString(string value)
    {
        return value.Replace("\"\"", "\"", StringComparison.Ordinal);
    }
}
