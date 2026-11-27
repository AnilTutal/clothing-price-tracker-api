using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClothingPriceTracker.Api.Models;

namespace ClothingPriceTracker.Api.Services;

public class RecommendedItemDto
{
    public ClothingItem Product { get; set; } = default!;
    public int SimilarityScore { get; set; }
    public decimal PriceDifference { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}

public class ProductRecommendationsResponse
{
    public string TargetProductId { get; set; } = string.Empty;
    public string EngineName { get; set; } = "Qwen/Qwen3-Embedding-0.6B";
    public string EngineMode { get; set; } = "Hybrid";
    public int VectorDimensions { get; set; }
    public List<RecommendedItemDto> SimilarProducts { get; set; } = new();
    public List<RecommendedItemDto> ComplementaryProducts { get; set; } = new();
}

public class WardrobeItemInputDto
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
}

public class WardrobeRecommendationRequest
{
    public List<WardrobeItemInputDto> SelectedItems { get; set; } = new();
    public string TargetCategory { get; set; } = "all"; // "tisort", "jean", "ceket" veya "all"
}

/// <summary>
/// Çift Modlu (Dual-Mode) Yapay Zeka Vektör & Öneri Motoru:
/// 1) Hugging Face Inference API veya Lokal Ollama (Qwen/Qwen3-Embedding-0.6B) aktifse doğrudan modelden vektör alır.
/// 2) Token girilmemişse veya çevrimdışı çalışılıyorsa, 128 boyutlu Yerel Türkçe Moda & Stil Vektör Motoru (C#) devreye girer.
/// Her iki modda da Kosinüs Benzerliği (Cosine Similarity) kullanarak:
///   - Diğer mağazalardaki en benzer ikiz/alternatif ürünleri
///   - Tarz (Oversize/Baggy/Slim/Regular) ve renk uyumlu tamamlayıcı kombin parçalarını bulur.
/// </summary>
public class EmbeddingRecommendationService
{
    private readonly HttpClient _httpClient;
    private readonly ProductScraperManager _scraperManager;
    private readonly IConfiguration _config;
    private readonly ILogger<EmbeddingRecommendationService> _logger;

    private static readonly ConcurrentDictionary<string, float[]> VectorCache = new();
    private const int LocalVectorDim = 128;

