using BotCripto.Models;
using BotCripto.Strategies;
using System.Text;

namespace BotCripto.Services;

public class BacktestService
{
    private const int WarmupCandles = 60; // candele minime richieste dalla strategia prima di poter generare segnali

    private readonly CryptoDataService _dataService;
    private readonly RiskManager _riskManager;
    private readonly EmaRibbonTrendFollowingStrategy _strategy;
    private readonly decimal _initialCapital;

    public BacktestService(decimal initialCapital = 1000m)
    {
        _initialCapital = initialCapital;
        _dataService = new CryptoDataService();
        _strategy = new EmaRibbonTrendFollowingStrategy();
        _riskManager = new RiskManager(
            initialCapital,
            riskPercentPerTrade: 0.02m,
            rewardRiskRatio: 2.0m,
            maxPositionSizePercent: 0.10m,
            commissionsPercent: 0.6m,
            taxRate: 0.26m
        );
    }

    // Esegue davvero la strategia EMA Ribbon + RiskManager candela per candela su dati storici reali
    // (Crypto.com/Bybit), aprendo e chiudendo posizioni quando stop loss o target vengono toccati.
    public async Task<BacktestResult> RunBacktestAsync(
        List<string> symbols,
        DateTime startDate,
        DateTime endDate,
        string timeframe = "4h")
    {
        Console.WriteLine($"\n🔍 INIZIO BACKTEST (dati storici reali)");
        Console.WriteLine($"📅 Periodo richiesto: {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}");
        Console.WriteLine($"⏱️  Timeframe: {timeframe}");
        Console.WriteLine($"🪙 Simboli: {symbols.Count}\n");

        var backtestResult = new BacktestResult
        {
            StartDate = startDate,
            EndDate = endDate,
            Timeframe = timeframe,
            SymbolsAnalyzed = symbols.Count,
            InitialCapital = _initialCapital,
            Trades = new List<Trade>(),
            AnalysisResults = new List<AnalysisResult>()
        };

        int totalSignals = 0;
        int totalFiltered = 0;
        int candleSetsAnalyzed = 0;
        DateTime? earliestTestedCandle = null;
        DateTime? latestTestedCandle = null;

        foreach (var symbol in symbols)
        {
            try
            {
                var candles = await _dataService.GetCandlesAsync(symbol, timeframe, 300);
                if (candles.Count < WarmupCandles + 20)
                {
                    continue; // troppo pochi dati per un warmup + una finestra di test minima
                }

                candles = candles.OrderBy(c => c.Time).ToList();
                candleSetsAnalyzed++;

                Trade? openTrade = null;
                bool openIsLong = false;
                decimal openStopLoss = 0m;
                decimal openTarget = 0m;
                decimal openPositionSize = 0m;

                for (int i = WarmupCandles; i < candles.Count; i++)
                {
                    var currentCandle = candles[i];
                    if (currentCandle.Time > endDate)
                        break;

                    var inTestWindow = currentCandle.Time >= startDate;

                    // 1) Se c'è una posizione aperta su questo simbolo, verifica se stop/target sono stati toccati
                    if (openTrade != null)
                    {
                        bool hitStop = openIsLong ? currentCandle.Low <= openStopLoss : currentCandle.High >= openStopLoss;
                        bool hitTarget = openIsLong ? currentCandle.High >= openTarget : currentCandle.Low <= openTarget;

                        if (hitStop || hitTarget)
                        {
                            // Se in una stessa candela vengono toccati entrambi, assumiamo lo stop loss (ipotesi conservativa)
                            var exitPrice = hitStop ? openStopLoss : openTarget;
                            var isWin = !hitStop;

                            var profitCalc = _riskManager.CalculateProfitAndTaxes(
                                openTrade.EntryPrice, exitPrice, openPositionSize, isWin);

                            openTrade.ExitPrice = exitPrice;
                            openTrade.CloseTime = currentCandle.Time;
                            openTrade.Status = "Closed";
                            openTrade.Profit = profitCalc.NetProfit;
                            openTrade.ProfitPercentage = profitCalc.ProfitPercent;

                            backtestResult.Trades.Add(openTrade);
                            openTrade = null;
                        }
                    }

                    // 2) Se non c'è (più) una posizione aperta, cerca un nuovo segnale
                    //    (i nuovi trade si aprono solo dentro la finestra [startDate, endDate] richiesta)
                    if (openTrade == null && inTestWindow)
                    {
                        var lookback = candles.Take(i + 1).ToList();
                        var analysis = _strategy.Analyze(symbol, lookback);

                        if (analysis.IsSignal)
                        {
                            totalSignals++;

                            var sizing = _riskManager.CalculatePosition(
                                symbol,
                                currentCandle.Close,
                                0m,
                                new List<Trade>(),
                                _initialCapital);

                            if (sizing.IsValid)
                            {
                                earliestTestedCandle ??= currentCandle.Time;
                                latestTestedCandle = currentCandle.Time;

                                openIsLong = analysis.Signal.Contains("BUY");
                                openStopLoss = sizing.StopLossPrice;
                                openTarget = sizing.TargetPrice;
                                openPositionSize = sizing.PositionSize;
                                openTrade = new Trade
                                {
                                    Symbol = symbol,
                                    OpenTime = currentCandle.Time,
                                    EntryPrice = currentCandle.Close,
                                    Strategy = analysis.StrategyName,
                                    Status = "Open"
                                };
                            }
                            else
                            {
                                totalFiltered++;
                            }
                        }
                    }
                }

                // Una posizione ancora aperta a fine dati storici non ha un esito: la scartiamo
                // dalle metriche (che considerano solo i trade "Closed"), non viene aggiunta ai risultati.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  {symbol}: {ex.Message}");
            }
        }

        backtestResult.Metrics = _riskManager.CalculatePerformanceMetrics(
            backtestResult.Trades,
            backtestResult.InitialCapital
        );

        backtestResult.TotalSignalsGenerated = totalSignals;
        backtestResult.TotalSignalsFiltered = totalFiltered;
        backtestResult.CandleSetsAnalyzed = candleSetsAnalyzed;
        backtestResult.ActualStartDate = earliestTestedCandle;
        backtestResult.ActualEndDate = latestTestedCandle;

        Console.WriteLine($"\n✅ BACKTEST COMPLETATO ({backtestResult.Trades.Count} trade chiusi da {candleSetsAnalyzed} simboli con dati sufficienti)");

        return backtestResult;
    }

