using Newtonsoft.Json;
using BotCripto.Models;
using System.Text;

namespace BotCripto.Services;

public class ReportingService
{
    private readonly string _dataDirectory;
    private readonly string _tradesFile;
    private List<Trade> _trades = new();

    public ReportingService()
    {
        _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        Directory.CreateDirectory(_dataDirectory);
        _tradesFile = Path.Combine(_dataDirectory, "trades.json");
        LoadTrades();
    }

    public void RecordTrade(string symbol, decimal entryPrice, string strategy)
    {
        var trade = new Trade
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            OpenTime = DateTime.UtcNow,
            Strategy = strategy,
            Status = "Open"
        };

        _trades.Add(trade);
        SaveTrades();
    }

    public Task CloseTradeAsync(string symbol, decimal exitPrice)
    {
        var trade = _trades.FirstOrDefault(t => t.Symbol == symbol && t.Status == "Open");
        if (trade != null)
        {
            trade.ExitPrice = exitPrice;
            trade.CloseTime = DateTime.UtcNow;
            trade.Status = "Closed";
            trade.Profit = exitPrice - trade.EntryPrice;
            trade.ProfitPercentage = (trade.Profit.Value / trade.EntryPrice) * 100;
            SaveTrades();
            LogClosedTrade(trade);
        }

        return Task.CompletedTask;
    }

    private void LogClosedTrade(Trade trade)
    {
        try
        {
            var logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logsDirectory);

            var logFilePath = Path.Combine(logsDirectory, "Operazioni_chiuse.log");

            var logEntry = new StringBuilder();
            logEntry.AppendLine($"\n{'═'} OPERAZIONE CHIUSA - {trade.CloseTime:yyyy-MM-dd HH:mm:ss} {'═'}");
            logEntry.AppendLine($"Simbolo: {trade.Symbol}");
            logEntry.AppendLine($"Strategia: {trade.Strategy}");
            logEntry.AppendLine($"Prezzo Entrata: ${trade.EntryPrice:F8}");
            logEntry.AppendLine($"Prezzo Uscita: ${trade.ExitPrice:F8}");
            logEntry.AppendLine($"Profitto: ${trade.Profit:F8}");
            logEntry.AppendLine($"Percentuale: {trade.ProfitPercentage:F2}%");
            logEntry.AppendLine($"Stato: {trade.Status}");
            logEntry.AppendLine($"Apertura: {trade.OpenTime:yyyy-MM-dd HH:mm:ss}");
            logEntry.AppendLine($"Chiusura: {trade.CloseTime:yyyy-MM-dd HH:mm:ss}");
            logEntry.AppendLine($"ID: {trade.Id}");
            logEntry.AppendLine(new string('═', 60));

            File.AppendAllText(logFilePath, logEntry.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Errore nel salvataggio del log di chiusura: {ex.Message}");
        }
    }

    public void GenerateWeeklyReport()
    {
        var weekStartDate = DateTime.UtcNow.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
        var weekEndDate = weekStartDate.AddDays(7);

        var weekTrades = _trades.Where(t =>
            t.OpenTime >= weekStartDate && t.OpenTime < weekEndDate).ToList();

        var closedTrades = weekTrades.Where(t => t.Status == "Closed").ToList();
        var openTrades = weekTrades.Where(t => t.Status == "Open").ToList();

        var totalProfit = closedTrades.Sum(t => t.Profit ?? 0);
        var totalProfitPercentage = closedTrades.Count > 0
            ? closedTrades.Average(t => t.ProfitPercentage ?? 0)
            : 0;

        var winningTrades = closedTrades.Count(t => t.Profit > 0);
        var winRate = closedTrades.Count > 0 ? (winningTrades / (decimal)closedTrades.Count) * 100 : 0;

        var report = new StringBuilder();
        report.AppendLine("\n" + new string('═', 70));
        report.AppendLine($"📈 REPORT SETTIMANALE - {weekStartDate:yyyy-MM-dd} a {weekEndDate:yyyy-MM-dd}");
        report.AppendLine(new string('═', 70));
        report.AppendLine($"\n📊 STATISTICHE GENERALI:");
        report.AppendLine($"   • Operazioni Totali: {weekTrades.Count}");
        report.AppendLine($"   • Operazioni Chiuse: {closedTrades.Count}");
        report.AppendLine($"   • Operazioni Aperte: {openTrades.Count}");

        report.AppendLine($"\n💰 PERFORMANCE:");
        report.AppendLine($"   • Profitto Totale: ${totalProfit:F2}");
        report.AppendLine($"   • ROI Medio: {totalProfitPercentage:F2}%");
        report.AppendLine($"   • Win Rate: {winRate:F2}%");

        report.AppendLine($"\n🎯 OPERAZIONI CHIUSE:");
        if (closedTrades.Count > 0)
        {
            foreach (var trade in closedTrades.OrderByDescending(t => t.CloseTime))
            {
                var profitColor = trade.Profit >= 0 ? "✅" : "❌";
                report.AppendLine(
                    $"   {profitColor} {trade.Symbol}: Entry ${trade.EntryPrice:F2} → Exit ${trade.ExitPrice:F2} " +
                    $"({trade.ProfitPercentage:F2}%) | {trade.Strategy}");
            }
        }
        else
        {
            report.AppendLine("   Nessuna operazione chiusa questa settimana");
        }

        report.AppendLine($"\n📂 OPERAZIONI APERTE:");
        if (openTrades.Count > 0)
        {
            foreach (var trade in openTrades)
            {
                report.AppendLine($"   ⏳ {trade.Symbol}: Entry ${trade.EntryPrice:F2} | {trade.Strategy}");
            }
        }
        else
        {
            report.AppendLine("   Nessuna operazione aperta");
        }

        report.AppendLine("\n" + new string('═', 70) + "\n");

        var reportText = report.ToString();
        Console.WriteLine(reportText);

        SaveReportToFile(reportText, weekStartDate);
    }

    private void SaveReportToFile(string report, DateTime weekStart)
    {
        try
        {
            var reportsDirectory = Path.Combine(_dataDirectory, "Reports");
            Directory.CreateDirectory(reportsDirectory);

            var fileName = $"Weekly_Report_{weekStart:yyyy-MM-dd}.txt";
            var filePath = Path.Combine(reportsDirectory, fileName);

            File.WriteAllText(filePath, report);
            Console.WriteLine($"Report salvato: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nel salvataggio del report: {ex.Message}");
        }
    }

    private void LoadTrades()
    {
        try
        {
            if (File.Exists(_tradesFile))
            {
                var json = File.ReadAllText(_tradesFile);
                _trades = JsonConvert.DeserializeObject<List<Trade>>(json) ?? new List<Trade>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nel caricamento delle operazioni: {ex.Message}");
            _trades = new List<Trade>();
        }
    }

    private void SaveTrades()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_trades, Formatting.Indented);
            File.WriteAllText(_tradesFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nel salvataggio delle operazioni: {ex.Message}");
        }
    }

    public List<Trade> GetAllTrades() => _trades;
}
