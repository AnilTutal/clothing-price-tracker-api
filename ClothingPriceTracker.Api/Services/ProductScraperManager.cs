using ClothingPriceTracker.Api.Models;
using ClothingPriceTracker.Api.Services.Abstractions;

namespace ClothingPriceTracker.Api.Services;

/// <summary>
/// Tüm mağaza scraper servislerini (Mavi, Bershka vb.) tek bir merkezden yöneten ortak servis.
/// Yeni bir marka eklendiğinde bu sınıfa dokunmaya gerek kalmaz; DI üzerinden otomatik dahil olur.
/// </summary>
public class ProductScraperManager
{
    private readonly IEnumerable<IStoreScraper> _scrapers;
    private readonly PriceHistoryService _priceHistory;

    public ProductScraperManager(IEnumerable<IStoreScraper> scrapers, PriceHistoryService priceHistory)
    {
        _scrapers = scrapers;
        _priceHistory = priceHistory;
    }

    public IReadOnlyList<string> GetSupportedStores()
    {
        return _scrapers.Select(s => s.StoreName).ToList();
    }

    /// <summary>
    /// Verilen URL veya ürün kodunun hangi mağazaya ait olduğunu otomatik tespit edip ürünü çeker.
    /// </summary>
    public async Task<ClothingItem?> GetProductByUrlOrCodeAsync(string urlOrCode)
    {
        if (string.IsNullOrWhiteSpace(urlOrCode))
            return null;

        // 1. Önce CanHandle(urlOrCode) true dönen mağaza adaptörünü bul
        var matchingScraper = _scrapers.FirstOrDefault(s => s.CanHandle(urlOrCode));
        if (matchingScraper != null)
        {
            var item = await matchingScraper.GetProductByUrlOrCodeAsync(urlOrCode);
            if (item != null)
            {
                await _priceHistory.RecordAsync(new[] { item });
                return item;
            }
        }

        // 2. Eşleşen bulunamazsa sırayla tüm mağaza adaptörlerinde dene
        foreach (var scraper in _scrapers)
        {
            if (scraper == matchingScraper) continue;
            var item = await scraper.GetProductByUrlOrCodeAsync(urlOrCode);
            if (item != null)
            {
                await _priceHistory.RecordAsync(new[] { item });
                return item;
            }
        }

        return null;
    }

    private static readonly string[] AllDefaultCategories = { "tisort", "jean", "sweatshirt", "gomlek", "ceket" };

    /// <summary>
    /// Seçili mağazada veya tüm mağazalarda paralel arama yapıp sonuçları tek listede birleştirir.
    /// query = "all" verilirse seçili mağazanın tüm kategorilerdeki bütün ürünlerini tek seferde çeker.
    /// </summary>
    public async Task<List<ClothingItem>> SearchProductsAsync(string query = "tisort", string? store = null, int pageSize = 8)
    {
        var activeScrapers = _scrapers.ToList();

        if (!string.IsNullOrWhiteSpace(store) && !store.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            activeScrapers = activeScrapers
                .Where(s => s.StoreName.Equals(store.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (activeScrapers.Count == 0)
            return new List<ClothingItem>();

        // "all" veya "tumu" sorgusunda seçili mağazanın TÜM kategorilerdeki bütün ürünlerini paralel çek
        if (string.Equals(query?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query?.Trim(), "tumu", StringComparison.OrdinalIgnoreCase))
        {
            var perCategoryLimit = Math.Max(pageSize, 60);
            var categoryTasks = new List<Task<List<ClothingItem>>>();

            foreach (var scraper in activeScrapers)
            {
                foreach (var cat in AllDefaultCategories)
                {
                    categoryTasks.Add(scraper.SearchProductsAsync(cat, perCategoryLimit));
                }
            }

            var categoryResults = await Task.WhenAll(categoryTasks);
            var allItems = categoryResults
                .SelectMany(x => x)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            await _priceHistory.RecordAsync(allItems);
            return allItems;
        }

        var perStoreLimit = activeScrapers.Count == 1
            ? pageSize
            : Math.Max(3, (int)Math.Ceiling((double)pageSize / activeScrapers.Count));

        var tasks = activeScrapers.Select(s => s.SearchProductsAsync(query, perStoreLimit));
        var results = await Task.WhenAll(tasks);

        // Mağaza sonuçlarını dengeli (interleaved: 1 Mavi, 1 Bershka...) şekilde birleştir
        var combined = new List<ClothingItem>();
        var maxCount = results.Max(r => r.Count);
        for (int i = 0; i < maxCount; i++)
        {
            foreach (var storeList in results)
            {
                if (i < storeList.Count)
                {
                    combined.Add(storeList[i]);
                }
            }
        }

        await _priceHistory.RecordAsync(combined);
        return combined;
    }
}
