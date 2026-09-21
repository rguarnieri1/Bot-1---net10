using Newtonsoft.Json;
using BotCripto.Models;

namespace BotCripto.Services;

public class CryptoDataService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.crypto.com/v2";
    private const string BybitBaseUrl = "https://api.bybit.com/v5";
    private const string CoinGeckoBaseUrl = "https://api.coingecko.com/api/v3";
    private const decimal MinVolume24hUsd = 2_000_000m;
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

        // Nessuna API reale disponibile: mai dati demo, si salta il ciclo
        Console.WriteLine("❌ Tutte le API di mercato sono fallite. Nessun dato demo: ciclo saltato.");
        return new List<Cryptocurrency>();
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

        // Nessuna API reale disponibile: mai candele demo, il simbolo verrà saltato
        Console.WriteLine($"   ❌ {symbol}: nessuna candela reale disponibile (mai dati demo)");
        return new List<Candle>();
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
