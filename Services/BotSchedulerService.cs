using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private readonly CryptoDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly EmaRibbonTrendFollowingStrategy _emaRibbonStrategy;
    private readonly RiskManager _riskManager;
    private Timer? _marketCheckTimer;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;

    private decimal _currentAccountValue = 1000m;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    public BotSchedulerService(decimal initialCapital = 1000m)
    {
        _dataService = new CryptoDataService();
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _emaRibbonStrategy = new EmaRibbonTrendFollowingStrategy();
        _riskManager = new RiskManager(
            initialCapital,
            riskPercentPerTrade: 0.02m,      // 2% rischio per trade
            rewardRiskRatio: 2.0m,            // 2:1 R:R
            maxPositionSizePercent: 0.10m,    // Max 10% per trade
            maxLeverage: 2.0m,                // Max 2.0x leva
            commissionsPercent: 0.6m,         // 0.6% commissioni
            taxRate: 0.26m                    // 26% tasse
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
        Console.WriteLine("\n🤖 Bot Cripto avviato!");
        Console.WriteLine("📊 Monitoraggio ogni 60 minuti...\n");

        // Esegui il primo controllo immediatamente
        await CheckMarketAsync();

        // Timer per il controllo ogni 60 minuti
        _marketCheckTimer = new Timer(
            async _ => await CheckMarketAsync(),
            null,
            TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(60));

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

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss}");

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
        try
        {
            _checksPerformed++;
            Console.WriteLine($"\n⏱️  Ciclo #{_checksPerformed} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Recupera tutte le criptovalute
            var cryptos = await _dataService.GetLargeCapCryptocurrenciesAsync();
            Console.WriteLine($"📈 Analizzando {cryptos.Count} criptovalute (max 500 per performance)...");

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();
            var openTrades = _reportingService.GetAllTrades().Where(t => t.Status == "Open").ToList();

            void CountRejection(string category)
            {
                rejectionCounts.TryGetValue(category, out var c);
                rejectionCounts[category] = c + 1;

                _cumulativeRejectionCounts.TryGetValue(category, out var cc);
                _cumulativeRejectionCounts[category] = cc + 1;
            }

            foreach (var crypto in cryptos)
            {
                try
                {
                    // Recupera candele per l'analisi
                    var candles = await _dataService.GetCandlesAsync(crypto.Symbol, "4h", 100);

                    if (candles.Count < 50)
                    {
                        CountRejection("Dati insufficienti (candele < 50)");
                        continue;
                    }

                    var volatilityPercent = CalculateVolatility(candles);

                    // ✅ STRATEGIA PRINCIPALE: EMA RIBBON TREND FOLLOWING

                    // 1️⃣ EMA Ribbon Trend Following + Candle Confirmation (Principale - Win Rate 60-62%)
                    var emaRibbonResult = _emaRibbonStrategy.Analyze(crypto.Symbol, candles);

                    if (emaRibbonResult.IsSignal)
                    {
                        // Valida con RiskManager
                        var positionResult = _riskManager.CalculatePosition(
                            crypto.Symbol,
                            crypto.CurrentPrice,
                            volatilityPercent,
                            openTrades,
                            _currentAccountValue
                        );

                        if (positionResult.IsValid)
                        {
                            emaRibbonResult.Indicators["PositionSize"] = positionResult.PositionSize;
                            emaRibbonResult.Indicators["RiskRewardRatio"] = positionResult.RiskRewardRatio;
                            emaRibbonResult.Indicators["Leverage"] = positionResult.LeverageRatio;
                            emaRibbonResult.Indicators["ExpectedProfit"] = positionResult.ExpectedProfit;

                            await _notificationService.SendNotificationAsync(emaRibbonResult);
                            _reportingService.RecordTrade(
                                crypto.Symbol,
                                crypto.CurrentPrice,
                                "EMA Ribbon Trend Following (Primary)");

                            _signalsGenerated++;
                            _tradesRecorded++;
                            signalsFound++;
                        }
                        else
                        {
                            signalsFiltered++;
                            CountRejection(ClassifyRiskRejection(positionResult.Reason));
                        }
                    }
                    else
                    {
                        CountRejection(ClassifyStrategyRejection(emaRibbonResult.Signal));
                    }

                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  {crypto.Symbol}: {ex.Message}");
                }
            }

            // Report del ciclo
            Console.WriteLine($"\n✅ Ciclo completato:");
            Console.WriteLine($"   • Segnali trovati: {signalsFound}");
            Console.WriteLine($"   • Segnali filtrati (rischio): {signalsFiltered}");
            Console.WriteLine($"   • Trade aperti: {openTrades.Count}");
            Console.WriteLine($"   • Account value (stimato): €{_currentAccountValue:F2}");

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
                var allTrades = _reportingService.GetAllTrades();
                var metrics = _riskManager.CalculatePerformanceMetrics(allTrades, 1000m);
                if (metrics.TotalTrades > 0)
                {
                    Console.WriteLine($"\n📊 Metriche cumulative (dopo {_checksPerformed} cicli):");
                    Console.WriteLine($"   • Win Rate: {metrics.WinRate:P}");
                    Console.WriteLine($"   • Profit Factor: {metrics.ProfitFactor:F2}");
                    Console.WriteLine($"   • Total P&L: €{metrics.TotalProfit:F2}");
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
    }

    // Raggruppa i motivi di scarto della strategia in categorie leggibili per la diagnostica.
    private static string ClassifyStrategyRejection(string signal)
    {
        if (signal.Contains("EMA Ribbon not aligned"))
            return "Strategia: trend non allineato (EMA5/10/20/50)";
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
        if (signal == "No Signal")
            return "Strategia: dati insufficienti per il calcolo";
        return $"Strategia: altro ({signal})";
    }

    // Raggruppa i motivi di scarto del RiskManager in categorie leggibili per la diagnostica.
    private static string ClassifyRiskRejection(string reason)
    {
        if (reason.Contains("Leverage"))
            return "Risk Manager: leva troppo alta";
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