    public EmbeddingRecommendationService(
        HttpClient httpClient,
        ProductScraperManager scraperManager,
        IConfiguration config,
        ILogger<EmbeddingRecommendationService> logger)
    {
        _httpClient = httpClient;
        _scraperManager = scraperManager;
        _config = config;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<ProductRecommendationsResponse> GetRecommendationsForProductAsync(ClothingItem target)
    {
        var categoryQuery = MapCategoryToQuery(target.Category, target.Name);
        var complementaryQuery = GetComplementaryCategoryQuery(categoryQuery);

        // Paralel olarak aynı kategoriden (benzer ürünler için) ve karşıt kategoriden (kombin için) aday ürünleri çek
        var sameCategoryTask = _scraperManager.SearchProductsAsync(categoryQuery, "all", 18);
        var compCategoryTask = _scraperManager.SearchProductsAsync(complementaryQuery, "all", 18);

        await Task.WhenAll(sameCategoryTask, compCategoryTask);

        var sameCategoryCandidates = (await sameCategoryTask)
            .Where(p => !string.Equals(p.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var compCandidates = (await compCategoryTask)
            .Where(p => !string.Equals(p.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 1) Benzer Ürünler için Qwen3 Instruction-Aware metin temsilleri
        var targetSimText = BuildSimilarityInstructionText(target, isQuery: true);
        var candidateSimTexts = sameCategoryCandidates
            .Select(c => BuildSimilarityInstructionText(c, isQuery: false))
            .ToList();

        // 2) Tamamlayıcı Kombin Ürünleri için Qwen3 Instruction-Aware hedef profili
        var targetCompQueryText = BuildComplementaryTargetQueryText(target);
        var candidateCompTexts = compCandidates
            .Select(c => BuildSimilarityInstructionText(c, isQuery: false))
            .ToList();

        var allTexts = new List<string> { targetSimText, targetCompQueryText };
        allTexts.AddRange(candidateSimTexts);
        allTexts.AddRange(candidateCompTexts);

        var (vectors, engineMode, dim) = await GenerateEmbeddingsBatchAsync(allTexts);

        var targetSimVec = vectors[0];
        var targetCompVec = vectors[1];
        var targetPrice = target.Offers.FirstOrDefault()?.Price ?? 0m;

        // A) Benzer Ürünleri Kosinüs Benzerliği ile Sırala
        var similarResults = new List<RecommendedItemDto>();
        for (int i = 0; i < sameCategoryCandidates.Count; i++)
        {
            var candidate = sameCategoryCandidates[i];
            var candidateVec = vectors[2 + i];
            var rawCosine = CosineSimilarity(targetSimVec, candidateVec);

            // Farklı mağazadan (Mavi <-> Bershka <-> LC Waikiki) alternatif bulmayı teşvik eden ufak bonus
            var crossStoreBonus = !string.Equals(candidate.Brand, target.Brand, StringComparison.OrdinalIgnoreCase)
                ? 0.04f
                : 0.0f;

            var score = NormalizeSimilarityPercentage(rawCosine + crossStoreBonus);
            var candPrice = candidate.Offers.FirstOrDefault()?.Price ?? 0m;

            similarResults.Add(new RecommendedItemDto
            {
                Product = candidate,
                SimilarityScore = score,
                PriceDifference = candPrice - targetPrice,
                MatchReason = BuildSimilarMatchReason(target, candidate)
            });
        }

        // B) Tamamlayıcı Kombin Ürünlerini Kosinüs Benzerliği ile Sırala
        var compOffset = 2 + sameCategoryCandidates.Count;
        var compResults = new List<RecommendedItemDto>();
        for (int i = 0; i < compCandidates.Count; i++)
        {
            var candidate = compCandidates[i];
            var candidateVec = vectors[compOffset + i];
            var rawCosine = CosineSimilarity(targetCompVec, candidateVec);
            var colorHarmonyBonus = ComputeColorHarmonyBonus(target.Color, candidate.Color);

            var score = NormalizeSimilarityPercentage((rawCosine * 0.72f) + (colorHarmonyBonus * 0.28f));
            var candPrice = candidate.Offers.FirstOrDefault()?.Price ?? 0m;

            compResults.Add(new RecommendedItemDto
            {
                Product = candidate,
                SimilarityScore = score,
                PriceDifference = candPrice,
                MatchReason = BuildComplementaryMatchReason(target, candidate)
            });
        }

        var modelName = _config["Embedding:Model"] ?? "Qwen/Qwen3-Embedding-0.6B";

        return new ProductRecommendationsResponse
        {
            TargetProductId = target.Id,
            EngineName = modelName,
            EngineMode = engineMode,
            VectorDimensions = dim,
            SimilarProducts = similarResults
                .OrderByDescending(r => r.SimilarityScore)
                .Take(4)
                .ToList(),
            ComplementaryProducts = compResults
                .OrderByDescending(r => r.SimilarityScore)
                .Take(4)
                .ToList()
        };
    }

    /// <summary>
    /// Kullanıcının Dolabım sayfasında seçtiği kıyafetlere (veya tüm dolabına) göre
    /// mağazalardan Qwen3-Embedding + Cosine Similarity ile en uyumlu tamamlayıcı ürünleri önerir.
    /// </summary>
    public async Task<ProductRecommendationsResponse> GetRecommendationsForWardrobeAsync(WardrobeRecommendationRequest request)
    {
        var anchorItems = request.SelectedItems ?? new List<WardrobeItemInputDto>();
        var firstItem = anchorItems.FirstOrDefault() ?? new WardrobeItemInputDto
        {
            Name = "Siyah Oversize Tişört",
            Category = "Tişört",
            Color = "Siyah"
        };

        var targetCategoryQuery = !string.IsNullOrWhiteSpace(request.TargetCategory) && request.TargetCategory != "all"
            ? request.TargetCategory
            : GetComplementaryCategoryQuery(MapCategoryToQuery(firstItem.Category, firstItem.Name));

        var candidates = await _scraperManager.SearchProductsAsync(targetCategoryQuery, "all", 18);

        var combinedNames = string.Join(" ", anchorItems.Select(x => $"{x.Name} {x.Category}"));
        var primaryColor = firstItem.Color;
        var styleProfile = DetectStyleSilhouette(combinedNames, combinedNames);
        var targetStyleWords = styleProfile switch
        {
            "streetwear" => "Baggy Fit Wide Leg Loose Oversize Boxy Sokak Stili",
            "slim" => "Slim Fit Skinny Dar Kalıp Modern",
            _ => "Regular Fit Straight Classic 90lar Rahat Kesim"
        };
        var harmoniousColors = GetHarmoniousColorsText(primaryColor);

        var queryInstruction =
            $"Instruct: Given the user's wardrobe items ({combinedNames}), retrieve a complementary store clothing item that matches their style silhouette and color harmony.\n" +
            $"Query: {targetCategoryQuery} | Kalıp: {targetStyleWords} | Renk: {harmoniousColors}";

        var allTexts = new List<string> { queryInstruction };
        allTexts.AddRange(candidates.Select(c => BuildSimilarityInstructionText(c, isQuery: false)));

        var (vectors, engineMode, dim) = await GenerateEmbeddingsBatchAsync(allTexts);
        var queryVec = vectors[0];

        var pseudoTarget = new ClothingItem
        {
            Name = string.Join(" + ", anchorItems.Select(a => a.Name)),
            Category = firstItem.Category,
            Color = primaryColor,
            Fit = styleProfile
        };

        var compResults = new List<RecommendedItemDto>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var candidateVec = vectors[1 + i];
            var rawCosine = CosineSimilarity(queryVec, candidateVec);
            var colorBonus = ComputeColorHarmonyBonus(primaryColor, candidate.Color);

            var score = NormalizeSimilarityPercentage((rawCosine * 0.70f) + (colorBonus * 0.30f));
            var price = candidate.Offers.FirstOrDefault()?.Price ?? 0m;

            compResults.Add(new RecommendedItemDto
            {
                Product = candidate,
                SimilarityScore = score,
                PriceDifference = price,
                MatchReason = BuildComplementaryMatchReason(pseudoTarget, candidate)
            });
        }

        var modelName = _config["Embedding:Model"] ?? "Qwen/Qwen3-Embedding-0.6B";
        return new ProductRecommendationsResponse
        {
            TargetProductId = "wardrobe-outfit",
            EngineName = modelName,
            EngineMode = engineMode,
            VectorDimensions = dim,
            ComplementaryProducts = compResults
                .OrderByDescending(r => r.SimilarityScore)
                .Take(4)
                .ToList()
        };
    }

    private async Task<(List<float[]> Vectors, string Mode, int Dim)> GenerateEmbeddingsBatchAsync(List<string> texts)
    {
        var model = _config["Embedding:Model"] ?? "Qwen/Qwen3-Embedding-0.6B";
        var hfToken = _config["Embedding:HuggingFaceApiKey"];
        var useOllama = bool.TryParse(_config["Embedding:UseOllama"], out var ollamaFlag) && ollamaFlag;

        // 1. Önce önbellekte olmayanları tespit et
        var results = new float[texts.Count][];
        var missingIndices = new List<int>();
        var missingTexts = new List<string>();

        for (int i = 0; i < texts.Count; i++)
        {
            var cacheKey = $"{( !string.IsNullOrWhiteSpace(hfToken) ? "HF" : useOllama ? "OLLAMA" : "LOCAL" )}:{texts[i]}";
            if (VectorCache.TryGetValue(cacheKey, out var cached))
            {
                results[i] = cached;
            }
            else
            {
                missingIndices.Add(i);
                missingTexts.Add(texts[i]);
            }
        }

        string activeMode = "Qwen3-Style Local Semantic Engine (C# Cosine Vector)";

        if (missingTexts.Count > 0)
        {
            List<float[]>? externalVectors = null;

            // Mod A: Hugging Face Inference API (Qwen/Qwen3-Embedding-0.6B)
            if (!string.IsNullOrWhiteSpace(hfToken))
            {
                externalVectors = await TryFetchHuggingFaceEmbeddingsAsync(model, hfToken, missingTexts);
                if (externalVectors != null)
                {
                    activeMode = $"Hugging Face API ({model})";
                }
            }
            // Mod B: Lokal Ollama (qwen3-embedding:0.6b)
            else if (useOllama)
            {
                var ollamaEndpoint = _config["Embedding:OllamaEndpoint"] ?? "http://localhost:11434/api/embed";
                externalVectors = await TryFetchOllamaEmbeddingsAsync(ollamaEndpoint, missingTexts);
                if (externalVectors != null)
                {
                    activeMode = "Ollama Local (qwen3-embedding:0.6b)";
                }
            }

            // Mod C: Yerel Türkçe Moda & Stil Vektör Motoru (Her zaman hazır & sıfır gecikmeli fallback)
            for (int m = 0; m < missingIndices.Count; m++)
            {
                var idx = missingIndices[m];
                var vec = externalVectors != null && m < externalVectors.Count
                    ? externalVectors[m]
                    : ComputeLocalFashionEmbedding(missingTexts[m]);

                var cacheKey = $"{( !string.IsNullOrWhiteSpace(hfToken) ? "HF" : useOllama ? "OLLAMA" : "LOCAL" )}:{missingTexts[m]}";
                VectorCache[cacheKey] = vec;
                results[idx] = vec;
            }
        }
        else if (!string.IsNullOrWhiteSpace(hfToken))
        {
            activeMode = $"Hugging Face API ({model} - Cached)";
        }
        else if (useOllama)
        {
            activeMode = "Ollama Local (qwen3-embedding:0.6b - Cached)";
        }

        var dim = results.FirstOrDefault()?.Length ?? LocalVectorDim;
        return (results.ToList(), activeMode, dim);
    }

    private async Task<List<float[]>?> TryFetchHuggingFaceEmbeddingsAsync(string model, string apiKey, List<string> texts)
    {
        try
        {
            var url = $"https://router.huggingface.co/hf-inference/models/{model}/pipeline/feature-extraction";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var payload = JsonSerializer.Serialize(new
            {
                inputs = texts,
                options = new { wait_for_model = true }
            });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Hugging Face API yanıt kodu: {Status}. Yerel vektör motoruna geçiliyor.", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<List<float[]>>(json);
            if (parsed != null && parsed.Count == texts.Count)
            {
                return parsed.Select(NormalizeL2).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hugging Face Embedding çağrısı başarısız oldu, yerel vektör motoru kullanılıyor.");
        }

        return null;
    }

    private async Task<List<float[]>?> TryFetchOllamaEmbeddingsAsync(string endpoint, List<string> texts)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = "qwen3-embedding:0.6b",
                input = texts
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(endpoint, content);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            if (doc.RootElement.TryGetProperty("embeddings", out var embArr) && embArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<float[]>();
                foreach (var item in embArr.EnumerateArray())
                {
                    var vec = item.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    list.Add(NormalizeL2(vec));
                }
                if (list.Count == texts.Count) return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama Embedding çağrısı başarısız oldu, yerel vektör motoru kullanılıyor.");
        }

        return null;
    }

    /// <summary>
    /// Qwen3-Embedding Instruction-Aware formatında ürün metni oluşturur.
    /// </summary>
    private static string BuildSimilarityInstructionText(ClothingItem item, bool isQuery)
    {
        var prefix = isQuery
            ? "Instruct: Given a clothing product, retrieve semantically similar clothing items with matching category, fit silhouette, fabric and color tone.\nQuery: "
            : "";

        return $"{prefix}{item.Category} | {item.Name} | Kalıp: {item.Fit ?? "Regular"} | Renk: {item.Color ?? "Standart"} | Materyal: {item.Material ?? "Pamuk"}";
    }

    /// <summary>
    /// Qwen3-Embedding Instruction-Aware formatında tamamlayıcı (kombin) hedef sorgusu oluşturur.
    /// Örneğin ürün Oversize/Boxy bir tişört ise, hedef vektör Baggy/Wide Leg/Relaxed bir alt giyim ve uyumlu renkleri tarif eder.
    /// </summary>
    private static string BuildComplementaryTargetQueryText(ClothingItem item)
    {
        var styleProfile = DetectStyleSilhouette(item.Name, item.Fit);
        var targetComplementaryStyle = styleProfile switch
        {
            "streetwear" => "Baggy Fit Wide Leg Loose Oversize Relaxed Kargo Sokak Stili",
            "slim" => "Slim Fit Skinny Dar Kesim Klasik Modern",
            _ => "Regular Fit Straight Classic 90lar Rahat Kesim"
        };

        var compCategory = MapCategoryToQuery(item.Category, item.Name) is "jean"
            ? "Tişört Gömlek Sweatshirt Ceket Üst Giyim"
            : "Jean Pantolon Denim Alt Giyim";

        var harmoniousColors = GetHarmoniousColorsText(item.Color);

        return $"Instruct: Given a clothing item, retrieve a complementary outfit piece that matches its style silhouette and color harmony.\nQuery: {compCategory} | Kalıp: {targetComplementaryStyle} | Renk: {harmoniousColors}";
    }

    /// <summary>
    /// 128 Boyutlu Yerel Türkçe Moda & Stil Semantik Vektör Motoru (L2 Normalize).
    /// Kategori, Kalıp/Silüet (Streetwear/Baggy/Oversize vs Regular vs Slim), Renk Uzayı, Materyal ve
    /// Karakter N-Gram LSH (Locality Sensitive Hashing) projeksiyonlarını tek bir vektörde birleştirir.
    /// </summary>
    private static float[] ComputeLocalFashionEmbedding(string text)
    {
        var vec = new float[LocalVectorDim];
        var lower = text.ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('ş', 's')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ö', 'o')
            .Replace('ç', 'c');

        // 1) Kategori & Giyim Rolü Boyutları (0..15)
        AddFeatureIfMatches(vec, lower, new[] { "tisort", "t-shirt", "tee", "kisa kollu" }, (0, 2.2f), (1, 1.5f));
        AddFeatureIfMatches(vec, lower, new[] { "gomlek", "shirt", "keten gomlek", "oduncu" }, (0, 1.8f), (2, 2.2f));
        AddFeatureIfMatches(vec, lower, new[] { "sweatshirt", "hoodie", "kapusonlu", "kazak", "triko" }, (0, 1.9f), (3, 2.2f));
        AddFeatureIfMatches(vec, lower, new[] { "jean", "denim", "kot", "pantolon", "trousers", "kargo" }, (4, 2.6f), (5, 1.8f));
        AddFeatureIfMatches(vec, lower, new[] { "ceket", "mont", "jacket", "kaban", "trenckot", "bomber" }, (6, 2.5f), (7, 1.8f));

        // 2) Kalıp & Stil Silüeti Boyutları (16..35) — Sokak Stili vs Klasik vs Dar Kalıp
        AddFeatureIfMatches(vec, lower, new[] { "oversize", "boxy", "baggy", "wide leg", "loose", "genis", "salas", "boyfriend", "skater", "kargo", "relaxed" },
            (16, 2.8f), (17, 2.0f), (18, 1.4f));
        AddFeatureIfMatches(vec, lower, new[] { "regular", "straight", "classic", "90lar", "90s", "standart", "normal", "rahat" },
            (20, 2.5f), (21, 1.6f), (17, 0.7f));
        AddFeatureIfMatches(vec, lower, new[] { "slim", "skinny", "dar", "fitted", "tailored" },
            (24, 2.7f), (25, 1.8f));

        // 3) Renk & Ton Uzayı Boyutları (36..59)
        AddFeatureIfMatches(vec, lower, new[] { "siyah", "black", "antrasit", "koyu gri" }, (36, 2.2f), (37, 1.8f));
        AddFeatureIfMatches(vec, lower, new[] { "beyaz", "white", "ekru", "krem", "off-white" }, (36, 1.8f), (38, 2.3f));
        AddFeatureIfMatches(vec, lower, new[] { "gri", "grey", "melanj", "fume" }, (36, 2.0f), (39, 2.0f));
        AddFeatureIfMatches(vec, lower, new[] { "mavi", "indigo", "blue", "buz mavisi", "acik mavi" }, (40, 2.4f), (41, 1.8f));
        AddFeatureIfMatches(vec, lower, new[] { "lacivert", "navy", "koyu mavi" }, (40, 2.0f), (42, 2.2f));
        AddFeatureIfMatches(vec, lower, new[] { "bej", "kum", "vizon", "camel", "tas" }, (44, 2.4f), (38, 1.1f));
        AddFeatureIfMatches(vec, lower, new[] { "haki", "yesil", "zeytin", "khaki", "olive" }, (46, 2.4f), (44, 1.0f));
        AddFeatureIfMatches(vec, lower, new[] { "kahve", "kahverengi", "taba", "brown" }, (48, 2.3f), (44, 1.2f));
        AddFeatureIfMatches(vec, lower, new[] { "kirmizi", "bordo", "pembe", "burgundy" }, (50, 2.3f));

        // 4) Kumaş & Materyal Boyutları (60..79)
        AddFeatureIfMatches(vec, lower, new[] { "pamuk", "cotton", "penye", "organik" }, (60, 1.6f));
        AddFeatureIfMatches(vec, lower, new[] { "denim", "kot", "jean" }, (62, 2.2f));
        AddFeatureIfMatches(vec, lower, new[] { "keten", "linen", "yazlik" }, (64, 2.1f));
        AddFeatureIfMatches(vec, lower, new[] { "polar", "sardonlu", "kislik", "yun", "triko" }, (66, 2.1f));
        AddFeatureIfMatches(vec, lower, new[] { "deri", "suet", "paraşüt", "nylon" }, (68, 2.1f));

        // 5) Karakter 3-Gram & Kelime Hash Projeksiyonu (80..127)
        // Ürün başlığındaki özel model adlarını (Marcus, Jake, Martin, Polo, Baskılı, Çizgili vb.) yakalar
        var tokens = lower.Split(new[] { ' ', '|', ',', '.', '-', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.Length < 2 || token is "instruct:" or "query:" or "kalip:" or "renk:" or "materyal:")
                continue;

            int h1 = Math.Abs( GetStableHash(token) );
            int dimIdx = 80 + (h1 % 48);
            float sign = ((h1 >> 8) & 1) == 0 ? 1.0f : -1.0f;
            vec[dimIdx] += sign * 0.85f;

            // Karakter 3-gram ile yazım farklarına dayanıklılık (Subword embedding)
            for (int c = 0; c <= token.Length - 3; c++)
            {
                var trigram = token.Substring(c, 3);
                int h2 = Math.Abs(GetStableHash(trigram));
                int triIdx = 80 + (h2 % 48);
                vec[triIdx] += (((h2 >> 5) & 1) == 0 ? 0.35f : -0.35f);
            }
        }

        return NormalizeL2(vec);
    }

    private static void AddFeatureIfMatches(float[] vec, string text, string[] keywords, params (int Index, float Weight)[] activations)
    {
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.Ordinal))
            {
                foreach (var (idx, weight) in activations)
                {
                    vec[idx] += weight;
                }
                break;
            }
        }
    }