    public void PrintBacktestReport(BacktestResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine("\n" + new string('═', 80));
        sb.AppendLine("📊 BACKTEST REPORT - BOT CRIPTO V1.1 (dati storici reali)");
        sb.AppendLine(new string('═', 80));

        sb.AppendLine($"\n📅 PERIODO TEST:");
        sb.AppendLine($"   • Richiesto: {result.StartDate:yyyy-MM-dd} → {result.EndDate:yyyy-MM-dd}");
        if (result.ActualStartDate.HasValue && result.ActualEndDate.HasValue)
        {
            sb.AppendLine($"   • Trade effettivamente aperti tra: {result.ActualStartDate:yyyy-MM-dd HH:mm} → {result.ActualEndDate:yyyy-MM-dd HH:mm}");
        }
        sb.AppendLine($"   • Timeframe: {result.Timeframe}");

        sb.AppendLine($"\n📈 COPERTURA ANALISI:");
        sb.AppendLine($"   • Simboli richiesti: {result.SymbolsAnalyzed}");
        sb.AppendLine($"   • Simboli con dati storici sufficienti: {result.CandleSetsAnalyzed}");
        sb.AppendLine($"   • Segnali generati dalla strategia: {result.TotalSignalsGenerated}");
        sb.AppendLine($"   • Segnali scartati dal RiskManager: {result.TotalSignalsFiltered}");
        var totalCandidati = result.TotalSignalsGenerated;
        if (totalCandidati > 0)
            sb.AppendLine($"   • Tasso di scarto RiskManager: {(decimal)result.TotalSignalsFiltered / totalCandidati * 100:F1}%");

        sb.AppendLine($"\n💼 ACCOUNT METRICS (al netto di commissioni e tasse):");
        sb.AppendLine($"   • Capitale iniziale: €{result.InitialCapital:F2}");
        sb.AppendLine($"   • P&L netto totale: €{result.Metrics.TotalProfit:F2}");
        sb.AppendLine($"   • ROI netto: {result.Metrics.ROI:F2}%");

        sb.AppendLine($"\n🎯 PERFORMANCE METRICS:");
        sb.AppendLine($"   • Trade chiusi: {result.Metrics.TotalTrades}");
        sb.AppendLine($"   • Vincenti: {result.Metrics.WinningTrades}");
        sb.AppendLine($"   • Perdenti: {result.Metrics.LosingTrades}");
        sb.AppendLine($"   • Win Rate: {result.Metrics.WinRate:P2}");
        sb.AppendLine($"   • Profit Factor: {result.Metrics.ProfitFactor:F2}");
        sb.AppendLine($"   • Expectancy: €{result.Metrics.Expectancy:F2}/trade (netto)");

        sb.AppendLine($"\n💰 WIN/LOSS ANALYSIS (nette):");
        sb.AppendLine($"   • Vincita media: €{result.Metrics.AverageWin:F2}");
        sb.AppendLine($"   • Perdita media: €{result.Metrics.AverageLoss:F2}");
        sb.AppendLine($"   • Serie massima di vincite consecutive: {result.Metrics.MaxConsecutiveWins}");
        sb.AppendLine($"   • Serie massima di perdite consecutive: {result.Metrics.MaxConsecutiveLosses}");

        sb.AppendLine("\n" + new string('═', 80) + "\n");

        var report = sb.ToString();
        Console.WriteLine(report);

        SaveBacktestReport(report);
    }

    private void SaveBacktestReport(string report)
    {
        try
        {
            var reportsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Reports");
            Directory.CreateDirectory(reportsDir);

            var fileName = $"Backtest_Report_{DateTime.UtcNow:yyyy-MM-dd_HHmmss}.txt";
            var filePath = Path.Combine(reportsDir, fileName);

            File.WriteAllText(filePath, report);
            Console.WriteLine($"💾 Report salvato: {filePath}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore nel salvataggio report: {ex.Message}");
        }
    }
}

public class BacktestResult
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? ActualStartDate { get; set; }
    public DateTime? ActualEndDate { get; set; }
    public string? Timeframe { get; set; }
    public int SymbolsAnalyzed { get; set; }
    public int CandleSetsAnalyzed { get; set; }
    public decimal InitialCapital { get; set; }
    public int TotalSignalsGenerated { get; set; }
    public int TotalSignalsFiltered { get; set; }
    public List<Trade> Trades { get; set; } = new();
    public List<AnalysisResult> AnalysisResults { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
}
