using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingPriceTracker.Api.Models;
using ClothingPriceTracker.Api.Services.Abstractions;

namespace ClothingPriceTracker.Api.Services.Scrapers;

/// <summary>
/// LC Waikiki (lcw.com) mağaza adaptörü.
/// - Kategori listesi: robots.txt'de izinli kategori sayfalarındaki (örn. /erkek-tisort-t-345)
///   sunucu tarafında gömülü "CatalogList" JSON verisi okunur.
/// - Ürün detayı: ürün sayfasındaki resmi Schema.org (application/ld+json) Product verisi okunur.
/// Kimlik gizlenmez; uygulama kendi adıyla (User-Agent) istek atar. Arama (/arama?q=) robots.txt'de
/// yasak olduğu için kullanılmaz.
/// Ürün kimlikleri Mavi kodlarıyla karışmasın diye "lcw-{optionId}" formatındadır.
/// </summary>
public class LcwStoreScraper : IStoreScraper
{
    public string StoreName => "LC Waikiki";

    private const string BaseUrl = "https://www.lcw.com";
    private const string IdPrefix = "lcw-";
    private const string UserAgent = "ClothingPriceTracker/1.0 (student project)";

    // Uygulamadaki kategori sorguları -> LCW erkek kategori sayfaları
    private static readonly Dictionary<string, (string Path, string Category)> CategoryMap = new()
    {
        ["tisort"] = ("/erkek-tisort-t-345", "Tişört"),
        ["jean"] = ("/erkek-jean-t-194", "Jean"),
        ["sweatshirt"] = ("/erkek-sweatshirt-t-300", "Sweatshirt"),
        ["gomlek"] = ("/erkek-gomlek-t-158", "Gömlek"),
        ["ceket"] = ("/erkek-ceket-t-130", "Ceket"),
    };

