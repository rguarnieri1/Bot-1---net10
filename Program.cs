using BotCripto.Services;

Console.WriteLine(@"
╔════════════════════════════════════════════════════════╗
║       🤖 BOT 1 - ERTF-Crypto - VERSIONE 1.1            ║
║                 Backtest + Live Trading                ║
╚════════════════════════════════════════════════════════╝
");

// Capitale totale e importo massimo per singolo trade (€), usati sia dal bot live sia dal backtest
const decimal InitialCapital = 150m;
const decimal MaxTradeAmount = 10m;

// Check se è backtest mode
var cmdArgs = Environment.GetCommandLineArgs();
bool isBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest";
bool isRealBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest-real";

if (isBacktestMode)
{
    await RunBacktestAsync();
}
else if (isRealBacktestMode)
{
    // --backtest-real [giorni]  oppure  --backtest-real yyyy-MM-dd yyyy-MM-dd
    var endDate = DateTime.UtcNow;
    var startDate = endDate.AddDays(-30);
    var dateStyle = System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal;
    if (cmdArgs.Length > 3
        && DateTime.TryParseExact(cmdArgs[2], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var fromDate)
        && DateTime.TryParseExact(cmdArgs[3], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var toDate))
    {
        startDate = fromDate;
        endDate = toDate.AddDays(1).AddTicks(-1); // data finale inclusa
    }
    else if (cmdArgs.Length > 2 && int.TryParse(cmdArgs[2], out var parsedDays))
    {
        startDate = endDate.AddDays(-parsedDays);
    }
    await RunRealBacktestAsync(startDate, endDate);
}
else
{
    await RunLiveAsync();
}

async Task RunLiveAsync()
{
    var scheduler = new BotSchedulerService(InitialCapital, MaxTradeAmount);

    Console.WriteLine("Strategia Principale Attiva:");
    Console.WriteLine("  ⭐ EMA Ribbon Trend Following + Candle Confirmation");
    Console.WriteLine("     • Win Rate Atteso: 60-62%");
    Console.WriteLine("     • Configurazione: EMA 5, 10, 20, 30");
    Console.WriteLine("     • Filtri: Volume, Candle Body, RSI, Breakout Confirmation");
    Console.WriteLine("\nImpostazioni:");
    Console.WriteLine($"  • Capitale Iniziale: €{InitialCapital:F2}");
    Console.WriteLine("  • Intervallo Monitoraggio: 10 minuti");
    Console.WriteLine("  • Max Crypto: 500");
    Console.WriteLine($"  • Risk per Trade: 2% (€{InitialCapital * 0.02m:F2})");
    Console.WriteLine($"  • Importo massimo per trade: €{Math.Min(InitialCapital * 0.10m, MaxTradeAmount):F2}");
    Console.WriteLine("  • Report Settimanale: Lunedì 00:00");
    Console.WriteLine("  • Notifiche Desktop: Abilitate");
    Console.WriteLine("  • Tracking Metriche: Ogni 10 cicli");

    try
    {
        await scheduler.StartAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore: {ex.Message}");
    }
    finally
    {
        scheduler.Stop();
    }
}

async Task RunRealBacktestAsync(DateTime startDate, DateTime endDate)
{
    Console.WriteLine($"\n🔍 MODALITA' BACKTEST SU DATI STORICI REALI ATTIVATA ({startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd})\n");

    var backtestService = new BacktestService(InitialCapital, MaxTradeAmount);

    try
    {
        var dataService = new CryptoDataService();
        var cryptos = await dataService.GetLargeCapCryptocurrenciesAsync();
        var symbols = cryptos.Select(c => c.Symbol).ToList();

        var result = await backtestService.RunBacktestAsync(symbols, startDate, endDate, "4h");
        backtestService.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

async Task RunBacktestAsync()
{
    Console.WriteLine("\n🔍 MODALITA' BACKTEST SINTETICO ATTIVATA\n");

    var backtest = new SyntheticBacktest(initialCapital: 100m);

    try
    {
        // Esegui backtest su 365 giorni (1 anno)
        // con 20 simboli, 55% win-rate atteso, 8 trade per simbolo
        var result = backtest.RunBacktest(
            numSymbols: 20,
            daysOfData: 365,
            expectedWinRate: 0.55m,
            tradesPerSymbol: 8
        );

        // Stampa report dettagliato
        backtest.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

