using ClothingPriceTracker.Api.Models;

namespace ClothingPriceTracker.Api.Services.Abstractions;

/// <summary>
/// Tüm mağaza (Mavi, Bershka, Zara vb.) veri çekme servislerinin uyması gereken ortak sözleşme.
/// Yeni bir marka eklendiğinde sadece bu arayüzü uygulayan yeni bir sınıf eklenir.
/// </summary>
public interface IStoreScraper
{
    string StoreName { get; }

    /// <summary>
    /// Verilen URL veya ürün kodunun bu mağazaya ait olup olmadığını belirler.
    /// </summary>
    bool CanHandle(string urlOrCode);

    /// <summary>
    /// Tekil ürün linkinden veya ürün kodundan ürün detayını çeker.
    /// </summary>
    Task<ClothingItem?> GetProductByUrlOrCodeAsync(string urlOrCode);

    /// <summary>
    /// Mağaza genelinde kategori/kelime bazlı ürün araması yapar.
    /// </summary>
    Task<List<ClothingItem>> SearchProductsAsync(string query = "tisort", int pageSize = 6);
}
