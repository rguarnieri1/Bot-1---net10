using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private const int CandlesPerAnalysis = 300;  // stessa finestra del backtest
    private const int MinCandlesForAnalysis = 60; // minimo richiesto dalla strategia
    // Un segnale viene valutato solo entro questo tempo dalla chiusura della candela (niente ingressi in ritardo)
    private static readonly TimeSpan SignalFreshness = TimeSpan.FromMinutes(30);

    private readonly CryptoDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly EmaRibbonTrendFollowingStrategy _emaRibbonStrategy;
    private readonly RiskManager _riskManager;
    private Timer? _marketCheckTimer;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;
    private int _cycleRunning = 0;

    private readonly decimal _initialCapital;
    private readonly string _timeframe;
    private readonly TimeSpan _timeframeSpan;
    private decimal _currentAccountValue;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    public BotSchedulerService(decimal initialCapital, decimal maxTradeAmount, string timeframe, decimal rewardRiskRatio)
    {
        _initialCapital = initialCapital;
        _timeframe = timeframe;
        _timeframeSpan = BacktestService.TimeframeToTimeSpan(timeframe);
        _dataService = new CryptoDataService();
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _emaRibbonStrategy = new EmaRibbonTrendFollowingStrategy();
        _riskManager = new RiskManager(
            initialCapital,
            riskPercentPerTrade: 0.02m,      // 2% rischio per trade
            rewardRiskRatio: rewardRiskRatio, // take profit = R:R × distanza dello stop
            maxPositionSizePercent: 0.10m,    // Max 10% per trade
            commissionsPercent: 0.25m,        // 0.25% commissioni per lato
            taxRate: 0.26m,                   // 26% tasse
            maxPositionValue: maxTradeAmount  // importo massimo per trade (€)
        );

        _currentAccountValue = initialCapital;
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            Console.WriteLine("Bot è già in esecuzione.");
            return;
        }

        _isRunning = true;
        Console.WriteLine("\n🤖 Bot Cripto avviato! (paper trading: nessun ordine reale)");
        Console.WriteLine($"📊 Monitoraggio ogni 10 minuti, candele {_timeframe}...\n");

        // Esegui il primo controllo immediatamente
        await CheckMarketAsync();

        // Timer per il controllo ogni 10 minuti
        _marketCheckTimer = new Timer(
            async _ => await CheckMarketAsync(),
            null,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10));

        // Timer per il report settimanale (ogni lunedì alle 00:00)
        var now = DateTime.UtcNow;
        var nextMonday = now.AddDays((DayOfWeek.Monday - now.DayOfWeek + 7) % 7);
        if (nextMonday <= now)
            nextMonday = nextMonday.AddDays(7);

        var timeUntilMonday = nextMonday - now;
        _weeklyReportTimer = new Timer(
            _ => _reportingService.GenerateWeeklyReport(),
            null,
            timeUntilMonday,
            TimeSpan.FromDays(7));

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss} UTC");

        await Task.Delay(-1);
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _marketCheckTimer?.Dispose();
        _weeklyReportTimer?.Dispose();
        Console.WriteLine("\n🛑 Bot Cripto fermato.");
    }

    private async Task CheckMarketAsync()
    {
        // Il timer scatta ogni 10 minuti anche se il ciclo precedente non è finito: mai due cicli insieme,
        // altrimenti lo stesso segnale verrebbe registrato due volte.
        if (Interlocked.Exchange(ref _cycleRunning, 1) == 1)
        {
            Console.WriteLine("⏭️  Ciclo precedente ancora in corso: questo ciclo viene saltato.");
            return;
        }

        try
        {
            _checksPerformed++;
            var now = DateTime.UtcNow;
            Console.WriteLine($"\n⏱️  Ciclo #{_checksPerformed} - {now:yyyy-MM-dd HH:mm:ss} UTC");

            var candleCache = new Dictionary<string, List<Candle>>();
            async Task<List<Candle>> GetCandlesCachedAsync(string symbol)
            {
                if (!candleCache.TryGetValue(symbol, out var cached))
                {
                    cached = (await _dataService.GetCandlesAsync(symbol, _timeframe, CandlesPerAnalysis)).OrderBy(c => c.Time).ToList();
                    candleCache[symbol] = cached;
                }
                return cached;
            }

            // 1) Paper trading: chiudi i trade aperti che hanno toccato stop loss o target
            int tradesClosed = await CloseTradesAtStopOrTargetAsync(GetCandlesCachedAsync);

            // Trade gestiti dal paper trading (quelli registrati prima non hanno stop/target e vengono ignorati)
            var managedTrades = _reportingService.GetAllTrades().Where(t => t.IsPaperManaged).ToList();
            var openTrades = managedTrades.Where(t => t.Status == "Open").ToList();
            _currentAccountValue = _initialCapital + managedTrades.Where(t => t.Status == "Closed").Sum(t => t.Profit ?? 0);

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();

            void CountRejection(string category)
            {
                rejectionCounts.TryGetValue(category, out var c);
                rejectionCounts[category] = c + 1;

                _cumulativeRejectionCounts.TryGetValue(category, out var cc);
                _cumulativeRejectionCounts[category] = cc + 1;
            }

            // 2) I segnali si valutano solo su candele chiuse (come nel backtest) e solo subito dopo la chiusura
            var btcClosed = ClosedCandles(await GetCandlesCachedAsync("BTC"), now);
            var lastClosedTime = btcClosed.Count > 0 ? btcClosed.Last().Time : (DateTime?)null;
            var lastCloseEnd = lastClosedTime + _timeframeSpan;
            bool newCandle = lastCloseEnd != null && now - lastCloseEnd.Value <= SignalFreshness;

            if (!newCandle)
            {
                var nextClose = lastCloseEnd?.Add(_timeframeSpan);
                Console.WriteLine($"🕒 Nessuna candela {_timeframe} appena chiusa: ricerca segnali alla prossima chiusura ({nextClose:yyyy-MM-dd HH:mm} UTC).");
            }
            else
            {
                // Regime di mercato BTC sulla candela appena chiusa (filtro direzionale della strategia)
                var btcBullish = EmaRibbonTrendFollowingStrategy.IsBtcBullish(btcClosed);
                Console.WriteLine($"🕯️  Candela {_timeframe} chiusa alle {lastCloseEnd:yyyy-MM-dd HH:mm} UTC: ricerca segnali");
                Console.WriteLine($"₿ Regime BTC: {(btcBullish == null ? "sconosciuto (nessun segnale)" : btcBullish.Value ? "rialzista (solo long)" : "ribassista (solo short)")}");

                var cryptos = await _dataService.GetLargeCapCryptocurrenciesAsync();
                Console.WriteLine($"📈 Analizzando {cryptos.Count} criptovalute (max 500 per performance)...");

                var allTrades = _reportingService.GetAllTrades();
                var candidates = new List<(Cryptocurrency crypto, AnalysisResult analysis, List<Candle> candles)>();

                foreach (var crypto in cryptos)
                {
                    try
                    {
                        if (openTrades.Any(t => t.Symbol == crypto.Symbol))
                        {
                            CountRejection("Posizione già aperta sul simbolo");
                            continue;
                        }

                        if (allTrades.Any(t => t.Symbol == crypto.Symbol && t.SignalCandleTime == lastClosedTime))
                        {
                            CountRejection("Segnale di questa candela già registrato");
                            continue;
                        }

                        var candles = ClosedCandles(await GetCandlesCachedAsync(crypto.Symbol), now);

                        if (candles.Count < MinCandlesForAnalysis)
                        {
                            CountRejection($"Dati insufficienti (candele < {MinCandlesForAnalysis})");
                            continue;
                        }

                        if (candles.Last().Time != lastClosedTime)
                        {
                            CountRejection("Ultima candela non aggiornata");
                            continue;
                        }

                        // ✅ STRATEGIA PRINCIPALE: EMA Ribbon Trend Following + Candle Confirmation
                        var emaRibbonResult = _emaRibbonStrategy.Analyze(crypto.Symbol, candles, btcBullish);

                        if (emaRibbonResult.IsSignal)
                            candidates.Add((crypto, emaRibbonResult, candles));
                        else
                            CountRejection(ClassifyStrategyRejection(emaRibbonResult.Signal));

                        await Task.Delay(50);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠️  {crypto.Symbol}: {ex.Message}");
                    }
                }

                // Dal segnale più forte al più debole: quando il capitale non basta per tutti entrano prima i migliori
                foreach (var (crypto, emaRibbonResult, candles) in candidates.OrderByDescending(c => c.analysis.Indicators.GetValueOrDefault("SignalStrength")))
                {
                    var isLong = emaRibbonResult.Signal.Contains("BUY");
                    var positionResult = _riskManager.CalculatePosition(
                        crypto.Symbol,
                        crypto.CurrentPrice,
                        CalculateVolatility(candles),
                        openTrades,
                        _currentAccountValue,
                        isLong,
                        emaRibbonResult.Indicators.GetValueOrDefault("StopLoss")
                    );

                    if (!positionResult.IsValid)
                    {
                        signalsFiltered++;
                        CountRejection(ClassifyRiskRejection(positionResult.Reason));
                        continue;
                    }

                    // Niente leva: la posizione deve entrare nel capitale non ancora impegnato
                    var committed = openTrades.Sum(t => t.EntryPrice * t.PositionSize);
                    if (committed + crypto.CurrentPrice * positionResult.PositionSize > _currentAccountValue)
                    {
                        signalsFiltered++;
                        CountRejection("Capitale già impegnato");
                        continue;
                    }

                    emaRibbonResult.Indicators["StopLoss"] = positionResult.StopLossPrice;
                    emaRibbonResult.Indicators["Target"] = positionResult.TargetPrice;
                    emaRibbonResult.Indicators["PositionSize"] = positionResult.PositionSize;
                    emaRibbonResult.Indicators["RiskRewardRatio"] = positionResult.RiskRewardRatio;
                    emaRibbonResult.Indicators["Leverage"] = positionResult.LeverageRatio;
                    emaRibbonResult.Indicators["ExpectedProfit"] = positionResult.ExpectedProfit;

                    await _notificationService.SendNotificationAsync(emaRibbonResult);
                    openTrades.Add(_reportingService.RecordTrade(
                        crypto.Symbol,
                        crypto.CurrentPrice,
                        "EMA Ribbon Trend Following (Primary)",
                        isLong,
                        positionResult.StopLossPrice,
                        positionResult.TargetPrice,
                        positionResult.PositionSize,
                        lastClosedTime!.Value));

                    _signalsGenerated++;
                    _tradesRecorded++;
                    signalsFound++;
                }
            }

            // Report del ciclo
            Console.WriteLine($"\n✅ Ciclo completato:");
            Console.WriteLine($"   • Trade chiusi (stop/target): {tradesClosed}");
            Console.WriteLine($"   • Segnali trovati: {signalsFound}");
            Console.WriteLine($"   • Segnali filtrati (rischio/capitale): {signalsFiltered}");
            Console.WriteLine($"   • Trade aperti: {openTrades.Count} (capitale impegnato €{openTrades.Sum(t => t.EntryPrice * t.PositionSize):F2})");
            Console.WriteLine($"   • Account value (realizzato): €{_currentAccountValue:F2}");

            if (rejectionCounts.Count > 0)
            {
                Console.WriteLine("\n📊 Motivi di scarto in questo ciclo:");
                foreach (var kv in rejectionCounts.OrderByDescending(k => k.Value))
                {
                    Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                }
            }

            // Calcola e mostra metriche giornaliere ogni 10 cicli
            if (_checksPerformed % 10 == 0)
            {
                var metrics = _riskManager.CalculatePerformanceMetrics(managedTrades, _initialCapital);
                if (metrics.TotalTrades > 0)
                {
                    Console.WriteLine($"\n📊 Metriche cumulative paper trading (dopo {_checksPerformed} cicli):");
                    Console.WriteLine($"   • Trade chiusi: {metrics.TotalTrades}");
                    Console.WriteLine($"   • Win Rate: {metrics.WinRate:P}");
                    Console.WriteLine($"   • Profit Factor: {metrics.ProfitFactor:F2}");
                    Console.WriteLine($"   • Total P&L (netto commissioni): €{metrics.TotalProfit:F2}");
                    Console.WriteLine($"   • Expectancy: €{metrics.Expectancy:F2}");
                }

                if (_cumulativeRejectionCounts.Count > 0)
                {
                    Console.WriteLine($"\n📊 Motivi di scarto cumulativi (dopo {_checksPerformed} cicli):");
                    foreach (var kv in _cumulativeRejectionCounts.OrderByDescending(k => k.Value))
                    {
                        Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Errore nel controllo del mercato: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _cycleRunning, 0);
        }
    }

    // Candele il cui periodo è già terminato (Time è l'inizio della candela): esclude quella ancora in formazione.
    private List<Candle> ClosedCandles(List<Candle> candles, DateTime now) =>
        candles.Where(c => c.Time + _timeframeSpan <= now).ToList();

    // Paper trading: stessa regola del backtest. Un trade si chiude quando una candela successiva a quella
    // del segnale tocca lo stop loss o il target; se li tocca entrambi si assume lo stop (ipotesi conservativa).
    private async Task<int> CloseTradesAtStopOrTargetAsync(Func<string, Task<List<Candle>>> getCandlesAsync)
    {
        int closed = 0;
        var openTrades = _reportingService.GetAllTrades().Where(t => t.Status == "Open" && t.IsPaperManaged).ToList();

        foreach (var trade in openTrades)
        {
            try
            {
                var candles = await getCandlesAsync(trade.Symbol);
                foreach (var candle in candles.Where(c => c.Time > trade.SignalCandleTime))
                {
                    bool hitStop = trade.IsLong ? candle.Low <= trade.StopLoss : candle.High >= trade.StopLoss;
                    bool hitTarget = trade.IsLong ? candle.High >= trade.TargetPrice : candle.Low <= trade.TargetPrice;
                    if (!hitStop && !hitTarget)
                        continue;

                    var exitPrice = hitStop ? trade.StopLoss : trade.TargetPrice;
                    var profitCalc = _riskManager.CalculateProfitAndTaxes(
                        trade.EntryPrice, exitPrice, trade.PositionSize, !hitStop, trade.IsLong);
                    var profit = profitCalc.NetProfitBeforeTax; // tasse sul saldo netto del periodo, non per trade
                    var profitPercent = profit / (trade.EntryPrice * trade.PositionSize) * 100;

                    _reportingService.ClosePaperTrade(trade, exitPrice, profit, profitPercent, hitStop ? "Stop loss" : "Target");
                    Console.WriteLine($"   {(profit > 0 ? "✅" : "❌")} Chiuso {trade.Symbol} {(trade.IsLong ? "LONG" : "SHORT")} per {(hitStop ? "stop loss" : "target")}: €{profit:F2} ({profitPercent:F2}%)");
                    closed++;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  {trade.Symbol} (gestione trade aperto): {ex.Message}");
            }
        }

        return closed;
    }

    // Raggruppa i motivi di scarto della strategia in categorie leggibili per la diagnostica.
    private static string ClassifyStrategyRejection(string signal)
    {
        if (signal.Contains("EMA Ribbon not aligned"))
            return "Strategia: trend non allineato (EMA5/10/20/30)";
        if (signal.Contains("Volume insufficient"))
            return "Strategia: volume insufficiente";
        if (signal.Contains("candle not"))
            return "Strategia: candela non abbastanza forte";
        if (signal.Contains("below EMA10") || signal.Contains("above EMA10"))
            return "Strategia: prezzo non conferma vs EMA10";
        if (signal.Contains("RSI"))
            return "Strategia: RSI estremo";
        if (signal.Contains("EMA breakout"))
            return "Strategia: nessun breakout EMA confermato";
        if (signal.Contains("BTC regime"))
            return "Strategia: contro il regime BTC";
        if (signal.Contains("No volume data"))
            return "Strategia: volume assente";
        if (signal == "No Signal")
            return "Strategia: dati insufficienti per il calcolo";
        return $"Strategia: altro ({signal})";
    }

    // Raggruppa i motivi di scarto del RiskManager in categorie leggibili per la diagnostica.
    private static string ClassifyRiskRejection(string reason)
    {
        if (reason.Contains("Max concurrent trades"))
            return "Risk Manager: troppi trade aperti simultaneamente";
        if (reason.Contains("profit"))
            return "Risk Manager: profitto non copre commissioni";
        if (reason.Contains("Risk-reward"))
            return "Risk Manager: rapporto rischio/rendimento insufficiente";
        if (reason.Contains("Stop loss"))
            return "Risk Manager: errore calcolo stop loss";
        return $"Risk Manager: altro ({reason})";
    }

    private decimal CalculateVolatility(List<Candle> candles)
    {
        if (candles.Count < 20)
            return 0.02m;

        var closes = candles.TakeLast(20).Select(c => c.Close).ToList();
        var returns = new List<decimal>();

        for (int i = 1; i < closes.Count; i++)
        {
            returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
        }

        var avg = returns.Average();
        var variance = returns.Sum(r => (r - avg) * (r - avg)) / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return Math.Max(0.01m, Math.Min(stdDev, 0.08m));
    }
}
