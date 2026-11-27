using ClothingPriceTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClothingPriceTracker.Api.Services;

/// <summary>
/// Arka planda periyodik olarak çalışan fiyat takip servisi (IHostedService / BackgroundService).
/// Kullanıcı siteyi hiç açmasa bile veritabanındaki takip edilen ürünlerin güncel fiyatlarını
/// belirli aralıklarla mağazalardan kontrol eder ve PriceRecords tablosuna otomatik kaydeder.
/// </summary>
public class PriceTrackerBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PriceTrackerBackgroundService> _logger;

    // Fiyat kontrol aralığı: Gerçek kullanımda 6 saatte bir çalışır
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    // Uygulama ilk başladığında ilk taramadan önce sistemin tam hazır olması için bekleme süresi
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(25);

    public PriceTrackerBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<PriceTrackerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("⏱️ [PriceTrackerBackgroundService] Arka plan fiyat takip servisi başlatıldı.");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrackProductPricesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚠️ [PriceTrackerBackgroundService] Fiyat güncelleme döngüsünde hata oluştu.");
            }

            try
            {
                _logger.LogInformation("⏱️ [PriceTrackerBackgroundService] Bir sonraki fiyat kontrolü {Hours} saat sonra yapılacak.", CheckInterval.TotalHours);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("🛑 [PriceTrackerBackgroundService] Arka plan fiyat takip servisi durduruldu.");
    }

    private async Task TrackProductPricesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scraperManager = scope.ServiceProvider.GetRequiredService<ProductScraperManager>();
        var priceHistory = scope.ServiceProvider.GetRequiredService<PriceHistoryService>();

        // Veritabanında kayıtlı olan tüm tekil ürün kimliklerini al (en son bakılan 30 ürün)
        var trackedProductIds = await db.PriceRecords
            .Select(r => r.ProductId)
            .Distinct()
            .Take(30)
            .ToListAsync(cancellationToken);

        // Eğer veritabanı henüz boşsa takip için varsayılan örnek ürünleri ekle
        if (trackedProductIds.Count == 0)
        {
            trackedProductIds = new List<string>
            {
                "00351A4072", // Mavi Marcus Jean
                "0613510-70057", // Mavi Ekru Tişört
                "c0p226931502", // Bershka Baggy Jean
                "lcw-5263116", // LCW Polo Yaka Tişört
            };
        }

        _logger.LogInformation("🔍 [PriceTrackerBackgroundService] {Count} adet ürünün güncel fiyatı mağazalardan taranıyor...", trackedProductIds.Count);

        var updatedCount = 0;
        foreach (var productId in trackedProductIds)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var product = await scraperManager.GetProductByUrlOrCodeAsync(productId);
                if (product != null && product.Offers.Count > 0)
                {
                    // Yeni güncel fiyatı veritabanına kaydet
                    await priceHistory.RecordAsync(new[] { product });
                    updatedCount++;
                }

                // Mağaza sunucularına aşırı yük bindirmemek için istekler arasına küçük bekleme
                await Task.Delay(1500, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ [PriceTrackerBackgroundService] Ürün fiyatı çekilemedi ({ProductId}): {Message}", productId, ex.Message);
            }
        }

        _logger.LogInformation("✅ [PriceTrackerBackgroundService] Otomatik fiyat güncelleme tamamlandı: {Updated}/{Total} ürün güncellendi.", updatedCount, trackedProductIds.Count);
    }
}
