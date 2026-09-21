using BotCripto.Services;

Console.WriteLine(@"
╔════════════════════════════════════════════════════════╗
║       🤖 BOT 1 - ERTF-Crypto - VERSIONE 1.1            ║
║                 Backtest + Live Trading                ║
╚════════════════════════════════════════════════════════╝
");

// Check se è backtest mode
var cmdArgs = Environment.GetCommandLineArgs();
bool isBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest";

if (isBacktestMode)
{
    await RunBacktestAsync();
}
else
{
    await RunLiveAsync();
}

async Task RunLiveAsync()
{
    var scheduler = new BotSchedulerService(initialCapital: 1000m);

    Console.WriteLine("Strategia Principale Attiva:");
    Console.WriteLine("  ⭐ EMA Ribbon Trend Following + Candle Confirmation");
    Console.WriteLine("     • Win Rate Atteso: 60-62%");
    Console.WriteLine("     • Configurazione: EMA 5, 10, 20, 50");
    Console.WriteLine("     • Filtri: Volume, Candle Body, RSI, Breakout Confirmation");
    Console.WriteLine("\nImpostazioni:");
    Console.WriteLine("  • Capitale Iniziale: €1000.00");
    Console.WriteLine("  • Intervallo Monitoraggio: 60 minuti");
    Console.WriteLine("  • Max Crypto: 500");
    Console.WriteLine("  • Risk per Trade: 2% (€20.00)");
    Console.WriteLine("  • Max Position Size: 10% (€100.00)");
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