    // Liste ve detay sonuçları kısa süre bellekte tutulur (siteye gereksiz yük bindirmemek için)
    private static readonly ConcurrentDictionary<string, (DateTime At, List<ClothingItem> Items)> ListCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, ClothingItem Item)> DetailCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;

    public LcwStoreScraper(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9");
    }

    public bool CanHandle(string urlOrCode)
    {
        if (string.IsNullOrWhiteSpace(urlOrCode))
            return false;

        var trimmed = urlOrCode.Trim();
        return trimmed.Contains("lcw.com", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ClothingItem?> GetProductByUrlOrCodeAsync(string urlOrCode)
    {
        var optionId = ExtractOptionId(urlOrCode);
        if (optionId == null)
            return null;

        var id = IdPrefix + optionId;
        if (DetailCache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < CacheDuration)
            return cached.Item;

        // LCW, "/herhangi-bir-slug-o-{id}" adresini doğru ürün sayfasına yönlendirir
        var html = await FetchHtmlAsync($"{BaseUrl}/urun-o-{optionId}");
        if (html == null)
            return null;

        var item = ParseProductDetail(html, id);
        if (item != null)
            DetailCache[id] = (DateTime.UtcNow, item);

        return item;
    }

    public async Task<List<ClothingItem>> SearchProductsAsync(string query = "tisort", int pageSize = 6)
    {
        var key = NormalizeTurkish(query);
        var (path, category) = CategoryMap.FirstOrDefault(kv => key.Contains(kv.Key) || kv.Key.Contains(key)).Value;
        if (path == null)
            (path, category) = CategoryMap["tisort"];

        if (!ListCache.TryGetValue(path, out var cached) || DateTime.UtcNow - cached.At >= CacheDuration)
        {
            var html = await FetchHtmlAsync(BaseUrl + path);
            var items = html == null ? new List<ClothingItem>() : ParseCatalogList(html, category);
            if (items.Count > 0)
                ListCache[path] = (DateTime.UtcNow, items);
            cached = (DateTime.UtcNow, items);
        }

        return cached.Items.Take(pageSize).ToList();
    }

    private async Task<string?> FetchHtmlAsync(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Kategori sayfasına gömülü "CatalogList":{"Items":[...]} JSON dizisini ayıklar.
    /// </summary>
    private List<ClothingItem> ParseCatalogList(string html, string category)
    {
        var result = new List<ClothingItem>();
        var marker = html.IndexOf("\"CatalogList\":{\"Items\":[", StringComparison.Ordinal);
        if (marker < 0)
            return result;

        var arrayJson = ExtractJsonBlock(html, html.IndexOf('[', marker));
        if (arrayJson == null)
            return result;

        try
        {
            using var doc = JsonDocument.Parse(arrayJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var optionId = GetLong(el, "OptionId");
                var name = GetString(el, "ProductDescription");
                var image = GetString(el, "DefaultOptionImageUrl");
                var modelUrl = GetString(el, "ModelUrl");
                var price = GetDecimal(el, "PriceValue");
                if (optionId <= 0 || string.IsNullOrWhiteSpace(name) || price <= 0)
                    continue;

                // Ürün üzerinde gerçek indirim varsa (sepet kampanyaları hariç)
                var discounted = GetDecimal(el, "DiscountedPriceValue");
                var hasDiscount = discounted > 0 && discounted < price;

                var gallery = new List<string>();
                if (el.TryGetProperty("OptionImageUrlList", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                    gallery.AddRange(imgs.EnumerateArray().Select(i => i.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))!);
                if (gallery.Count == 0 && !string.IsNullOrWhiteSpace(image))
                    gallery.Add(image);

                result.Add(new ClothingItem
                {
                    Id = IdPrefix + optionId,
                    Name = name,
                    Brand = StoreName,
                    Category = category,
                    Color = ColorFromUrl(modelUrl),
                    ImageUrl = image ?? gallery.FirstOrDefault() ?? string.Empty,
                    GalleryImages = gallery,
                    Offers = new List<ClothingOffer>
                    {
                        new()
                        {
                            StoreName = StoreName,
                            Price = hasDiscount ? discounted : price,
                            OriginalPrice = hasDiscount ? price : null,
                            DiscountRate = hasDiscount ? (int)Math.Round((1 - discounted / price) * 100) : null,
                            Url = BaseUrl + modelUrl
                        }
                    }
                });
            }
        }
        catch
        {
            // Sayfa yapısı değiştiyse boş liste döner; diğer mağazalar etkilenmez
        }

        return result;
    }

    /// <summary>
    /// Ürün sayfasındaki Schema.org Product (application/ld+json) verisinden ürünü oluşturur.
    /// </summary>
    private ClothingItem? ParseProductDetail(string html, string id)
    {
        foreach (Match m in Regex.Matches(html, @"<script[^>]*application/ld\+json[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                var root = doc.RootElement;
                if (GetString(root, "@type") != "Product")
                    continue;

                var name = GetString(root, "name") ?? string.Empty;
                var gallery = new List<string>();
                if (root.TryGetProperty("image", out var img))
                {
                    if (img.ValueKind == JsonValueKind.Array)
                        gallery.AddRange(img.EnumerateArray().Select(i => i.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))!);
                    else if (img.ValueKind == JsonValueKind.String)
                        gallery.Add(img.GetString()!);
                }

                decimal price = 0;
                var url = string.Empty;
                if (root.TryGetProperty("offers", out var offers))
                {
                    var offer = offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().FirstOrDefault() : offers;
                    if (offer.ValueKind == JsonValueKind.Object)
                    {
                        price = GetDecimal(offer, "price");
                        url = GetString(offer, "url") ?? string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(name) || price <= 0)
                    return null;

                var sizes = new List<string>();
                if (root.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Array)
                    sizes.AddRange(sizeEl.EnumerateArray().Select(s => s.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))!);

                string? material = null;
                if (root.TryGetProperty("material", out var matEl))
                    material = matEl.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", matEl.EnumerateArray().Select(x => x.GetString()))
                        : matEl.GetString();

                string? fit = null;
                if (root.TryGetProperty("additionalProperty", out var props) && props.ValueKind == JsonValueKind.Array)
                    fit = props.EnumerateArray()
                        .Where(p => GetString(p, "name") == "Kalıp")
                        .Select(p => GetString(p, "value"))
                        .FirstOrDefault();

                var category = GetString(root, "category") ?? string.Empty;

                return new ClothingItem
                {
                    Id = id,
                    Name = name,
                    Brand = StoreName,
                    Category = category.Split('>').LastOrDefault()?.Trim() ?? category,
                    Color = GetString(root, "color"),
                    Fit = fit,
                    Material = material,
                    Description = WebUtility.HtmlDecode(GetString(root, "description") ?? string.Empty),
                    ImageUrl = gallery.FirstOrDefault() ?? string.Empty,
                    GalleryImages = gallery,
                    AvailableSizes = sizes,
                    Offers = new List<ClothingOffer>
                    {
                        new() { StoreName = StoreName, Price = price, Url = url }
                    }
                };
            }
            catch
            {
                // Bu script bloğu geçerli JSON değil; sıradakine bak
            }
        }

        return null;
    }

    private static string? ExtractOptionId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        var prefixed = Regex.Match(trimmed, @"^lcw-(\d+)$", RegexOptions.IgnoreCase);
        if (prefixed.Success)
            return prefixed.Groups[1].Value;

        // Örn: https://www.lcw.com/polo-yaka-pike-erkek-tisort-siyah-o-5263116
        var fromUrl = Regex.Match(trimmed, @"-o-(\d+)", RegexOptions.IgnoreCase);
        return fromUrl.Success ? fromUrl.Groups[1].Value : null;
    }

    /// <summary>URL sonundaki renk kelimesini çıkarır: ".../polo-yaka-tisort-siyah-o-123" -> "Siyah"</summary>
    private static string? ColorFromUrl(string? modelUrl)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            return null;
        var m = Regex.Match(modelUrl, @"-([a-z]+)-o-\d+$");
        if (!m.Success)
            return null;
        var c = m.Groups[1].Value;
        return char.ToUpper(c[0], new CultureInfo("tr-TR")) + c[1..];
    }

    /// <summary>Verilen konumdaki [ veya { ile başlayan JSON bloğunu, string içlerini dikkate alarak keser.</summary>
    private static string? ExtractJsonBlock(string text, int start)
    {
        if (start < 0)
            return null;

        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '[' || c == '{') depth++;
            else if (c == ']' || c == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static decimal GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
            return s;
        return 0;
    }

    private static string NormalizeTurkish(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return input.Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u')
            .Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');
    }
}
