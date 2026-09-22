using BotCripto.Models;

namespace BotCripto.Services;

public class NotificationService
{
    private readonly string _logDirectory;

    public NotificationService()
    {
        _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public Task SendNotificationAsync(AnalysisResult result)
    {
        var message = FormatNotification(result);

        LogToFile(message);
        ConsoleNotification(result);

        return Task.CompletedTask;
    }

    private void LogToFile(string message)
    {
        try
        {
            var logFile = Path.Combine(_logDirectory, $"signals_{DateTime.UtcNow:yyyy-MM-dd}.log");
            File.AppendAllText(logFile, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC - {message}\n");
        }
        catch { }
    }

    private void ConsoleNotification(AnalysisResult result)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.ForegroundColor = result.Signal.Contains("BUY") ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"📊 SEGNALE: {result.Signal}");
        Console.ResetColor();
        Console.WriteLine($"   Crypto: {result.Symbol}");
        Console.WriteLine($"   Prezzo: ${result.CurrentPrice:F8}");
        Console.WriteLine($"   Strategia: {result.StrategyName}");
        Console.WriteLine($"   Ora: {result.AnalysisTime:yyyy-MM-dd HH:mm:ss}");

        foreach (var indicator in result.Indicators)
        {
            Console.WriteLine($"   {indicator.Key}: {indicator.Value:F4}");
        }

        Console.WriteLine(new string('=', 60) + "\n");

        Console.Beep(800, 500);
    }

    private string FormatNotification(AnalysisResult result)
    {
        return $"[{result.StrategyName}] {result.Symbol}: {result.Signal} @ ${result.CurrentPrice:F8}";
    }
}
