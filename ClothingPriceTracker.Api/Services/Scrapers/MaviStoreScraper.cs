using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingPriceTracker.Api.Models;
using ClothingPriceTracker.Api.Services.Abstractions;

namespace ClothingPriceTracker.Api.Services.Scrapers;

public class MaviStoreScraper : IStoreScraper
{
    private readonly HttpClient _httpClient;
    private const string MaviApiBase = "https://p1-api.mavi.com/maviwebservices/v2/mavi";
    private const string MaviWebBase = "https://www.mavi.com";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public string StoreName => "Mavi";

    public MaviStoreScraper(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);
        _httpClient.DefaultRequestHeaders.Add("Origin", MaviWebBase);
        _httpClient.DefaultRequestHeaders.Add("Referer", $"{MaviWebBase}/");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public bool CanHandle(string urlOrCode)
    {
        if (string.IsNullOrWhiteSpace(urlOrCode))
            return false;

        var trimmed = urlOrCode.Trim();
        if (trimmed == "1")
            return true;

        if (trimmed.Contains("mavi.com", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.StartsWith("bershka", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("bsk-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("lcw-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("lcw.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("c0p", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Mavi ürün kodu formatı: örn. 0613510-70057 veya 00351A4072
        return Regex.IsMatch(trimmed, @"^[0-9][A-Za-z0-9\-_]{4,}$");
    }

    public async Task<ClothingItem?> GetProductByUrlOrCodeAsync(string urlOrCode)
    {
        var productCode = ExtractProductCode(urlOrCode);
        if (string.IsNullOrWhiteSpace(productCode))
            return null;

        var requestUrl = $"{MaviApiBase}/products/basic/{Uri.EscapeDataString(productCode)}?fields=FULL&lang=tr&curr=TRY";
        var json = await FetchJsonFromMaviAsync(requestUrl);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return MapProductElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ClothingItem>> SearchProductsAsync(string query = "tisort", int pageSize = 6)
    {
        var safeQuery = string.IsNullOrWhiteSpace(query) ? "tisort" : query.Trim();
        var requestUrl = $"{MaviApiBase}/products/search?fields=FULL&query={Uri.EscapeDataString(safeQuery)}&pageSize={pageSize}&lang=tr&curr=TRY";

        var json = await FetchJsonFromMaviAsync(requestUrl);
        if (string.IsNullOrWhiteSpace(json))
            return new List<ClothingItem>();

        var items = new List<ClothingItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("products", out var productsEl) && productsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var prod in productsEl.EnumerateArray())
                {
                    var mapped = MapProductElement(prod);
                    if (mapped != null)
                    {
                        items.Add(mapped);
                    }
                }
            }
        }
        catch
        {
            // JSON parse hatası durumunda boş liste dön
        }

        return items;
    }

    private async Task<string?> FetchJsonFromMaviAsync(string requestUrl)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "curl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add(requestUrl);
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"User-Agent: {BrowserUserAgent}");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"Origin: {MaviWebBase}");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"Referer: {MaviWebBase}/");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Accept: application/json");

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("{"))
                {
                    return output;
                }
            }
        }
        catch
        {
            // curl.exe çalışmazsa HttpClient fallback'ine geç
        }

        try
        {
            var response = await _httpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractProductCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (trimmed == "1")
            return "00351A4072";

        var match = Regex.Match(trimmed, @"/p/([A-Za-z0-9\-_]+)");
        if (match.Success)
            return match.Groups[1].Value;

        if (Regex.IsMatch(trimmed, @"^[A-Za-z0-9\-_]+$"))
            return trimmed;

        return null;
    }

    private ClothingItem? MapProductElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        var code = GetString(el, "code");
        var name = GetString(el, "name");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return null;

        var productTitle = GetString(el, "productTitle");
        if (!string.IsNullOrWhiteSpace(productTitle) && !name.Contains(productTitle, StringComparison.OrdinalIgnoreCase))
        {
            name = $"{productTitle} {name}";
        }

        var regularPrice = GetDecimal(el, "price", "value");
        var salePrice = GetDecimal(el, "salePrice", "value") ?? GetDecimal(el, "storeSalePrice", "value");
        var effectivePrice = salePrice ?? regularPrice ?? 0m;
        var originalPrice = (salePrice.HasValue && regularPrice.HasValue && regularPrice.Value > salePrice.Value)
            ? regularPrice
            : null;

        int? discountRate = null;
        if (el.TryGetProperty("discountRate", out var discEl) && discEl.ValueKind == JsonValueKind.Number && discEl.TryGetInt32(out var discVal) && discVal > 0)
        {
            discountRate = discVal;
        }

        var category = "Giyim";
        if (el.TryGetProperty("mainCategories", out var mainCats) && mainCats.ValueKind == JsonValueKind.Array && mainCats.GetArrayLength() > 0)
        {
            category = GetString(mainCats[0], "name") ?? category;
        }

        var gallery = new List<string>();
        if (el.TryGetProperty("galleryImages", out var galEl) && galEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in galEl.EnumerateArray())
            {
                var u = NormalizeImageUrl(GetString(g, "main") ?? GetString(g, "large"));
                if (!string.IsNullOrWhiteSpace(u) && !gallery.Contains(u))
                    gallery.Add(u);
            }
        }
        else if (el.TryGetProperty("allMainImage", out var allMainEl) && allMainEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in allMainEl.EnumerateArray())
            {
                var u = NormalizeImageUrl(GetString(g, "url"));
                if (!string.IsNullOrWhiteSpace(u) && !gallery.Contains(u))
                    gallery.Add(u);
            }
        }

        var primaryImage = gallery.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryImage) && el.TryGetProperty("mainImage", out var mainImgEl))
        {
            primaryImage = NormalizeImageUrl(GetString(mainImgEl, "url"));
        }
        if (string.IsNullOrWhiteSpace(primaryImage) && el.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array && imgsEl.GetArrayLength() > 0)
        {
            primaryImage = NormalizeImageUrl(GetString(imgsEl[0], "url"));
        }

        var sizes = new List<string>();
        if (el.TryGetProperty("allSizeVariants", out var sizesEl) && sizesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sizesEl.EnumerateArray())
            {
                var sizeName = GetString(s, "size");
                var lengthName = GetString(s, "length");
                var stockStatus = GetString(s, "stockLevelStatus");
                if (!string.IsNullOrWhiteSpace(sizeName) && stockStatus == "inStock")
                {
                    var label = !string.IsNullOrWhiteSpace(lengthName) ? $"{sizeName}/{lengthName}" : sizeName;
                    if (!sizes.Contains(label))
                    {
                        sizes.Add(label);
                    }
                }
            }
        }

        var relUrl = GetString(el, "url") ?? $"/p/{code}";
        var fullProductUrl = relUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relUrl
            : $"{MaviWebBase}{(relUrl.StartsWith('/') ? relUrl : "/" + relUrl)}";

        var color = el.TryGetProperty("colour", out var colEl) ? GetString(colEl, "name") : null;
        var fit = el.TryGetProperty("fit", out var fitEl) ? GetString(fitEl, "name") : GetString(el, "fitAndWaist");
        var material = GetString(el, "materialContent");
        var description = GetString(el, "description");

        return new ClothingItem
        {
            Id = code,
            Name = name,
            Brand = StoreName,
            Category = category,
            ImageUrl = primaryImage ?? string.Empty,
            GalleryImages = gallery,
            Description = description,
            Color = color,
            Fit = fit,
            Material = material,
            AvailableSizes = sizes,
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = StoreName,
                    Price = effectivePrice,
                    OriginalPrice = originalPrice,
                    DiscountRate = discountRate,
                    Url = fullProductUrl
                }
            }
        };
    }

    private static string NormalizeImageUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return string.Empty;

        var trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("//"))
            return "https:" + trimmed;

        return trimmed;
    }

    private static string? GetString(JsonElement el, string propName)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, string parentProp, string valueProp)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(parentProp, out var parent) &&
            parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(valueProp, out var valEl) &&
            valEl.ValueKind == JsonValueKind.Number &&
            valEl.TryGetDecimal(out var d))
        {
            return d;
        }
        return null;
    }
}
