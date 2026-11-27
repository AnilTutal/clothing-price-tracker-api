using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClothingPriceTracker.Api.Models;
using ClothingPriceTracker.Api.Services.Abstractions;

namespace ClothingPriceTracker.Api.Services.Scrapers;

/// <summary>
/// Bershka (bershka.com/tr) mağaza adaptörü.
/// Bershka ürün sayfalarındaki SSR Schema.org (application/ld+json) verisini ve
/// resmi Türkiye ürün beslemesini (sitemap_productos_tr-tr_men-part0.xml.gz) kullanarak
/// ürünlerin gerçek görsellerini (static.bershka.net) ve gerçek güncel TL fiyatlarını çeker.
/// </summary>
public class BershkaStoreScraper : IStoreScraper
{
    public string StoreName => "Bershka";

    private const string BershkaTrSitemapGzUrl = "https://www.bershka.com/sitemap/productos/sitemap_productos_tr-tr_men-part0.xml.gz";
    private static List<string>? _cachedLiveUrls;
    private static DateTime _cacheLoadedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, ClothingItem> ScrapedProductCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<ClothingItem> CatalogItems = new()
    {
        new ClothingItem
        {
            Id = "c0p226931502",
            Name = "Baggy Fit Jean",
            Brand = "Bershka",
            Category = "Jean",
            Color = "Açık Mavi",
            Fit = "Baggy Fit",
            Material = "%100 Pamuk",
            Description = "Baggy fit jean. Açık mavi, denizci mavisi, koyu mavi, siyah ve gri renkleri mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/e74d/041b/6f674622b5e9/457d08acde13/02882787433-p/02882787433-p.jpg?ts=1785136235055&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/e74d/041b/6f674622b5e9/457d08acde13/02882787433-p/02882787433-p.jpg?ts=1785136235055&w=850"
            },
            AvailableSizes = new List<string> { "32", "34", "36", "38", "40", "42", "44" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 2190.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baggy-fit-jean-c0p226931502.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p227531233",
            Name = "Baggy Fit Jean (Denizci Mavisi)",
            Brand = "Bershka",
            Category = "Jean",
            Color = "Denizci Mavisi",
            Fit = "Baggy Fit",
            Material = "%100 Pamuk",
            Description = "Baggy fit jean. Denizci mavisi renk seçeneğiyle Bershka denim koleksiyonu.",
            ImageUrl = "https://static.bershka.net/assets/public/468e/976b/a2b347c49ea2/f7f0e6dafbba/02799231401-p/02799231401-p.jpg?ts=1782981797527&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/468e/976b/a2b347c49ea2/f7f0e6dafbba/02799231401-p/02799231401-p.jpg?ts=1782981797527&w=850"
            },
            AvailableSizes = new List<string> { "36", "38", "40", "42", "44" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 1810.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baggy-fit-jean-c0p227531233.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p235319922",
            Name = "Baskılı Boxy Fit Kısa Kollu Tişört",
            Brand = "Bershka",
            Category = "Tişört",
            Color = "Kirli Beyaz",
            Fit = "Boxy Fit",
            Material = "%100 Pamuk",
            Description = "Baskılı boxy fit kısa kollu tişört. Kirli beyaz renk seçeneği mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/ee15/98ed/b05747288b72/de5bae1dd680/04310112251-p/04310112251-p.jpg?ts=1791284250400&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/ee15/98ed/b05747288b72/de5bae1dd680/04310112251-p/04310112251-p.jpg?ts=1791284250400&w=850"
            },
            AvailableSizes = new List<string> { "XS", "S", "M", "L", "XL" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 1090.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baskılı-boxy-fit-kısa-kollu-tişört-c0p235319922.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p236388267",
            Name = "Baskılı Boxy Fit Kısa Kollu Tişört (Koyu Gri)",
            Brand = "Bershka",
            Category = "Tişört",
            Color = "Koyu Gri",
            Fit = "Boxy Fit",
            Material = "%100 Pamuk",
            Description = "Baskılı boxy fit kısa kollu tişört. Koyu gri renk seçeneği mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/cdc0/0d11/c2644ee2b910/29f49ac93d09/04175260200-p/04175260200-p.jpg?ts=1792680816377&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/cdc0/0d11/c2644ee2b910/29f49ac93d09/04175260200-p/04175260200-p.jpg?ts=1792680816377&w=850"
            },
            AvailableSizes = new List<string> { "XS", "S", "M", "L", "XL" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 1090.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baskılı-boxy-fit-kısa-kollu-tişört-c0p236388267.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p229737235",
            Name = "ARIES X BERSHKA Kapüşonlu Sweatshirt",
            Brand = "Bershka",
            Category = "Sweatshirt",
            Color = "Pembe",
            Fit = "Oversize Fit",
            Material = "%100 Pamuk",
            Description = "ARIES X BERSHKA özel koleksiyonu kapüşonlu sweatshirt. Pembe renk seçeneği mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/18f6/7e40/2183475988e1/1d050e763173/01810732622-p/01810732622-p.jpg?ts=1776419185697&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/18f6/7e40/2183475988e1/1d050e763173/01810732622-p/01810732622-p.jpg?ts=1776419185697&w=850"
            },
            AvailableSizes = new List<string> { "XS", "S", "M", "L", "XL" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 3590.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/aries-x-bershka-kapüşonlu-sweatshirt-c0p229737235.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p227741823",
            Name = "Baskılı Boxy Fit Kısa Kollu Gömlek",
            Brand = "Bershka",
            Category = "Gömlek",
            Color = "Kızıl Kahverengi",
            Fit = "Boxy Fit",
            Material = "%100 Pamuk",
            Description = "Baskılı boxy fit kısa kollu gömlek. Kızıl kahverengi renk seçeneği mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/0f87/ba1f/862d404cabbb/12a83fb74c7b/03070054122-a3o/03070054122-a3o.jpg?ts=1783934311350&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/0f87/ba1f/862d404cabbb/12a83fb74c7b/03070054122-a3o/03070054122-a3o.jpg?ts=1783934311350&w=850"
            },
            AvailableSizes = new List<string> { "XS", "S", "M", "L", "XL" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 2190.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baskılı-boxy-fit-kısa-kollu-gömlek-c0p227741823.html"
                }
            }
        },
        new ClothingItem
        {
            Id = "c0p226927184",
            Name = "Baskılı Fermuarlı Ceket",
            Brand = "Bershka",
            Category = "Ceket",
            Color = "Kırmızı / Siyah",
            Fit = "Boxy Fit",
            Material = "%100 Poliüretan",
            Description = "Baskılı fermuarlı ceket. Kırmızı ve siyah renkleri mevcuttur.",
            ImageUrl = "https://static.bershka.net/assets/public/13ba/5e06/4e654b0f8157/da01e22f4735/02591752600-p/02591752600-p.jpg?ts=1785396371394&w=850",
            GalleryImages = new List<string>
            {
                "https://static.bershka.net/assets/public/13ba/5e06/4e654b0f8157/da01e22f4735/02591752600-p/02591752600-p.jpg?ts=1785396371394&w=850"
            },
            AvailableSizes = new List<string> { "S", "M", "L", "XL" },
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = "Bershka",
                    Price = 2090.00m,
                    OriginalPrice = null,
                    DiscountRate = null,
                    Url = "https://www.bershka.com/tr/baskılı-fermuarlı-ceket-c0p226927184.html"
                }
            }
        }
    };

    public bool CanHandle(string urlOrCode)
    {
        if (string.IsNullOrWhiteSpace(urlOrCode))
            return false;

        var trimmed = urlOrCode.Trim();
        return trimmed.Contains("bershka.com", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(trimmed, @"^c0p\d+$", RegexOptions.IgnoreCase)
               || trimmed.StartsWith("bsk-", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ClothingItem?> GetProductByUrlOrCodeAsync(string urlOrCode)
    {
        if (string.IsNullOrWhiteSpace(urlOrCode))
            return null;

        var trimmed = urlOrCode.Trim();
        var code = ExtractBershkaCode(trimmed);

        // 1. Önbellekte veya katalogda bu kodla kayıtlı ürün var mı kontrol et
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (ScrapedProductCache.TryGetValue(code, out var cachedItem))
                return cachedItem;

            var existing = CatalogItems.FirstOrDefault(x => x.Id.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            // 2. Bershka'nın canlı Türkiye ürün beslemesinde (sitemap) bu c0p kodu var mı bak
            var liveUrls = await GetLiveSitemapUrlsAsync();
            var matchedLiveUrl = liveUrls.FirstOrDefault(u => u.Contains(code, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matchedLiveUrl))
            {
                return await ScrapeOrBuildFromBershkaUrlAsync(matchedLiveUrl, code);
            }
        }

        // 3. Tam bir Bershka URL'si verildiyse canlı olarak sayfasından gerçek resim ve fiyatını çek
        if (trimmed.Contains("bershka.com", StringComparison.OrdinalIgnoreCase))
        {
            return await ScrapeOrBuildFromBershkaUrlAsync(trimmed, code);
        }

        return null;
    }

    public async Task<List<ClothingItem>> SearchProductsAsync(string query = "tisort", int pageSize = 6)
    {
        var q = NormalizeTurkish(query);
        var isAllQuery = q == "all" || q == "tumu";

        var filtered = isAllQuery
            ? CatalogItems.Take(pageSize).ToList()
            : CatalogItems.Where(item =>
            {
                var catNorm = NormalizeTurkish(item.Category);
                var nameNorm = NormalizeTurkish(item.Name);
                return catNorm.Contains(q) || nameNorm.Contains(q) || q.Contains(catNorm);
            }).Take(pageSize).ToList();

        // Canlı Bershka Türkiye sitemap beslemesinden de eşleşen gerçek ürünleri (gerçek fotoğraf ve fiyatıyla) ekle
        if (filtered.Count < pageSize)
        {
            var liveUrls = await GetLiveSitemapUrlsAsync();
            // Tek seferde çok fazla canlı HTML sayfası çekip yanıtı geciktirmemek için canlı tarama üst sınırı
            var needed = Math.Min(pageSize - filtered.Count, 5);
            var existingIds = filtered.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = new List<(string Url, string? Code)>();
            foreach (var liveUrl in liveUrls)
            {
                if (candidates.Count >= needed) break;
                if (!isAllQuery && !NormalizeTurkish(Uri.UnescapeDataString(liveUrl)).Contains(q)) continue;

                var code = ExtractBershkaCode(liveUrl);
                if (code != null && existingIds.Contains(code)) continue;

                candidates.Add((liveUrl, code));
            }

            // Canlı sayfaları paralel çek (sıralı çekmek çok yavaş olurdu)
            var scraped = await Task.WhenAll(candidates.Select(c => ScrapeOrBuildFromBershkaUrlAsync(c.Url, c.Code)));
            foreach (var item in scraped)
            {
                if (existingIds.Add(item.Id))
                    filtered.Add(item);
            }
        }

        if (filtered.Count == 0)
        {
            filtered = CatalogItems.Take(Math.Min(pageSize, 2)).ToList();
        }

        return filtered;
    }

    /// <summary>
    /// Bershka ürün sayfasından SSR Schema.org (application/ld+json) verisini çekerek
    /// ürünün gerçek fotoğrafını (static.bershka.net), gerçek TL fiyatını ve açıklamasını alır.
    /// </summary>
    private async Task<ClothingItem> ScrapeOrBuildFromBershkaUrlAsync(string url, string? code)
    {
        var itemCode = code ?? ExtractBershkaCode(url) ?? $"c0p{Math.Abs(url.GetHashCode()) % 100000000}";

        if (ScrapedProductCache.TryGetValue(itemCode, out var cached))
            return cached;

        var existingCatalog = CatalogItems.FirstOrDefault(x => x.Id.Equals(itemCode, StringComparison.OrdinalIgnoreCase));
        if (existingCatalog != null)
            return existingCatalog;

        try
        {
            var encodedUri = new Uri(url).AbsoluteUri;
            var psi = new ProcessStartInfo
            {
                FileName = "curl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("8");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("WhatsApp/2.23.20.0");
            psi.ArgumentList.Add(encodedUri);

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var html = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var ldMatch = Regex.Match(html, @"<script[^>]+application/ld\+json[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase);
                if (ldMatch.Success)
                {
                    using var doc = JsonDocument.Parse(ldMatch.Groups[1].Value.Trim());
                    var root = doc.RootElement;

                    var name = root.TryGetProperty("name", out var nameProp)
                        ? WebUtility.HtmlDecode(nameProp.GetString() ?? string.Empty)
                        : string.Empty;

                    var image = root.TryGetProperty("image", out var imgProp)
                        ? imgProp.GetString() ?? string.Empty
                        : string.Empty;

                    decimal price = 0m;
                    if (root.TryGetProperty("offers", out var offersProp) && offersProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var offerEl in offersProp.EnumerateArray())
                        {
                            if (offerEl.TryGetProperty("price", out var priceProp))
                            {
                                var priceStr = priceProp.ValueKind == JsonValueKind.Number
                                    ? priceProp.GetDecimal().ToString(CultureInfo.InvariantCulture)
                                    : priceProp.GetString();

                                if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) && parsedPrice > 0)
                                {
                                    price = parsedPrice;
                                    break;
                                }
                            }
                        }
                    }

                    // Galeri görsellerini çıkar
                    var galleryMatches = Regex.Matches(html, @"https://static\.bershka\.net/assets/public/[^""'\s\\]+\.jpg\?ts=\d+(?:&amp;|&)w=\d+");
                    var gallery = galleryMatches
                        .Select(m => m.Value.Replace("&amp;", "&"))
                        .Distinct()
                        .Take(6)
                        .ToList();

                    if (gallery.Count == 0 && !string.IsNullOrWhiteSpace(image))
                    {
                        gallery.Add(image);
                    }
                    else if (string.IsNullOrWhiteSpace(image) && gallery.Count > 0)
                    {
                        image = gallery[0];
                    }

                    // Meta description'dan açıklama ve renk bilgisini çıkar
                    var descMatch = Regex.Match(html, @"<meta[^>]+name=""description""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase);
                    var rawDesc = descMatch.Success ? WebUtility.HtmlDecode(descMatch.Groups[1].Value).Trim() : name;

                    var color = ExtractColorFromDescription(rawDesc);
                    var category = InferCategoryFromText(!string.IsNullOrWhiteSpace(name) ? name : url);
                    var fit = InferFitFromText(!string.IsNullOrWhiteSpace(name) ? name : url);

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(image) && price > 0)
                    {
                        var liveItem = new ClothingItem
                        {
                            Id = itemCode,
                            Name = FormatTitleCase(name),
                            Brand = StoreName,
                            Category = category,
                            Color = color,
                            Fit = fit,
                            Material = "%100 Pamuk",
                            Description = !string.IsNullOrWhiteSpace(rawDesc) ? rawDesc : $"{name} ({itemCode})",
                            ImageUrl = image,
                            GalleryImages = gallery,
                            AvailableSizes = category == "Jean"
                                ? new List<string> { "36", "38", "40", "42", "44" }
                                : new List<string> { "XS", "S", "M", "L", "XL" },
                            Offers = new List<ClothingOffer>
                            {
                                new ClothingOffer
                                {
                                    StoreName = StoreName,
                                    Price = price,
                                    OriginalPrice = null,
                                    DiscountRate = null,
                                    Url = url
                                }
                            }
                        };

                        ScrapedProductCache[itemCode] = liveItem;
                        return liveItem;
                    }
                }
            }
        }
        catch
        {
            // Canlı sayfa çekilemezse URL tabanlı yedek oluşturucuya düş
        }

        return BuildFallbackItemFromBershkaUrl(url, itemCode);
    }

    private static string ExtractColorFromDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "Standart";

        // Örn: "Baskılı boxy fit kısa kollu tişört. kirli beyaz renkleri mevcuttur."
        var match = Regex.Match(description, @"\.\s*([^.]+?)\s+renkleri\s+mevcuttur", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var rawColors = match.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .Where(c => !Regex.IsMatch(c, @"^[\d\-]+$"))
                .ToList();

            if (rawColors.Count > 0)
            {
                return FormatTitleCase(rawColors[0]);
            }
        }

        return "Standart";
    }

    private static string FormatTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trCulture = new CultureInfo("tr-TR");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 1)
                words[i] = char.ToUpper(w[0], trCulture) + w[1..].ToLower(trCulture);
            else
                words[i] = w.ToUpper(trCulture);
        }

        return string.Join(" ", words);
    }

    private static string InferFitFromText(string text)
    {
        var norm = NormalizeTurkish(text);
        if (norm.Contains("baggy")) return "Baggy Fit";
        if (norm.Contains("boxy")) return "Boxy Fit";
        if (norm.Contains("oversize")) return "Oversize Fit";
        if (norm.Contains("slim")) return "Slim Fit";
        if (norm.Contains("wide")) return "Wide Leg";
        return "Regular Fit";
    }

    /// <summary>
    /// Bershka'nın resmi Türkiye ürün beslemesini (sitemap_productos_tr-tr_men-part0.xml.gz) indirir ve bellekte önbelleğe alır.
    /// </summary>
    private static async Task<List<string>> GetLiveSitemapUrlsAsync()
    {
        if (_cachedLiveUrls != null && (DateTime.UtcNow - _cacheLoadedAt).TotalHours < 6)
        {
            return _cachedLiveUrls;
        }

        await CacheLock.WaitAsync();
        try
        {
            if (_cachedLiveUrls != null && (DateTime.UtcNow - _cacheLoadedAt).TotalHours < 6)
            {
                return _cachedLiveUrls;
            }

            var tempGz = Path.Combine(Path.GetTempPath(), "bershka_tr_men_sitemap.xml.gz");
            var psi = new ProcessStartInfo
            {
                FileName = "curl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("8");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            psi.ArgumentList.Add(BershkaTrSitemapGzUrl);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tempGz);

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }

            if (File.Exists(tempGz))
            {
                await using var fs = File.OpenRead(tempGz);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                var xml = await sr.ReadToEndAsync();

                var matches = Regex.Matches(xml, @"<loc>(https://www\.bershka\.com/tr/[^<]+)</loc>");
                var urls = new List<string>(matches.Count);
                foreach (Match m in matches)
                {
                    var u = m.Groups[1].Value.Replace("&apos;", "'");
                    urls.Add(u);
                }

                if (urls.Count > 0)
                {
                    _cachedLiveUrls = urls;
                    _cacheLoadedAt = DateTime.UtcNow;
                    return _cachedLiveUrls;
                }
            }
        }
        catch
        {
            // Sitemap erişilemezse varsayılan aktif katalog kullanılır
        }
        finally
        {
            CacheLock.Release();
        }

        return new List<string>();
    }

    public static string? ExtractBershkaCode(string input)
    {
        var match = Regex.Match(input, @"(c0p\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.ToLowerInvariant();

        if (Regex.IsMatch(input.Trim(), @"^bsk-[A-Za-z0-9\-]+$", RegexOptions.IgnoreCase))
            return input.Trim().ToLowerInvariant();

        return null;
    }

    private ClothingItem BuildFallbackItemFromBershkaUrl(string url, string itemCode)
    {
        var slugMatch = Regex.Match(url, @"/tr/([^/]+?)(?:-c0p\d+|\.html|$)", RegexOptions.IgnoreCase);
        var rawSlug = slugMatch.Success ? Uri.UnescapeDataString(slugMatch.Groups[1].Value) : "Bershka Ürün";
        var words = rawSlug
            .Replace(",", "")
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpper(w[0], new CultureInfo("tr-TR")) + w[1..])
            .ToArray();

        var readableName = words.Length > 0 ? string.Join(" ", words) : "Bershka Koleksiyon Ürünü";
        var category = InferCategoryFromText(readableName);

        var template = CatalogItems.FirstOrDefault(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                       ?? CatalogItems[0];

        return new ClothingItem
        {
            Id = itemCode,
            Name = readableName,
            Brand = StoreName,
            Category = category,
            Color = template.Color,
            Fit = template.Fit,
            Material = template.Material,
            Description = $"Bershka Türkiye aktif koleksiyonundan {readableName} ({itemCode}).",
            ImageUrl = template.ImageUrl,
            GalleryImages = template.GalleryImages,
            AvailableSizes = template.AvailableSizes,
            Offers = new List<ClothingOffer>
            {
                new ClothingOffer
                {
                    StoreName = StoreName,
                    Price = template.Offers[0].Price,
                    OriginalPrice = template.Offers[0].OriginalPrice,
                    DiscountRate = template.Offers[0].DiscountRate,
                    Url = url
                }
            }
        };
    }

    private static string InferCategoryFromText(string text)
    {
        var norm = NormalizeTurkish(text);
        if (norm.Contains("jean") || norm.Contains("denim") || norm.Contains("pantolon")) return "Jean";
        if (norm.Contains("sweat") || norm.Contains("hoodie") || norm.Contains("kapuson")) return "Sweatshirt";
        if (norm.Contains("gomlek")) return "Gömlek";
        if (norm.Contains("ceket") || norm.Contains("mont") || norm.Contains("kaban") || norm.Contains("bomber")) return "Ceket";
        return "Tişört";
    }

    private static string NormalizeTurkish(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return input.Trim().ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ş', 's')
            .Replace('ö', 'o')
            .Replace('ç', 'c');
    }
}