    private static int GetStableHash(string s)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in s)
            {
                hash = (hash * 31) + c;
            }
            return hash;
        }
    }

    private static float[] NormalizeL2(float[] vec)
    {
        double sumSq = 0;
        for (int i = 0; i < vec.Length; i++)
            sumSq += vec[i] * vec[i];

        float norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-6f) return vec;

        var result = new float[vec.Length];
        for (int i = 0; i < vec.Length; i++)
            result[i] = vec[i] / norm;

        return result;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0f;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        if (denom < 1e-8) return 0f;
        return (float)(dot / denom);
    }

    private static int NormalizeSimilarityPercentage(float rawScore)
    {
        // Kosinüs skorunu kullanıcı dostu %68 - %98 bandına ölçekle
        var clamped = Math.Clamp(rawScore, 0.20f, 0.99f);
        var mapped = 65 + (int)Math.Round(clamped * 33);
        return Math.Clamp(mapped, 68, 98);
    }

    private static string DetectStyleSilhouette(string? name, string? fit)
    {
        var combined = $"{name} {fit}".ToLowerInvariant();
        if (combined.Contains("oversize") || combined.Contains("baggy") || combined.Contains("boxy") ||
            combined.Contains("wide") || combined.Contains("loose") || combined.Contains("kargo") ||
            combined.Contains("geniş") || combined.Contains("salas") || combined.Contains("relaxed"))
        {
            return "streetwear";
        }

        if (combined.Contains("slim") || combined.Contains("skinny") || combined.Contains("dar"))
        {
            return "slim";
        }

        return "regular";
    }

    private static string GetHarmoniousColorsText(string? color)
    {
        var c = (color ?? "").ToLowerInvariant();
        if (c.Contains("siyah") || c.Contains("antrasit"))
            return "Beyaz Ekru Gri Açık Mavi İndigo Bej Haki";
        if (c.Contains("beyaz") || c.Contains("ekru"))
            return "Mavi İndigo Siyah Lacivert Haki Bej Kahverengi";
        if (c.Contains("mavi") || c.Contains("indigo") || c.Contains("lacivert"))
            return "Beyaz Ekru Siyah Bej Gri Haki";
        if (c.Contains("bej") || c.Contains("kahve"))
            return "Beyaz Siyah Lacivert Mavi Haki Ekru";
        if (c.Contains("haki") || c.Contains("yeşil"))
            return "Siyah Beyaz Ekru Bej Mavi";

        return "Siyah Beyaz Ekru Mavi Bej Gri";
    }

    private static float ComputeColorHarmonyBonus(string? colorA, string? colorB)
    {
        if (string.IsNullOrWhiteSpace(colorA) || string.IsNullOrWhiteSpace(colorB))
            return 0.75f;

        var a = colorA.ToLowerInvariant();
        var b = colorB.ToLowerInvariant();

        bool aNeutral = a.Contains("siyah") || a.Contains("beyaz") || a.Contains("ekru") || a.Contains("gri") || a.Contains("bej");
        bool bNeutral = b.Contains("siyah") || b.Contains("beyaz") || b.Contains("ekru") || b.Contains("gri") || b.Contains("bej");

        if (aNeutral && !bNeutral || !aNeutral && bNeutral)
            return 0.96f; // Nötr + Ana renk kontrastı
        if (aNeutral && bNeutral && a != b)
            return 0.92f; // İki farklı nötr ton (örn: Siyah + Beyaz/Gri)
        if ((a.Contains("mavi") || a.Contains("indigo")) && (b.Contains("bej") || b.Contains("beyaz") || b.Contains("siyah")))
            return 0.95f;

        return a == b ? 0.72f : 0.84f;
    }

    private static string BuildSimilarMatchReason(ClothingItem target, ClothingItem candidate)
    {
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(target.Fit) && !string.IsNullOrWhiteSpace(candidate.Fit) &&
            DetectStyleSilhouette(target.Name, target.Fit) == DetectStyleSilhouette(candidate.Name, candidate.Fit))
        {
            reasons.Add($"{candidate.Fit} kesim silüeti");
        }

        if (!string.IsNullOrWhiteSpace(target.Color) && !string.IsNullOrWhiteSpace(candidate.Color) &&
            string.Equals(target.Color, candidate.Color, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"{candidate.Color} renk tonu");
        }

        reasons.Add($"{candidate.Category} kategorisi");

        return $"Qwen3 vektör uzayında {string.Join(", ", reasons)} ile en yakın alternatif ({candidate.Brand}).";
    }

    private static string BuildComplementaryMatchReason(ClothingItem target, ClothingItem candidate)
    {
        var targetStyle = DetectStyleSilhouette(target.Name, target.Fit);
        var styleLabel = targetStyle switch
        {
            "streetwear" => "Sokak Stili (Oversize / Baggy)",
            "slim" => "Modern Dar Kalıp (Slim)",
            _ => "Klasik / Günlük Rahat (Regular)"
        };

        var colorInfo = !string.IsNullOrWhiteSpace(target.Color) && !string.IsNullOrWhiteSpace(candidate.Color)
            ? $"{target.Color} × {candidate.Color} renk uyumu"
            : "renk dengesi";

        return $"{styleLabel} kesim dili ve {colorInfo} ile tamamlayıcı kombin parçası.";
    }

    private static string MapCategoryToQuery(string? category, string? name)
    {
        var text = $"{category} {name}".ToLowerInvariant();
        if (text.Contains("jean") || text.Contains("pantolon") || text.Contains("denim") || text.Contains("kot"))
            return "jean";
        if (text.Contains("sweatshirt") || text.Contains("hoodie") || text.Contains("kapüşon"))
            return "sweatshirt";
        if (text.Contains("gömlek") || text.Contains("gomlek") || text.Contains("shirt"))
            return "gomlek";
        if (text.Contains("ceket") || text.Contains("mont") || text.Contains("jacket"))
            return "ceket";
        return "tisort";
    }

    private static string GetComplementaryCategoryQuery(string currentCategoryQuery)
    {
        return currentCategoryQuery switch
        {
            "jean" => "tisort",
            "tisort" => "jean",
            "gomlek" => "jean",
            "sweatshirt" => "jean",
            "ceket" => "jean",
            _ => "jean"
        };
    }
}
