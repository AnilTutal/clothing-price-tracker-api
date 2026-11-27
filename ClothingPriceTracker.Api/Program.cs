using System.Diagnostics;
using ClothingPriceTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using ClothingPriceTracker.Api.Services;
using ClothingPriceTracker.Api.Services.Abstractions;
using ClothingPriceTracker.Api.Services.Scrapers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient<MaviStoreScraper>();
builder.Services.AddScoped<IStoreScraper>(sp => sp.GetRequiredService<MaviStoreScraper>());
builder.Services.AddScoped<IStoreScraper, BershkaStoreScraper>();
builder.Services.AddHttpClient<LcwStoreScraper>();
builder.Services.AddScoped<IStoreScraper>(sp => sp.GetRequiredService<LcwStoreScraper>());
builder.Services.AddScoped<ProductScraperManager>();
builder.Services.AddHttpClient<EmbeddingRecommendationService>();

// Fiyat geçmişi veritabanı (SQLite). PostgreSQL'e geçmek için sadece bu satır (UseNpgsql) değişir.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=pricetracker.db"));
builder.Services.AddScoped<PriceHistoryService>();

// Arka planda periyodik çalışan otomatik fiyat güncelleme servisi
builder.Services.AddHostedService<PriceTrackerBackgroundService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// React (Frontend) uygulamasından gelecek isteklere izin vermek için CORS ayarı
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Veritabanı ve tablolar yoksa otomatik oluştur
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "swagger";
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Clothing Price Tracker API v1");
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var baseUrl = app.Urls.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(baseUrl)) return;

    var swaggerUrl = baseUrl.TrimEnd('/') + "/swagger";
    try { Process.Start(new ProcessStartInfo(swaggerUrl) { UseShellExecute = true }); }
    catch { }
});

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();
app.MapControllers();
app.Run();
