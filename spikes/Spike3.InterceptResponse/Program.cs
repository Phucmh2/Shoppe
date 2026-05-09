using System.Text.Json;
using Microsoft.Playwright;

const string BaseUrl = "https://shopee.vn";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
    "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

const string SearchKeyword = "ao thun nam";
const int ScrollPasses = 4;
const int ScrollDelayMs = 2500;

var headless = !string.Equals(
    Environment.GetEnvironmentVariable("HEADLESS"), "false",
    StringComparison.OrdinalIgnoreCase);

Console.WriteLine("[setup] Ensuring Chromium is installed...");
Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = headless,
    Args = new[]
    {
        "--disable-blink-features=AutomationControlled",
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

await context.AddInitScriptAsync(@"
    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
    Object.defineProperty(navigator, 'languages', { get: () => ['vi-VN', 'vi', 'en-US', 'en'] });
    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
    window.chrome = { runtime: {} };
");

var page = await context.NewPageAsync();

// Capture every JSON-ish response from any /api/ endpoint (any size).
// Done in two passes: index first (cheap), bodies for promising ones only.
var captures = new List<Capture>();
page.Response += async (_, resp) =>
{
    var url = resp.Url;
    if (!url.Contains("/api/", StringComparison.OrdinalIgnoreCase)) return;
    try
    {
        var body = await resp.TextAsync();
        captures.Add(new Capture(url, resp.Status, body?.Length ?? 0, body));
    }
    catch { /* response body not retrievable */ }
};

PrintHeader();

Console.WriteLine("[step 1] Warm-up: GET shopee.vn home");
try
{
    var home = await page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = 60_000
    });
    Console.WriteLine($"  Home status: {home?.Status}");
}
catch (Exception ex) { Console.WriteLine($"  Home exception: {ex.Message}"); }
await Task.Delay(3000);

var searchUrl = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(SearchKeyword)}";
Console.WriteLine();
Console.WriteLine($"[step 2] Navigate to SEARCH page: {searchUrl}");
try
{
    var sr = await page.GotoAsync(searchUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.NetworkIdle,
        Timeout = 60_000
    });
    Console.WriteLine($"  Search nav status: {sr?.Status}");
}
catch (Exception ex) { Console.WriteLine($"  Search nav exception: {ex.Message}"); }
await Task.Delay(4000);

Console.WriteLine();
Console.WriteLine($"[step 3] Scroll {ScrollPasses} times to trigger lazy-load");
for (var i = 1; i <= ScrollPasses; i++)
{
    await page.EvaluateAsync("window.scrollBy(0, 1500)");
    Console.WriteLine($"  Scroll pass {i}/{ScrollPasses}");
    await Task.Delay(ScrollDelayMs);
}
await Task.Delay(2000);

// Analysis
Console.WriteLine();
Console.WriteLine("=== Captured API responses ===");
Console.WriteLine($"Total /api/ responses: {captures.Count}");
Console.WriteLine();

Console.WriteLine("--- All responses (sorted by size desc) ---");
foreach (var c in captures.OrderByDescending(c => c.BodyLen).Take(40))
{
    var hasItems = c.Body != null && (
        c.Body.Contains("\"itemid\"", StringComparison.Ordinal) ||
        c.Body.Contains("\"item_basic\"", StringComparison.Ordinal) ||
        c.Body.Contains("\"shopid\"", StringComparison.Ordinal)
    );
    var marker = hasItems ? " [HAS ITEMS]" : "";
    var shortUrl = c.Url.Length > 130 ? c.Url[..130] + "..." : c.Url;
    Console.WriteLine($"  [{c.Status}] {c.BodyLen,8}B{marker}  {shortUrl}");
}

// Save promising responses (large + contains item-like data) to disk
var outDir = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "spike3_responses"));
Directory.CreateDirectory(outDir);

var saved = 0;
foreach (var c in captures.Where(c => c.BodyLen > 1000))
{
    var hasItems = c.Body != null && (
        c.Body.Contains("\"itemid\"", StringComparison.Ordinal) ||
        c.Body.Contains("\"item_basic\"", StringComparison.Ordinal) ||
        c.Body.Contains("\"shopid\"", StringComparison.Ordinal)
    );
    var prefix = hasItems ? "ITEMS_" : "other_";
    var safe = $"{prefix}{saved:D3}_{c.Status}_{c.BodyLen}.json";
    var path = Path.Combine(outDir, safe);
    var content = $"// URL: {c.Url}\n// Status: {c.Status}\n// Size: {c.BodyLen} bytes\n\n{c.Body}";
    File.WriteAllText(path, content);
    saved++;
}
Console.WriteLine();
Console.WriteLine($"Saved {saved} responses (>1KB) to: {outDir}");

// Identify the items endpoint(s)
var itemEndpoints = captures
    .Where(c => c.Body != null &&
                c.BodyLen > 5000 &&
                (c.Body.Contains("\"itemid\"", StringComparison.Ordinal) ||
                 c.Body.Contains("\"item_basic\"", StringComparison.Ordinal)))
    .Select(c => StripQuery(c.Url))
    .Distinct()
    .ToList();

Console.WriteLine();
Console.WriteLine("=== Result ===");
if (itemEndpoints.Count == 0)
{
    Console.WriteLine("FAIL: No response contained item-like data.");
    Console.WriteLine("Possible causes:");
    Console.WriteLine("  - Search page rendered server-side (items in HTML, not API)");
    Console.WriteLine("  - Items API uses different field names (further obfuscated)");
    Console.WriteLine("  - Akamai still blocking even browser-initiated requests");
    Console.WriteLine();
    Console.WriteLine("Next step: try HTML scraping (spike #4) or alternative keyword.");
}
else
{
    Console.WriteLine("SUCCESS: Found item-bearing endpoint(s):");
    foreach (var ep in itemEndpoints)
    {
        Console.WriteLine($"  -> {ep}");
    }
}

return itemEndpoints.Count > 0 ? 0 : 1;


void PrintHeader()
{
    Console.WriteLine();
    Console.WriteLine("=== Shopee Spike #3 - Intercept Response ===");
    Console.WriteLine($"Search keyword : {SearchKeyword}");
    Console.WriteLine($"Scroll passes  : {ScrollPasses}");
    Console.WriteLine($"Headless       : {headless}");
    Console.WriteLine();
}

static string StripQuery(string url)
{
    var idx = url.IndexOf('?');
    return idx < 0 ? url : url[..idx];
}

record Capture(string Url, int Status, int BodyLen, string? Body);
