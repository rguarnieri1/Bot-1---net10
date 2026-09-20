using Newtonsoft.Json;

namespace BotCripto.Models;

public class Cryptocurrency
{
    public string Symbol { get; set; }
    public string Name { get; set; }
    public decimal CurrentPrice { get; set; }
    public DateTime LastUpdate { get; set; }
}

public class Candle
{
    public DateTime Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}

public class AnalysisResult
{
    public string Symbol { get; set; }
    public string StrategyName { get; set; }
    public bool IsSignal { get; set; }
    public string Signal { get; set; }
    public decimal CurrentPrice { get; set; }
    public DateTime AnalysisTime { get; set; }
    public Dictionary<string, decimal> Indicators { get; set; } = new();
}

public class Trade
{
    [JsonProperty("Id")]
    public string Id { get; set; }

    [JsonProperty("Symbol")]
    public string Symbol { get; set; }

    [JsonProperty("OpenTime")]
    public DateTime OpenTime { get; set; }

    [JsonProperty("CloseTime")]
    public DateTime? CloseTime { get; set; }

    [JsonProperty("EntryPrice")]
    public decimal EntryPrice { get; set; }

    [JsonProperty("ExitPrice")]
    public decimal? ExitPrice { get; set; }

    [JsonProperty("Strategy")]
    public string Strategy { get; set; }

    [JsonProperty("Status")]
    public string Status { get; set; }

    [JsonProperty("Profit")]
    public decimal? Profit { get; set; }

    [JsonProperty("ProfitPercentage")]
    public decimal? ProfitPercentage { get; set; }

    public Trade()
    {
        Id = Guid.NewGuid().ToString();
        Status = "Open";
    }
}
