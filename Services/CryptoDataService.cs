using Newtonsoft.Json;
using BotCripto.Models;

namespace BotCripto.Services;

public class CryptoDataService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.crypto.com/v2";
    private const string BybitBaseUrl = "https://api.bybit.com/v5";
    private const string CoinGeckoBaseUrl = "https://api.coingecko.com/api/v3";
    private const decimal MinVolume24hUsd = 5_000_000m;
    private readonly List<string> _largeCapCryptos = new();
    private int _apiCallCount = 0;
    private DateTime _lastApiCallTime = DateTime.UtcNow;
    private const int RateLimitDelayMs = 100; // milliseconds between calls

    public CryptoDataService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "BotCripto/1.1");
    }

    public async Task<List<Cryptocurrency>> GetLargeCapCryptocurrenciesAsync()
    {
        var volumes = await GetVolumesAsync();

        // Tenta Crypto.com
        var cryptoComData = await TryGetCryptoComTickers();
        if (cryptoComData != null && cryptoComData.Count > 0)
        {
            var filtered = FilterByVolume(cryptoComData, volumes);
            Console.WriteLine($"✅ Caricate {filtered.Count} criptovalute da API Crypto.com (volume 24h ≥ ${MinVolume24hUsd:N0})");
            return filtered;
        }

        // Fallback: Tenta Bybit
        Console.WriteLine("⚠️  Crypto.com fallito, tentativo Bybit API...");
        var bybitData = await TryGetBybitTickers();
        if (bybitData != null && bybitData.Count > 0)
        {
            var filtered = FilterByVolume(bybitData, volumes);
            Console.WriteLine($"✅ Caricate {filtered.Count} criptovalute da API Bybit (volume 24h ≥ ${MinVolume24hUsd:N0})");
            return filtered;
        }

        // Fallback finale: Dati demo
        Console.WriteLine("⚠️  Tutte le API fallite. Utilizzo dati demo...");
        return GetDemoData();
    }

    private List<Cryptocurrency> FilterByVolume(List<Cryptocurrency> cryptos, Dictionary<string, decimal> volumes)
    {
        if (volumes.Count == 0)
        {
            Console.WriteLine("   ⚠️  Dati di volume non disponibili (CoinGecko irraggiungibile): filtro volume saltato per questo ciclo");
            return cryptos;
        }

        var result = new List<Cryptocurrency>();
        foreach (var crypto in cryptos)
        {
            if (volumes.TryGetValue(crypto.Symbol, out var volume24h) && volume24h >= MinVolume24hUsd)
            {
                result.Add(crypto);
            }
        }

        return result;
    }

    private async Task<Dictionary<string, decimal>> GetVolumesAsync()
    {
        var volumes = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int page = 1; page <= 2; page++)
            {
                await RateLimitDelay();
                var url = $"{CoinGeckoBaseUrl}/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=250&page={page}";

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    break;

                var content = await response.Content.ReadAsStringAsync();
                var coins = JsonConvert.DeserializeObject<List<CoinGeckoMarketData>>(content);
                if (coins == null || coins.Count == 0)
                    break;

                foreach (var coin in coins)
                {
                    var symbol = coin.Symbol?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(symbol) || volumes.ContainsKey(symbol))
                        continue;

                    volumes[symbol] = coin.TotalVolume;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ CoinGecko Error (dati di volume): {ex.Message}");
        }

        return volumes;
    }

    private async Task<List<Cryptocurrency>> TryGetCryptoComTickers()
    {
        try
        {
            await RateLimitDelay();
            var url = $"{BaseUrl}/public/get-ticker";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<CryptoComTickerResponse>(content);

            if (data?.Result?.Data == null || data.Result.Data.Count == 0)
                return null;

            var result = new List<Cryptocurrency>();
            foreach (var ticker in data.Result.Data)
            {
                try
                {
                    if (ticker.i.EndsWith("_USDT") || ticker.i.EndsWith("_USD"))
                    {
                        var symbol = ticker.i.Replace("_USDT", "").Replace("_USD", "");
                        var price = decimal.Parse(ticker.a);

                        if (price > 0)
                        {
                            result.Add(new Cryptocurrency
                            {
                                Symbol = symbol,
                                Name = symbol,
                                CurrentPrice = price,
                                LastUpdate = DateTime.UtcNow
                            });
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            return result.Count > 0 ? result.OrderByDescending(c => c.CurrentPrice).Take(500).ToList() : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Crypto.com Error: {ex.Message}");
            return null;
        }
    }

    private async Task<List<Cryptocurrency>> TryGetBybitTickers()
    {
        try
        {
            await RateLimitDelay();
            var url = $"{BybitBaseUrl}/market/tickers?category=spot&limit=200";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            dynamic data = JsonConvert.DeserializeObject(content);

            if (data?.result?.list == null)
                return null;

            var result = new List<Cryptocurrency>();
            foreach (var ticker in data.result.list)
            {
                try
                {
                    string symbol = ticker.symbol;
                    if (symbol.EndsWith("USDT"))
                    {
                        var symbolName = symbol.Replace("USDT", "");
                        var price = decimal.Parse((string)ticker.lastPrice);

                        if (price > 0)
                        {
                            result.Add(new Cryptocurrency
                            {
                                Symbol = symbolName,
                                Name = symbolName,
                                CurrentPrice = price,
                                LastUpdate = DateTime.UtcNow
                            });
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            return result.Count > 0 ? result.OrderByDescending(c => c.CurrentPrice).Take(500).ToList() : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Bybit Error: {ex.Message}");
            return null;
        }
    }

    private async Task RateLimitDelay()
    {
        var timeSinceLastCall = (DateTime.UtcNow - _lastApiCallTime).TotalMilliseconds;
        if (timeSinceLastCall < RateLimitDelayMs)
        {
            await Task.Delay((int)(RateLimitDelayMs - timeSinceLastCall));
        }
        _lastApiCallTime = DateTime.UtcNow;
    }

    private List<Cryptocurrency> GetDemoData()
    {
        return new List<Cryptocurrency>
        {
            new() { Symbol = "BTC", Name = "BTC", CurrentPrice = 78728.50m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ETH", Name = "ETH", CurrentPrice = 2470.71m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "SOL", Name = "SOL", CurrentPrice = 102.85m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "XRP", Name = "XRP", CurrentPrice = 1.3772m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ADA", Name = "ADA", CurrentPrice = 0.19602m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "DOGE", Name = "DOGE", CurrentPrice = 0.082936m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "CRO", Name = "CRO", CurrentPrice = 0.05611m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "USDT", Name = "USDT", CurrentPrice = 0.99978m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ARB", Name = "ARB", CurrentPrice = 0.089064m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "HNT", Name = "HNT", CurrentPrice = 0.6493m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "LINK", Name = "LINK", CurrentPrice = 11.324m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "SUI", Name = "SUI", CurrentPrice = 0.72221m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "LTC", Name = "LTC", CurrentPrice = 48.357m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "XLM", Name = "XLM", CurrentPrice = 0.17722m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "PEPE", Name = "PEPE", CurrentPrice = 0.000003558m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "DOT", Name = "DOT", CurrentPrice = 0.8306m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "AVAX", Name = "AVAX", CurrentPrice = 7.198m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "SHIB", Name = "SHIB", CurrentPrice = 0.000005039m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "NEAR", Name = "NEAR", CurrentPrice = 1.8546m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "UNI", Name = "UNI", CurrentPrice = 5.1292m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "BCH", Name = "BCH", CurrentPrice = 246.61m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "AAVE", Name = "AAVE", CurrentPrice = 122.983m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "FET", Name = "FET", CurrentPrice = 0.1498m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ATOM", Name = "ATOM", CurrentPrice = 1.466m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ICP", Name = "ICP", CurrentPrice = 2.4045m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "INJ", Name = "INJ", CurrentPrice = 4.938m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "QNT", Name = "QNT", CurrentPrice = 61.348m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "GALA", Name = "GALA", CurrentPrice = 0.001737m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "JUP", Name = "JUP", CurrentPrice = 0.21429m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ZRO", Name = "ZRO", CurrentPrice = 1.0606m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ARB", Name = "ARB", CurrentPrice = 0.089064m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "RENDER", Name = "RENDER", CurrentPrice = 1.4138m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "GRT", Name = "GRT", CurrentPrice = 0.01631m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "FIL", Name = "FIL", CurrentPrice = 0.6720m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ORDI", Name = "ORDI", CurrentPrice = 3.8970m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "APT", Name = "APT", CurrentPrice = 0.5250m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "WBTC", Name = "WBTC", CurrentPrice = 77464.57m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ONDO", Name = "ONDO", CurrentPrice = 0.34279m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "AIOZ", Name = "AIOZ", CurrentPrice = 0.056900m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "CVX", Name = "CVX", CurrentPrice = 2.2408m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "LDO", Name = "LDO", CurrentPrice = 0.3554m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "WIF", Name = "WIF", CurrentPrice = 0.1974m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "AR", Name = "AR", CurrentPrice = 2.0573m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "DGB", Name = "DGB", CurrentPrice = 0.004593m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "AKT", Name = "AKT", CurrentPrice = 0.5110m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "CRV", Name = "CRV", CurrentPrice = 0.3111m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "FARTCOIN", Name = "FARTCOIN", CurrentPrice = 0.17360m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "APE", Name = "APE", CurrentPrice = 0.1346m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "PEAQ", Name = "PEAQ", CurrentPrice = 0.022610m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "BICO", Name = "BICO", CurrentPrice = 0.02329m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "STX", Name = "STX", CurrentPrice = 0.2504m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ADI", Name = "ADI", CurrentPrice = 7.00507m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "WEN", Name = "WEN", CurrentPrice = 0.0000114683m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "THETA", Name = "THETA", CurrentPrice = 0.16768m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "RSR", Name = "RSR", CurrentPrice = 0.0013432m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "VELO", Name = "VELO", CurrentPrice = 0.0047646m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "JASMY", Name = "JASMY", CurrentPrice = 0.0048322m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "LUNC", Name = "LUNC", CurrentPrice = 0.00005067m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "EGLD", Name = "EGLD", CurrentPrice = 3.829m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ZRO", Name = "ZRO", CurrentPrice = 1.0606m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "FLOKI", Name = "FLOKI", CurrentPrice = 0.00002421m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "ONDO", Name = "ONDO", CurrentPrice = 0.34279m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "CHZ", Name = "CHZ", CurrentPrice = 0.01321m, LastUpdate = DateTime.UtcNow },
            new() { Symbol = "DOGS", Name = "DOGS", CurrentPrice = 0.000046334m, LastUpdate = DateTime.UtcNow }
        };
    }

    public async Task<List<Candle>> GetCandlesAsync(string symbol, string interval = "4h", int limit = 100)
    {
        // Tenta Crypto.com prima
        var cryptoComCandles = await TryGetCryptoComCandles(symbol, interval, limit);
        if (cryptoComCandles != null && cryptoComCandles.Count >= 50)
            return cryptoComCandles;

        // Fallback: Tenta Bybit
        var bybitCandles = await TryGetBybitCandles(symbol, interval, limit);
        if (bybitCandles != null && bybitCandles.Count >= 50)
            return bybitCandles;

        // Fallback finale: Candele demo
        return GenerateDemoCandles(symbol, limit);
    }

    private async Task<List<Candle>> TryGetCryptoComCandles(string symbol, string interval, int limit)
    {
        try
        {
            await RateLimitDelay();
            var pair = $"{symbol}_USDT";
            var url = $"{BaseUrl}/public/get-candlestick?instrument_name={pair}&timeframe={ConvertInterval(interval)}&limit={limit}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<CryptoComCandleResponse>(content);

            if (data?.Result?.Data == null || data.Result.Data.Count == 0)
                return null;

            var candles = new List<Candle>();
            foreach (var candleData in data.Result.Data)
            {
                try
                {
                    candles.Add(new Candle
                    {
                        Time = UnixTimeStampToDateTime(candleData.Time),
                        Open = decimal.Parse(candleData.Open),
                        High = decimal.Parse(candleData.High),
                        Low = decimal.Parse(candleData.Low),
                        Close = decimal.Parse(candleData.Close),
                        Volume = decimal.Parse(candleData.Volume)
                    });
                }
                catch
                {
                    continue;
                }
            }

            return candles.Count > 0 ? candles.OrderBy(c => c.Time).ToList() : null;
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private async Task<List<Candle>> TryGetBybitCandles(string symbol, string interval, int limit)
    {
        try
        {
            await RateLimitDelay();
            var pair = $"{symbol}USDT";
            var bybitInterval = ConvertToBybitInterval(interval);
            var url = $"{BybitBaseUrl}/market/kline?category=spot&symbol={pair}&interval={bybitInterval}&limit={limit}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync();
            dynamic data = JsonConvert.DeserializeObject(content);

            if (data?.result?.list == null)
                return null;

            var candles = new List<Candle>();
            foreach (var kline in data.result.list)
            {
                try
                {
                    candles.Add(new Candle
                    {
                        Time = UnixTimeStampToDateTime(long.Parse((string)kline[0])),
                        Open = decimal.Parse((string)kline[1]),
                        High = decimal.Parse((string)kline[2]),
                        Low = decimal.Parse((string)kline[3]),
                        Close = decimal.Parse((string)kline[4]),
                        Volume = decimal.Parse((string)kline[7])
                    });
                }
                catch
                {
                    continue;
                }
            }

            return candles.Count > 0 ? candles.OrderBy(c => c.Time).ToList() : null;
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private List<Candle> GenerateDemoCandles(string symbol, int limit)
    {
        var candles = new List<Candle>();
        var basePrice = GetSymbolBasePrice(symbol);
        var random = new Random();

        for (int i = limit; i > 0; i--)
        {
            var time = DateTime.UtcNow.AddHours(-i);
            var volatility = basePrice * 0.02m;
            var open = basePrice + (decimal)(random.NextDouble() - 0.5) * (volatility * 2);
            var close = open + (decimal)(random.NextDouble() - 0.5) * volatility;
            var high = Math.Max(open, close) + (decimal)random.NextDouble() * volatility;
            var low = Math.Min(open, close) - (decimal)random.NextDouble() * volatility;
            var volume = 1000000m + (decimal)(random.NextDouble() * 9000000);

            candles.Add(new Candle
            {
                Time = time,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });

            basePrice = close;
        }

        return candles.OrderBy(c => c.Time).ToList();
    }

    private decimal GetSymbolBasePrice(string symbol)
    {
        return symbol switch
        {
            "BTC" => 78745.68m,
            "ETH" => 2471.79m,
            "SOL" => 103.07m,
            "XRP" => 1.3791m,
            "ADA" => 0.19669m,
            "DOGE" => 0.42m,
            "BNB" => 620.50m,
            "LINK" => 28.75m,
            "AVAX" => 42.30m,
            "NEAR" => 8.45m,
            "MATIC" => 0.72m,
            "ARB" => 1.95m,
            "OP" => 3.25m,
            "ICP" => 15.40m,
            "UNI" => 18.95m,
            "AAVE" => 356.80m,
            "MKR" => 3850.00m,
            "YFI" => 28900.00m,
            "GMX" => 92.30m,
            _ => 10m
        };
    }

    private string ConvertInterval(string interval)
    {
        return interval switch
        {
            "1m" => "1m",
            "5m" => "5m",
            "15m" => "15m",
            "30m" => "30m",
            "1h" => "1h",
            "4h" => "4h",
            "1d" => "1d",
            _ => "4h"
        };
    }

    private string ConvertToBybitInterval(string interval)
    {
        return interval switch
        {
            "1m" => "1",
            "5m" => "5",
            "15m" => "15",
            "30m" => "30",
            "1h" => "60",
            "4h" => "240",
            "1d" => "D",
            _ => "240"
        };
    }

    private DateTime UnixTimeStampToDateTime(object timestamp)
    {
        var unixTime = long.Parse(timestamp.ToString()) / 1000;
        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(unixTime);
        return dateTime;
    }
}

public class CryptoComTickerResponse
{
    [JsonProperty("result")]
    public TickerResult Result { get; set; } = null!;
}

public class TickerResult
{
    [JsonProperty("data")]
    public List<TickerData> Data { get; set; } = null!;
}

public class TickerData
{
    [JsonProperty("i")]
    public string i { get; set; } = null!;

    [JsonProperty("a")]
    public string a { get; set; } = null!;

    [JsonProperty("k")]
    public string k { get; set; } = null!;
}

public class CryptoComCandleResponse
{
    [JsonProperty("result")]
    public CandleResult Result { get; set; }
}

public class CandleResult
{
    [JsonProperty("data")]
    public List<CandleData> Data { get; set; } = new();
}

public class CoinGeckoMarketData
{
    [JsonProperty("symbol")]
    public string Symbol { get; set; } = "";

    [JsonProperty("total_volume")]
    public decimal TotalVolume { get; set; }
}

public class CandleData
{
    [JsonProperty("o")]
    public string Open { get; set; } = "0";

    [JsonProperty("h")]
    public string High { get; set; } = "0";

    [JsonProperty("l")]
    public string Low { get; set; } = "0";

    [JsonProperty("c")]
    public string Close { get; set; } = "0";

    [JsonProperty("v")]
    public string Volume { get; set; } = "0";

    [JsonProperty("t")]
    public long Time { get; set; } = 0;
}
