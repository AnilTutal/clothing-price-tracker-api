# ⚙️ Clothing Price Tracker API (.NET 8 Web API)

Farklı mağazalardaki kıyafet fiyatlarını karşılaştırmayı sağlayan platformumuzun **C# (.NET 8 Web API + Swagger)** tabanlı Backend projesi.

## 🚀 Projeyi Bilgisayarınızda Nasıl Çalıştırırsınız?

### Yöntem 1: Visual Studio 2022 ile (Önerilen)
1. Projeyi bilgisayarınıza çekin ve kendi branch'inize geçin (`anil`, `sena`, `irem`, `berkay`).
2. `ClothingPriceTrackerApi.sln` dosyasına çift tıklayarak Visual Studio 2022'de açın.
3. Üstteki yeşil **`https` (Play)** tuşuna (veya `F5`'e) basın. Otomatik olarak tarayıcıda `/swagger` test ekranı açılacaktır.

### Yöntem 2: Terminal ile
```bash
git checkout kendi_isminiz
dotnet run --project ClothingPriceTracker.Api
```

## 📂 Klasör Düzeni ve Ortak Scraper Mimarisi
- `Controllers/ProductsController.cs` 👉 Ürün listeleme (`/api/products`), ortak linkten ürün çekme (`/api/products/scrape`) ve tekil ürün detayı (`/api/products/{id}`) uçları
- `Models/ClothingItem.cs` 👉 Ortak kıyafet (`ClothingItem`) ve mağaza fiyat teklifi (`ClothingOffer`) modelleri
- `Services/Abstractions/IStoreScraper.cs` 👉 Tüm mağaza adaptörlerinin (Mavi, Bershka vb.) uyduğu ortak sözleşme (Interface)
- `Services/Scrapers/MaviStoreScraper.cs` 👉 Mavi canlı ürün çekme ve arama adaptörü
- `Services/Scrapers/BershkaStoreScraper.cs` 👉 Bershka ürün çekme, URL çözümleyici ve katalog adaptörü
- `Services/ProductScraperManager.cs` 👉 Gelen URL'nin hangi mağazaya ait olduğunu otomatik tanıyan ve çoklu mağaza aramalarını paralel (`Task.WhenAll`) birleştiren ortak yönetici servis
- `Data/` 👉 PostgreSQL (`DbContext`) veritabanı bağlantısının yer alacağı klasör
- `Program.cs` 👉 Dependency Injection (`IStoreScraper`), Swagger, CORS ve uygulama başlatma ayarları
