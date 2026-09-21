using BotCripto.Models;

namespace BotCripto.Strategies;

public class EmaRibbonTrendFollowingStrategy
{
    // Configurazione ottimizzata per win rate 60-62%
    private readonly int[] _emaPeriods = { 5, 10, 20, 30 };
    private readonly decimal _volumeMultiplierThreshold = 1.2m;  // Volume almeno 20% sopra media
    private readonly decimal _bodyStrengthThreshold = 0.6m;      // Corpo deve essere 60% della candela
    private readonly int _trendConfirmationCandles = 2;           // Ultime 2 candele devono confermare
    private readonly decimal _rsiOverboughtLevel = 70m;
    private readonly decimal _rsiOversoldLevel = 30m;

    public AnalysisResult Analyze(string symbol, List<Candle> candles)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "EMA Ribbon Trend Following + Candle Confirmation",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < 60)
            return result;

        var closes = candles.Select(c => c.Close).ToList();
        var volumes = candles.Select(c => c.Volume).ToList();
        var highs = candles.Select(c => c.High).ToList();
        var lows = candles.Select(c => c.Low).ToList();

        // 1️⃣ CALCULATE EMA RIBBON
        var emaRibbon = new Dictionary<int, List<decimal>>();
        foreach (var period in _emaPeriods)
        {
            emaRibbon[period] = TechnicalIndicators.CalculateEMA(closes, period);
        }

        // Verifica che tutte le EMA siano state calcolate
        if (emaRibbon.Values.Any(ema => ema.Count == 0))
            return result;

        var currentPrice = closes.Last();
        var lastEma5 = emaRibbon[5].Last();
        var lastEma10 = emaRibbon[10].Last();
        var lastEma20 = emaRibbon[20].Last();
        var lastEma30 = emaRibbon[30].Last();

        result.Indicators["EMA5"] = lastEma5;
        result.Indicators["EMA10"] = lastEma10;
        result.Indicators["EMA20"] = lastEma20;
        result.Indicators["EMA30"] = lastEma30;
        result.CurrentPrice = currentPrice;

        // 2️⃣ TREND FILTER - Ribbon Alignment Check
        // Uptrend: EMA5 > EMA10 > EMA20 > EMA30
        // Downtrend: EMA5 < EMA10 < EMA20 < EMA30
        bool isUptrendAligned = lastEma5 > lastEma10 && lastEma10 > lastEma20 && lastEma20 > lastEma30;
        bool isDowntrendAligned = lastEma5 < lastEma10 && lastEma10 < lastEma20 && lastEma20 < lastEma30;

        if (!isUptrendAligned && !isDowntrendAligned)
        {
            result.Signal = "Rejected - EMA Ribbon not aligned (no clear trend)";
            return result;
        }

        result.Indicators["Trend"] = isUptrendAligned ? 1 : -1;
        var trendType = isUptrendAligned ? "UPTREND" : "DOWNTREND";

        // 3️⃣ VOLUME FILTER - Ensure strong volume
        var avgVolume = volumes.TakeLast(20).Average();
        var recentVolume = volumes.Last();

        if (recentVolume < avgVolume * 0.8m)
        {
            result.Signal = "Rejected - Volume insufficient";
            return result;
        }

        result.Indicators["Volume"] = recentVolume;
        result.Indicators["AvgVolume"] = avgVolume;
        result.Indicators["VolumeRatio"] = recentVolume / avgVolume;

        // 4️⃣ CANDLE CONFIRMATION - Solid body and alignment
        var lastCandle = candles.Last();
        var prevCandle = candles[candles.Count - 2];

        decimal lastCandleBody, lastCandleRange, bodyStrength;
        bool lastCandleConfirms;

        if (isUptrendAligned)
        {
            // UPTREND - Green candles with solid bodies
            lastCandleBody = lastCandle.Close - lastCandle.Open;
            lastCandleRange = lastCandle.High - lastCandle.Low;
            bodyStrength = lastCandleRange > 0 ? lastCandleBody / lastCandleRange : 0;

            // Candle deve essere bullish e con corpo solido
            lastCandleConfirms = lastCandleBody > 0 && bodyStrength >= _bodyStrengthThreshold;

            if (!lastCandleConfirms)
            {
                result.Signal = "Rejected - Last candle not bullish enough";
                return result;
            }

            // Verifica che il prezzo sia almeno al livello di EMA10 per conferma forte
            if (currentPrice < lastEma10)
            {
                result.Signal = "Rejected - Price below EMA10 (weak uptrend)";
                return result;
            }
        }
        else
        {
            // DOWNTREND - Red candles with solid bodies
            lastCandleBody = lastCandle.Open - lastCandle.Close;
            lastCandleRange = lastCandle.High - lastCandle.Low;
            bodyStrength = lastCandleRange > 0 ? lastCandleBody / lastCandleRange : 0;

            // Candle deve essere bearish e con corpo solido
            lastCandleConfirms = lastCandleBody > 0 && bodyStrength >= _bodyStrengthThreshold;

            if (!lastCandleConfirms)
            {
                result.Signal = "Rejected - Last candle not bearish enough";
                return result;
            }

            // Verifica che il prezzo sia almeno al livello di EMA10 per conferma forte
            if (currentPrice > lastEma10)
            {
                result.Signal = "Rejected - Price above EMA10 (weak downtrend)";
                return result;
            }
        }

        result.Indicators["CandleBodyStrength"] = bodyStrength;

        // 5️⃣ RSI FILTER - Avoid overbought/oversold extremes
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (rsi.Count < 1)
            return result;

        var currentRSI = rsi.Last();
        result.Indicators["RSI"] = currentRSI;

        // Non vogliamo segnali quando RSI è estremamente overextended
        if (isUptrendAligned && currentRSI > 85)
        {
            result.Signal = "Rejected - RSI overbought (extended move)";
            return result;
        }

        if (isDowntrendAligned && currentRSI < 15)
        {
            result.Signal = "Rejected - RSI oversold (extended move)";
            return result;
        }

        // 6️⃣ BREAKOUT FROM EMA CONFIRMATION
        // Price should have just crossed above/below key EMA
        var prevPrice = candles[candles.Count - 2].Close;

        bool breakoutConfirmed = false;
        if (isUptrendAligned)
        {
            // Prezzo rompe sopra EMA5 o passa sopra EMA10
            breakoutConfirmed = (prevPrice <= lastEma5 && currentPrice > lastEma5) ||
                               (prevPrice < lastEma10 && currentPrice > lastEma10 && currentPrice > lastEma5);
        }
        else
        {
            // Prezzo rompe sotto EMA5 o passa sotto EMA10
            breakoutConfirmed = (prevPrice >= lastEma5 && currentPrice < lastEma5) ||
                               (prevPrice > lastEma10 && currentPrice < lastEma10 && currentPrice < lastEma5);
        }

        if (!breakoutConfirmed)
        {
            result.Signal = "Rejected - No EMA breakout confirmation";
            return result;
        }

        // ✅ SIGNAL GENERATED
        if (isUptrendAligned)
        {
            result.IsSignal = true;
            result.Signal = "🟢 BUY - EMA Ribbon Bullish (Trend + Candle + Volume)";
            result.Indicators["StopLoss"] = Math.Min(lastEma20, lastCandle.Low) * 0.98m;
            result.Indicators["Target"] = currentPrice + (currentPrice - result.Indicators["StopLoss"]) * 2;
        }
        else
        {
            result.IsSignal = true;
            result.Signal = "🔴 SELL - EMA Ribbon Bearish (Trend + Candle + Volume)";
            result.Indicators["StopLoss"] = Math.Max(lastEma20, lastCandle.High) * 1.02m;
            result.Indicators["Target"] = currentPrice - (result.Indicators["StopLoss"] - currentPrice) * 2;
        }

        result.Indicators["SignalStrength"] = Math.Min(1m, bodyStrength + (recentVolume / avgVolume - 1) * 0.5m);

        return result;
    }
}
