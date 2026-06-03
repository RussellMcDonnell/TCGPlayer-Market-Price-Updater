# TCGPlayer Market Price Updater

A local Windows .NET 9 console app that reads the `Sales & Inventory` Google Sheets workbook, scrapes current visible TCGPlayer Market Prices with Playwright Chromium, and updates only columns `I` and `J` on the `Inventory/Pricing` worksheet.

## What It Updates

- Reads rows from `Inventory/Pricing`.
- By default, updates only rows where `Product Qty` in column `C` is greater than `0`.
- Set `InventoryPricing:UpdateAllProducts` to `true` to update every row that has a TCGPlayer link.
- Only processes rows where column `I` contains a TCGPlayer hyperlink formula or raw TCGPlayer URL.
- Writes column `I` as `=HYPERLINK("https://www.tcgplayer.com/product/661934/...", "$144.49")`.
- Writes column `J` with the local timestamp.
- Leaves all other columns untouched.
- Logs failures and continues with the next row.

## Install .NET 9

1. Download and install the .NET 9 SDK from <https://dotnet.microsoft.com/download/dotnet/9.0>.
2. Confirm the SDK is available:

```powershell
dotnet --version
```

The version should start with `9.`.

## Install Dependencies

From the repository root:

```powershell
dotnet restore
```

## Install Playwright Browsers

After restoring/building the project, install Chromium:

```powershell
dotnet build .\src\Riftbound.PriceUpdater\Riftbound.PriceUpdater.csproj
pwsh .\src\Riftbound.PriceUpdater\bin\Debug\net9.0\playwright.ps1 install chromium
```

If you use Release builds, run the `playwright.ps1` script from `bin\Release\net9.0` instead.

## Configure Google Service Account

The app expects a Google service account **JSON key file**. This is not a file the app generates. It is normally downloaded from Google Cloud after you create a key for the service account.

Preferred flow:

1. In Google Cloud, create or use an existing service account.
2. Enable the Google Sheets API for that Google Cloud project.
3. Open the service account, go to **Keys**, choose **Add key**, then choose **Create new key**.
4. Select **JSON**. Google will download a `.json` file.
5. Rename that downloaded file to `service-account.json`.
6. Place it at:

```text
src\Riftbound.PriceUpdater\service-account.json
```

You can also place it elsewhere and update `GoogleSheets:ServiceAccountJsonPath` in `appsettings.json`.

The file should look like this shape. Use the real values from the JSON key Google downloaded for your service account:

```json
{
  "type": "service_account",
  "project_id": "your-google-cloud-project-id",
  "private_key_id": "abc123...",
  "private_key": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n",
  "client_email": "service-account-name@your-google-cloud-project-id.iam.gserviceaccount.com",
  "client_id": "123456789012345678901",
  "auth_uri": "https://accounts.google.com/o/oauth2/auth",
  "token_uri": "https://oauth2.googleapis.com/token",
  "auth_provider_x509_cert_url": "https://www.googleapis.com/oauth2/v1/certs",
  "client_x509_cert_url": "https://www.googleapis.com/robot/v1/metadata/x509/service-account-name%40your-google-cloud-project-id.iam.gserviceaccount.com",
  "universe_domain": "googleapis.com"
}
```

Do not use an OAuth client secret file here. The app uses service account authentication, so the important fields are `type`, `private_key`, `client_email`, and the Google token/cert URLs. If you only have the service account email and project ID but do not have `private_key`, create/download a new JSON key from Google Cloud.

## Share The Google Sheet

1. Open the `Sales & Inventory` Google Sheets workbook.
2. Click **Share**.
3. Add the service account email from the JSON file. It usually looks like:

```text
name@project-id.iam.gserviceaccount.com
```

4. Give it editor access.

## Configure appsettings.json

Default config is in `src\Riftbound.PriceUpdater\appsettings.json`:

```json
{
  "GoogleSheets": {
    "SpreadsheetId": "1wGfkCP1aJBEjhTOKu21RGPmcZhVMQiGsp7ovuOynvqo",
    "WorksheetName": "Inventory/Pricing",
    "ServiceAccountJsonPath": "service-account.json"
  },
  "InventoryPricing": {
    "HeaderRow": 1,
    "FirstDataRow": 2,
    "ProductNameColumn": "A",
    "InventoryQuantityColumn": "C",
    "MarketPriceColumn": "I",
    "LastUpdatedColumn": "J",
    "UpdateAllProducts": false
  },
  "Scraping": {
    "Headless": true,
    "DelayBetweenRequestsMs": 2500,
    "MaxRetries": 2,
    "NavigationTimeoutMs": 30000
  }
}
```

Optional local overrides can go in `appsettings.local.json`.

## Run The App

From the repository root:

```powershell
dotnet run --project .\src\Riftbound.PriceUpdater\Riftbound.PriceUpdater.csproj
```

The app logs rows scanned, eligible, skipped, updated, failed, product names, row numbers, product IDs, old prices, new prices, errors, and total execution time.

To test only the TCGPlayer scraper without reading or updating Google Sheets:

```powershell
dotnet run --project .\src\Riftbound.PriceUpdater\Riftbound.PriceUpdater.csproj -- --scrape-url "https://www.tcgplayer.com/product/661934/Riftbound%20League%20of%20Legends%20Trading%20Card%20Game-Spiritforged-Spiritforged%20Booster%20Display?Language=English"
```

## Schedule With Windows Task Scheduler

1. Build the app:

```powershell
dotnet publish .\src\Riftbound.PriceUpdater\Riftbound.PriceUpdater.csproj -c Release -o .\publish
```

2. Open **Task Scheduler**.
3. Choose **Create Task**.
4. On **General**, choose **Run whether user is logged on or not**.
5. On **Triggers**, add the schedule you want.
6. On **Actions**, add:

```text
Program/script: C:\path\to\repo\publish\Riftbound.PriceUpdater.exe
Start in: C:\path\to\repo\publish
```

7. Make sure `service-account.json` is present in the publish folder or update `ServiceAccountJsonPath` to an absolute path.
8. Save the task and run it once manually to confirm logs and Google Sheet updates.
