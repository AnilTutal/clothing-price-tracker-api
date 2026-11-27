namespace ClothingPriceTracker.Api.Models;

/// <summary>
/// Bir ürünün belirli bir andaki fiyatını tutan veritabanı kaydı (fiyat geçmişi grafiği için).
/// </summary>
public class PriceRecord
{
    public int Id { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
