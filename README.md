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

## 📂 Klasör Düzeni
- `Controllers/` 👉 API uçlarının (endpoint'lerin) bulunduğu klasör (Örn: `ProductsController.cs`)
- `Models/` 👉 Kıyafet, Mağaza, Fiyat sınıflarının (Entity / DTO) bulunduğu klasör
- `Services/` 👉 İş mantığı ve fiyat karşılaştırma servislerinin bulunduğu klasör
- `Data/` 👉 PostgreSQL (`DbContext`) veritabanı bağlantısının yer alacağı klasör
- `Program.cs` 👉 Swagger, CORS ve uygulama başlatma ayarları
