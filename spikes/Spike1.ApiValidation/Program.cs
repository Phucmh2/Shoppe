using System.Net;
using System.Text.Json;

const string BaseUrl = "https://shopee.vn";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
    "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

// Test category. If 0 items returned, swap to another ID.
// Sample category IDs (may need refresh):
//   11035567 - Thoi trang nu
//   11036132 - Thoi trang nam
//   11036030 - Dien thoai & Phu kien
//   11036910 - Nha cua & Doi song
const int CategoryId = 11035567;

const int PageSize = 60;
const int TotalProducts = 100;
const int DelayMs = 2500;

var handler = new HttpClientHandler
{
    AutomaticDecompression =
        DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
};

using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
http.DefaultRequestHeaders.Add("Accept", "application/json");
http.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en;q=0.8");
http.DefaultRequestHeaders.Add("Referer", $"{BaseUrl}/");
http.DefaultRequestHeaders.Add("X-API-SOURCE", "pc");
http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
http.DefaultRequestHeaders.Add("X-Shopee-Language", "vi");

PrintHeader();

var allItems = new List<JsonElement>();
var totalFetched = 0;
var errorCount = 0;
var blocked = false;

for (var offset = 0; offset < TotalProducts && totalFetched < TotalProducts; offset += PageSize)
{
    var url =
        $"{BaseUrl}/api/v4/search/search_items" +
        $"?by=sales" +
        $"&limit={PageSize}" +
        $"&match_id={CategoryId}" +
        $"&newest={offset}" +
        $"&order=desc" +
        $"&page_type=search" +
        $"&scenario=PAGE_CATEGORY" +
        $"&version=2";

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] GET offset={offset}");

    try
    {
        using var resp = await http.GetAsync(url);
        Console.WriteLine($"  Status: {(int)resp.StatusCode} {resp.StatusCode}");

        if (!resp.IsSuccessStatusCode)
        {
            errorCount++;
            var errBody = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"  Body: {Truncate(errBody, 300)}");

            if ((int)resp.StatusCode is 403 or 429)
            {
                Console.WriteLine("  Rate-limited or blocked. Stopping early.");
                blocked = true;
                break;
            }
            continue;
        }

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("  No 'items' array in response.");
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
    Console.WriteLine("=== Shopee Spike #1 - API Validation ===");
    Console.WriteLine($"Category ID    : {CategoryId}");
    Console.WriteLine($"Target items   : {TotalProducts}");
    Console.WriteLine($"Page size      : {PageSize}");
    Console.WriteLine($"Delay between  : {DelayMs}ms");
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
        Console.WriteLine("RESULT: BLOCKED. Need proxy or slower rate.");
    }
    else if (totalFetched == 0)
    {
        Console.WriteLine("RESULT: NO DATA. Likely wrong category ID or API shape changed.");
    }
    else if (errorCount == 0)
    {
        Console.WriteLine("RESULT: OK. API accessible without proxy at this rate.");
    }
    else
    {
        Console.WriteLine("RESULT: PARTIAL. Some errors, but API mostly accessible.");
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
    var outPath = Path.GetFullPath(Path.Combine(outDir, "spike1_output.json"));

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
