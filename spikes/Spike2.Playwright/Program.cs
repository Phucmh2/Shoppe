using System.Text.Json;
using Microsoft.Playwright;

const string BaseUrl = "https://shopee.vn";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
    "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

const int CategoryId = 11035567;   // Thoi trang nu (swap if 0 results)
const int PageSize = 60;
const int TotalProducts = 100;
const int DelayMs = 2500;

// Set HEADLESS=false in env to watch the browser. Useful first run.
var headless = !string.Equals(
    Environment.GetEnvironmentVariable("HEADLESS"), "false",
    StringComparison.OrdinalIgnoreCase);

// Auto-install Chromium on first run.
Console.WriteLine("[setup] Ensuring Chromium is installed (first run downloads ~150MB)...");
var installExit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
if (installExit != 0)
{
    Console.WriteLine($"  WARN: playwright install returned exit code {installExit}");
}

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = headless,
    Args = new[]
    {
        "--disable-blink-features=AutomationControlled",
        "--disable-features=IsolateOrigins,site-per-process",
        "--no-sandbox"
    }
});

var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    UserAgent = UserAgent,
    Locale = "vi-VN",
    TimezoneId = "Asia/Ho_Chi_Minh",
    ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
    ExtraHTTPHeaders = new Dictionary<string, string>
    {
        ["Accept-Language"] = "vi-VN,vi;q=0.9,en;q=0.8"
    }
});

// Basic stealth: hide automation signals.
await context.AddInitScriptAsync(@"
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
    Object.defineProperty(navigator, 'languages', { get: () => ['vi-VN', 'vi', 'en-US', 'en'] });
    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
    window.chrome = { runtime: {} };
");

var page = await context.NewPageAsync();

PrintHeader();

Console.WriteLine("[warm-up] Navigating to shopee.vn home...");
try
{
    var warm = await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 60_000
    });
    Console.WriteLine($"  Home status: {warm?.Status}");
}
catch (Exception ex)
{
    Console.WriteLine($"  Warm-up exception: {ex.Message}");
}

// Let JS challenges complete.
await Task.Delay(5000);

var cookiesAfter = await context.CookiesAsync(new[] { BaseUrl });
Console.WriteLine($"  Cookies after warm-up: {cookiesAfter.Count}");
if (cookiesAfter.Count > 0)
{
    var names = string.Join(", ", cookiesAfter.Take(8).Select(c => c.Name));
    Console.WriteLine($"  Cookie names (first 8): {names}");
}
Console.WriteLine();

var allItems = new List<JsonElement>();
var totalFetched = 0;
var errorCount = 0;
var blocked = false;

for (var offset = 0; offset < TotalProducts && totalFetched < TotalProducts; offset += PageSize)
{
    var apiUrl =
        $"{BaseUrl}/api/v4/search/search_items" +
        $"?by=sales&limit={PageSize}&match_id={CategoryId}" +
        $"&newest={offset}&order=desc&page_type=search" +
        $"&scenario=PAGE_CATEGORY&version=2";

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Fetch offset={offset}");

    try
    {
        // Run fetch INSIDE the page context => uses real Chrome TLS, real cookies, real headers.
        var resultJson = await page.EvaluateAsync<string>(@"async (url) => {
            const r = await fetch(url, {
                method: 'GET',
                headers: {
                    'Accept': 'application/json',
                    'X-API-SOURCE': 'pc',
                    'X-Requested-With': 'XMLHttpRequest',
                    'X-Shopee-Language': 'vi'
                },
                credentials: 'include'
            });
            const text = await r.text();
            return JSON.stringify({ status: r.status, body: text });
        }", apiUrl);

        using var doc = JsonDocument.Parse(resultJson);
        var status = doc.RootElement.GetProperty("status").GetInt32();
        var bodyText = doc.RootElement.GetProperty("body").GetString() ?? "";

        Console.WriteLine($"  Status: {status}");

        if (status != 200)
        {
            errorCount++;
            Console.WriteLine($"  Body: {Truncate(bodyText, 300)}");
            if (status is 403 or 429)
            {
                Console.WriteLine("  Blocked. Stopping early.");
                blocked = true;
                break;
            }
            continue;
        }

        using var bodyDoc = JsonDocument.Parse(bodyText);
        var root = bodyDoc.RootElement;

        if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  No 'items' array.");
            Console.WriteLine($"  Top-level keys: {string.Join(", ", root.EnumerateObject().Select(p => p.Name))}");
            continue;
        }

        var pageCount = 0;
        foreach (var item in itemsEl.EnumerateArray())
        {
            allItems.Add(item.Clone());
            pageCount++;
            totalFetched++;
            if (totalFetched >= TotalProducts) break;
        }
        Console.WriteLine($"  Got {pageCount} items (total fetched: {totalFetched})");

        if (pageCount == 0)
        {
            Console.WriteLine("  Empty page. Maybe wrong category ID. Stopping.");
            break;
        }
    }
    catch (Exception ex)
    {
        errorCount++;
        Console.WriteLine($"  Exception: {ex.Message}");
    }

    if (totalFetched < TotalProducts)
    {
        await Task.Delay(DelayMs);
    }
}

