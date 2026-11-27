using ClothingPriceTracker.Api.Data;
using ClothingPriceTracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClothingPriceTracker.Api.Services;

/// <summary>
/// Çekilen ürünlerin fiyatlarını veritabanına kaydeder ve fiyat geçmişini okur.
/// </summary>
public class PriceHistoryService
{
    // Fiyat değişmediyse bile en az bu aralıkta bir nokta ekle (grafik için)
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);

    private readonly AppDbContext _db;
    private readonly ILogger<PriceHistoryService> _logger;

    public PriceHistoryService(AppDbContext db, ILogger<PriceHistoryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Ürünün güncel fiyatını kaydeder. Fiyat değiştiyse veya son kayıt eskiyse yeni nokta ekler.
    /// Hata olursa ürün akışını bozmaz.
    /// </summary>
    public async Task RecordAsync(IEnumerable<ClothingItem> items)
    {
        try
        {
            var added = false;
            foreach (var item in items)
            {
                var offer = item.Offers.FirstOrDefault();
                if (offer == null || offer.Price <= 0) continue;

                var last = await _db.PriceRecords
                    .Where(r => r.ProductId == item.Id && r.StoreName == item.Brand)
                    .OrderByDescending(r => r.RecordedAt)
                    .FirstOrDefaultAsync();

                var changed = last == null || last.Price != offer.Price;
                var stale = last != null && DateTime.UtcNow - last.RecordedAt > MinInterval;
                if (!changed && !stale) continue;

                _db.PriceRecords.Add(new PriceRecord
                {
                    ProductId = item.Id,
                    StoreName = item.Brand,
                    ProductName = item.Name,
                    Price = offer.Price,
                    RecordedAt = DateTime.UtcNow
                });
                added = true;
            }

            if (added) await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fiyat geçmişi kaydedilemedi.");
        }
    }

    public async Task<List<PriceRecord>> GetHistoryAsync(string productId, int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        return await _db.PriceRecords
            .Where(r => r.ProductId == productId && r.RecordedAt >= since)
            .OrderBy(r => r.RecordedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Sadece geliştirme/demo için: son 30 güne yayılmış sahte fiyat geçmişi üretir.
    /// </summary>
    public async Task SeedDemoAsync(ClothingItem item)
    {
        var offer = item.Offers.First();
        var rnd = new Random(item.Id.GetHashCode());
        var price = offer.Price * 1.15m;

        for (var d = 30; d >= 1; d--)
        {
            if (rnd.NextDouble() < 0.25)
                price = Math.Round(price * (decimal)(0.92 + rnd.NextDouble() * 0.12) / 10) * 10;

            _db.PriceRecords.Add(new PriceRecord
            {
                ProductId = item.Id,
                StoreName = item.Brand,
                ProductName = item.Name,
                Price = price,
                RecordedAt = DateTime.UtcNow.AddDays(-d)
            });
        }

        await _db.SaveChangesAsync();
    }
}
