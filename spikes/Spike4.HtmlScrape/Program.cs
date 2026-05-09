using System.Text.RegularExpressions;
using Microsoft.Playwright;

const string BaseUrl = "https://shopee.vn";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
    "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

const string SearchKeyword = "ao thun nam";
const int InitialWaitMs = 8000;
const int ScrollPasses = 3;
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
    ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
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

Console.WriteLine();
Console.WriteLine("=== Shopee Spike #4 - HTML Scrape ===");
Console.WriteLine($"Search keyword : {SearchKeyword}");
Console.WriteLine($"Headless       : {headless}");
Console.WriteLine();

Console.WriteLine("[step 1] Warm-up home...");
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
await Task.Delay(2500);

var searchUrl = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(SearchKeyword)}";
Console.WriteLine();
Console.WriteLine($"[step 2] Navigate to: {searchUrl}");
try
{
    var sr = await page.GotoAsync(searchUrl, new PageGotoOptions
    {
        WaitUntil = WaitUntilState.NetworkIdle,
        Timeout = 60_000
    });
    Console.WriteLine($"  Status: {sr?.Status}");
}
catch (Exception ex) { Console.WriteLine($"  Exception: {ex.Message}"); }

Console.WriteLine();
Console.WriteLine($"[step 3] Wait {InitialWaitMs}ms for items to render...");
await Task.Delay(InitialWaitMs);

// Scroll a bit so lazy-loaded items also appear in DOM.
Console.WriteLine($"[step 4] Scroll {ScrollPasses}x to trigger lazy-load...");
for (var i = 1; i <= ScrollPasses; i++)
{
    await page.EvaluateAsync("window.scrollBy(0, 1200)");
    await Task.Delay(ScrollDelayMs);
}
await Task.Delay(1500);

// Detect if Shopee shows a CAPTCHA / verification challenge.
var pageText = (await page.TextContentAsync("body")) ?? "";
var lowerText = pageText.ToLowerInvariant();
var hasChallenge =
    lowerText.Contains("captcha") ||
    lowerText.Contains("verify you are human") ||
    lowerText.Contains("xác minh") ||
    lowerText.Contains("vui lòng xác minh");
Console.WriteLine();
Console.WriteLine($"[step 5] Challenge detected: {hasChallenge}");
Console.WriteLine($"         Body text length : {pageText.Length} chars");

// Try a list of candidate selectors common across Shopee versions.
string[] candidateSelectors = new[]
{
    "li.shopee-search-item-result__item",
    "div.shopee-search-item-result__item",
    "a[data-sqe='link']",
    "[data-sqe='link']",
    ".col-xs-2-4.shopee-search-item-result__item",
    "div[data-sqe='item']",
    "li[data-sqe='item']",
    ".contents > a",
    "section ul li a",
};

Console.WriteLine();
Console.WriteLine("[step 6] Try candidate selectors:");
string? winningSelector = null;
var winningCount = 0;
foreach (var sel in candidateSelectors)
{
    try
    {
        var els = await page.QuerySelectorAllAsync(sel);
        Console.WriteLine($"  {els.Count,4}  '{sel}'");
        if (els.Count > winningCount)
        {
            winningCount = els.Count;
            winningSelector = sel;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERR   '{sel}'  {ex.Message}");
    }
}

Console.WriteLine();
if (winningSelector is null || winningCount < 3)
{
    Console.WriteLine("FAIL: No usable product selector found.");
}
else
{
    Console.WriteLine($"WINNER: '{winningSelector}' with {winningCount} matches.");

    var elements = await page.QuerySelectorAllAsync(winningSelector);
    Console.WriteLine();
    Console.WriteLine("=== Sample of first 5 cards (heuristic extraction) ===");

    var idx = 0;
    foreach (var el in elements.Take(5))
    {
        idx++;
        var text = (await el.TextContentAsync()) ?? "";
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Try to find href for product URL.
        var anchor = await el.QuerySelectorAsync("a[href]") ?? el;
        var href = await anchor.GetAttributeAsync("href");

        // Try to find img src.
        var img = await el.QuerySelectorAsync("img");
        var imgSrc = img is null ? null : await img.GetAttributeAsync("src");

        // Heuristic: pull "Đã bán <N>" if present.
        var soldMatch = Regex.Match(text, @"(?:Đã bán|đã bán|Sold)\s*([\d.,k]+)", RegexOptions.IgnoreCase);
        var sold = soldMatch.Success ? soldMatch.Groups[1].Value : "?";

        // Heuristic: find first "đ" / "₫" price token.
        var priceMatch = Regex.Match(text, @"(\d{1,3}(?:[.,]\d{3})+|\d+)\s*[đ₫]");
        var price = priceMatch.Success ? priceMatch.Groups[1].Value : "?";

        Console.WriteLine();
        Console.WriteLine($"--- Card #{idx} ---");
        Console.WriteLine($"  href : {Truncate(href ?? "?", 90)}");
        Console.WriteLine($"  img  : {Truncate(imgSrc ?? "?", 90)}");
        Console.WriteLine($"  price: {price}");
        Console.WriteLine($"  sold : {sold}");
        Console.WriteLine($"  text : {Truncate(text, 200)}");
    }
}

// Save full page HTML + screenshot regardless of result.
var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var htmlPath = Path.Combine(outDir, "spike4_page.html");
var shotPath = Path.Combine(outDir, "spike4_screenshot.png");
var firstCardPath = Path.Combine(outDir, "spike4_first_card.html");

var fullHtml = await page.ContentAsync();
File.WriteAllText(htmlPath, fullHtml);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = shotPath, FullPage = true });

if (winningSelector is not null && winningCount > 0)
{
    var first = (await page.QuerySelectorAllAsync(winningSelector)).FirstOrDefault();
    if (first is not null)
    {
        var firstHtml = await first.EvaluateAsync<string>("el => el.outerHTML");
        File.WriteAllText(firstCardPath, firstHtml ?? "");
    }
}

Console.WriteLine();
Console.WriteLine("=== Artifacts saved ===");
Console.WriteLine($"  Full HTML : {htmlPath}");
Console.WriteLine($"  Screenshot: {shotPath}");
if (File.Exists(firstCardPath))
    Console.WriteLine($"  First card: {firstCardPath}");

return (winningSelector is not null && winningCount >= 3) ? 0 : 1;


static string Truncate(string s, int max) =>
    string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "...");
