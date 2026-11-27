using ClothingPriceTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClothingPriceTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ProductScraperManager _scraperManager;
    private readonly PriceHistoryService _priceHistory;
    private readonly EmbeddingRecommendationService _embeddingService;

    public ProductsController(
        ProductScraperManager scraperManager,
        PriceHistoryService priceHistory,
        EmbeddingRecommendationService embeddingService)
    {
        _scraperManager = scraperManager;
        _priceHistory = priceHistory;
        _embeddingService = embeddingService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string q = "tisort",
        [FromQuery] string? store = null,
        [FromQuery] int pageSize = 8)
    {
        var products = await _scraperManager.SearchProductsAsync(q, store, pageSize);
        return Ok(products);
    }

    [HttpGet("stores")]
    public IActionResult GetSupportedStores()
    {
        return Ok(_scraperManager.GetSupportedStores());
    }

    /// <summary>
    /// Ortak ürün çekme uç noktası: Mavi, Bershka vb. hangi mağazanın linki/kodu verilirse otomatik algılar.
    /// </summary>
    [HttpGet("scrape")]
    [HttpGet("mavi")]
    public async Task<IActionResult> ScrapeProductByUrl([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest(new { message = "Lütfen geçerli bir ürün linki veya ürün kodu girin." });

        var product = await _scraperManager.GetProductByUrlOrCodeAsync(url);
        if (product == null)
            return NotFound(new { message = "Bu link veya kod için ürün bulunamadı." });

        return Ok(product);
    }

    [HttpGet("{id}/price-history")]
    public async Task<IActionResult> GetPriceHistory(string id, [FromQuery] int days = 30)
    {
        var history = await _priceHistory.GetHistoryAsync(id, days);
        return Ok(history.Select(h => new { date = h.RecordedAt, price = h.Price }));
    }

    /// <summary>
    /// Sadece geliştirme: grafiği denemek için ürüne son 30 günlük sahte fiyat geçmişi ekler.
    /// </summary>
    [HttpPost("{id}/price-history/seed-demo")]
    public async Task<IActionResult> SeedDemoHistory(string id, [FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment())
            return NotFound();

        var product = await _scraperManager.GetProductByUrlOrCodeAsync(id);
        if (product == null)
            return NotFound(new { message = "Ürün bulunamadı." });

        await _priceHistory.SeedDemoAsync(product);
        return Ok(new { message = "Demo fiyat geçmişi eklendi." });
    }

    [HttpGet("{id}/recommendations")]
    public async Task<IActionResult> GetProductRecommendations(string id)
    {
        var product = await _scraperManager.GetProductByUrlOrCodeAsync(id);
        if (product == null)
            return NotFound(new { message = "Ürün bulunamadı." });

        var recommendations = await _embeddingService.GetRecommendationsForProductAsync(product);
        return Ok(recommendations);
    }

    [HttpPost("wardrobe-recommendations")]
    public async Task<IActionResult> GetWardrobeRecommendations([FromBody] WardrobeRecommendationRequest request)
    {
        var recommendations = await _embeddingService.GetRecommendationsForWardrobeAsync(request);
        return Ok(recommendations);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetProductById(string id)
    {
        var product = await _scraperManager.GetProductByUrlOrCodeAsync(id);
        if (product == null)
            return NotFound(new { message = "Ürün bulunamadı." });

        return Ok(product);
    }
}
