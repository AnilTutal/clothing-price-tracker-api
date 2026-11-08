using Microsoft.AspNetCore.Mvc;

namespace ClothingPriceTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetHello()
    {
        return Ok(new { message = "Merhaba! Clothing Price Tracker API çalışıyor." });
    }
}