PrintSummary();
PrintSampleItems();
SaveOutput();

return blocked ? 2 : (totalFetched == 0 ? 1 : 0);


void PrintHeader()
{
    Console.WriteLine();
    Console.WriteLine("=== Shopee Spike #2 - Playwright + In-page Fetch ===");
    Console.WriteLine($"Category ID    : {CategoryId}");
    Console.WriteLine($"Target items   : {TotalProducts}");
    Console.WriteLine($"Page size      : {PageSize}");
    Console.WriteLine($"Delay between  : {DelayMs}ms");
    Console.WriteLine($"Headless       : {headless}");
    Console.WriteLine();
}

void PrintSummary()
{
    Console.WriteLine();
    Console.WriteLine("=== Summary ===");
    Console.WriteLine($"Items fetched : {totalFetched}");
    Console.WriteLine($"Errors        : {errorCount}");
    Console.WriteLine($"Blocked       : {blocked}");
    Console.WriteLine();

    if (blocked)
    {
        Console.WriteLine("RESULT: BLOCKED. Akamai detected even with real Chrome - need stealth tweaks or proxy.");
    }
    else if (totalFetched == 0)
    {
        Console.WriteLine("RESULT: NO DATA. Likely wrong category ID.");
    }
    else if (errorCount == 0)
    {
        Console.WriteLine("RESULT: OK. Playwright bypasses Akamai.");
    }
    else
    {
        Console.WriteLine("RESULT: PARTIAL. Mostly works but some errors.");
    }
}

void PrintSampleItems()
{
    if (allItems.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine("=== First 5 items ===");
    foreach (var item in allItems.Take(5))
    {
        if (!item.TryGetProperty("item_basic", out var basic)) continue;

        var itemId = ReadLong(basic, "itemid");
        var shopId = ReadLong(basic, "shopid");
        var name = ReadString(basic, "name") ?? "?";
        var priceRaw = ReadLong(basic, "price");
        var soldHist = ReadLong(basic, "historical_sold");
        var sold30d = ReadLong(basic, "sold");
        var stock = ReadLong(basic, "stock");
        var rating = ReadDouble(basic, "item_rating", "rating_star");

        Console.WriteLine($"- [{itemId}] {Truncate(name, 70)}");
        Console.WriteLine($"  Shop: {shopId}  Price: {priceRaw / 100000m:N0}d  " +
                          $"Sold(hist): {soldHist}  Sold(30d): {sold30d}  Stock: {stock}  Rating: {rating:F2}");
    }
}

void SaveOutput()
{
    if (allItems.Count == 0) return;

    var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
    var outPath = Path.GetFullPath(Path.Combine(outDir, "spike2_output.json"));

    var firstItem = JsonDocument.Parse(allItems[0].GetRawText()).RootElement;
    var payload = new
    {
        Timestamp = DateTime.Now,
        CategoryId,
        TotalFetched = totalFetched,
        ErrorCount = errorCount,
        Blocked = blocked,
        SampleItem = firstItem
    };

    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(outPath, JsonSerializer.Serialize(payload, opts));
    Console.WriteLine();
    Console.WriteLine($"Sample saved to: {outPath}");
}

static string Truncate(string s, int max) =>
    string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "...");

static long ReadLong(JsonElement el, string prop)
{
    if (!el.TryGetProperty(prop, out var v)) return 0;
    return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

static string? ReadString(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static double ReadDouble(JsonElement el, params string[] path)
{
    var current = el;
    foreach (var p in path)
    {
        if (current.ValueKind != JsonValueKind.Object) return 0;
        if (!current.TryGetProperty(p, out var next)) return 0;
        current = next;
    }
    return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var d) ? d : 0;
}
