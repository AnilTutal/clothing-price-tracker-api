namespace ClothingPriceTracker.Api.Models;

public class ClothingOffer
{
    public string StoreName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public int? DiscountRate { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class ClothingItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public List<string> GalleryImages { get; set; } = new();
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? Fit { get; set; }
    public string? Material { get; set; }
    public List<string> AvailableSizes { get; set; } = new();
    public List<ClothingOffer> Offers { get; set; } = new();
}
