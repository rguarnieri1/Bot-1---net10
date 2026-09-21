using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using BotCripto.Models;

namespace BotCripto.Services;

public class NotificationService
{
    private readonly string _logDirectory;
    private readonly bool _emailAlertsEnabled;
    private readonly string _smtpServer;
    private readonly int _smtpPort;
    private readonly string _emailFrom;
    private readonly List<string> _emailTo;
    private readonly string _emailPassword;
    private readonly bool _emailOnSignal;
    private readonly bool _emailOnTrade;
    private readonly bool _emailOnError;
    private readonly Dictionary<string, JsonElement>? _notificationSettings;

    public NotificationService()
    {
        _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        Directory.CreateDirectory(_logDirectory);

        _notificationSettings = LoadNotificationSettingsFromAppsettings();

        // Load email settings from environment variables first, falling back to appsettings.json
        _emailAlertsEnabled = GetConfigBool("ENABLE_EMAIL_ALERTS", "EnableEmailAlerts", false);
        _smtpServer = GetConfigString("SMTP_SERVER", "EmailSmtpServer", "smtp.gmail.com");
        _smtpPort = GetConfigInt("SMTP_PORT", "EmailSmtpPort", 587);
        _emailFrom = GetConfigString("EMAIL_FROM", "EmailFrom", "");
        _emailPassword = GetConfigString("EMAIL_APP_PASSWORD", "EmailAppPassword", "");
        _emailOnSignal = GetConfigBool("EMAIL_ON_SIGNAL", "EmailAlertOnSignal", true);
        _emailOnTrade = GetConfigBool("EMAIL_ON_TRADE", "EmailAlertOnTrade", true);
        _emailOnError = GetConfigBool("EMAIL_ON_ERROR", "EmailAlertOnError", true);

        var emailToStr = GetConfigString("EMAIL_TO", "", "");
        if (!string.IsNullOrEmpty(emailToStr))
        {
            _emailTo = emailToStr.Split(';').Select(e => e.Trim()).ToList();
        }
        else if (_notificationSettings != null &&
                 _notificationSettings.TryGetValue("EmailToAddresses", out var toArray) &&
                 toArray.ValueKind == JsonValueKind.Array)
        {
            _emailTo = toArray.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        else
        {
            _emailTo = new List<string> { _emailFrom };
        }
    }

    private static Dictionary<string, JsonElement>? LoadNotificationSettingsFromAppsettings()
    {
        var merged = new Dictionary<string, JsonElement>();

        void MergeFrom(string directory)
        {
            foreach (var fileName in new[] { "appsettings.json", "appsettings.local.json" })
            {
                var path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("Notifications", out var notifications) &&
                        notifications.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in notifications.EnumerateObject())
                        {
                            merged[prop.Name] = prop.Value.Clone();
                        }
                    }
                }
                catch { }
            }
        }

        MergeFrom(AppDomain.CurrentDomain.BaseDirectory);
        if (merged.Count == 0)
        {
            MergeFrom(Directory.GetCurrentDirectory());
        }

        return merged.Count > 0 ? merged : null;
    }

    public async Task SendNotificationAsync(AnalysisResult result)
    {
        var message = FormatNotification(result);

        LogToFile(message);
        ConsoleNotification(result);

        try
        {
            await SendWindowsNotificationAsync(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nella notifica Windows: {ex.Message}");
        }

        if (_emailAlertsEnabled && _emailOnSignal)
        {
            try
            {
                await SendEmailNotificationAsync(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Errore invio email: {ex.Message}");
            }
        }
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

    private async Task SendWindowsNotificationAsync(AnalysisResult result)
    {
        try
        {
            var title = result.Signal.Contains("BUY") ? "🚀 SEGNALE DI ACQUISTO" : "⛔ SEGNALE DI VENDITA";
            var message = $"{result.Symbol} - {result.Signal}\nPrezzo: ${result.CurrentPrice:F2}";

            var psScript = $@"
Add-Type -AssemblyName Windows.UI

$app = 'BotCripto'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications.ToastNotificationManager]> $null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications.ToastNotification] > $null

$APP_ID = 'BotCripto'

$template = @""
<toast>
    <visual>
        <binding template='ToastText02'>
            <text id='1'>{title}</text>
            <text id='2'>{message}</text>
        </binding>
    </visual>
</toast>
""@

[xml]$xml = $template
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($APP_ID).Show($toast);
";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{psScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                await Task.Run(() => process?.WaitForExit());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nella notifica Windows: {ex.Message}");
        }
    }

    public async Task SendClosedTradeNotificationAsync(string symbol, decimal entryPrice, decimal exitPrice, decimal profit, decimal profitPercentage)
    {
        var profitEmoji = profit >= 0 ? "✅" : "❌";
        var title = profit >= 0 ? "🎉 PROFITTO!" : "📉 PERDITA";
        var message = $"{profitEmoji} {symbol}\nEntrata: ${entryPrice:F2} → Uscita: ${exitPrice:F2}\nP&L: {profitPercentage:F2}%";

        try
        {
            await SendWindowsDesktopNotificationAsync(title, message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nella notifica di chiusura: {ex.Message}");
        }
    }

    private async Task SendWindowsDesktopNotificationAsync(string title, string message)
    {
        try
        {
            var psScript = $@"
Add-Type -AssemblyName Windows.UI

$APP_ID = 'BotCripto'

$template = @""
<toast>
    <visual>
        <binding template='ToastText02'>
            <text id='1'>{title}</text>
            <text id='2'>{message}</text>
        </binding>
    </visual>
</toast>
""@

[xml]$xml = $template
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($APP_ID).Show($toast);
";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{psScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                await Task.Run(() => process?.WaitForExit());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore nella notifica desktop: {ex.Message}");
        }
    }

    private string FormatNotification(AnalysisResult result)
    {
        return $"[{result.StrategyName}] {result.Symbol}: {result.Signal} @ ${result.CurrentPrice:F8}";
    }

    private async Task SendEmailNotificationAsync(AnalysisResult result)
    {
        if (string.IsNullOrEmpty(_emailFrom) || string.IsNullOrEmpty(_emailPassword) || _emailTo.Count == 0)
        {
            Console.WriteLine("⚠️  Email alerts disabilitati: credenziali mancanti");
            return;
        }

        try
        {
            var subject = $"🤖 BotCripto: {result.Signal} - {result.Symbol}";
            var body = FormatEmailBody(result);

            using (var client = new SmtpClient(_smtpServer, _smtpPort))
            {
                client.EnableSsl = true;
                client.Credentials = new NetworkCredential(_emailFrom, _emailPassword);

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(_emailFrom),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };

                foreach (var email in _emailTo)
                {
                    mailMessage.To.Add(new MailAddress(email));
                }

                await Task.Run(() => client.Send(mailMessage));
                mailMessage.Dispose();

                Console.WriteLine($"✉️  Email inviata a {string.Join(", ", _emailTo)}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Errore invio email: {ex.Message}", ex);
        }
    }

    private string FormatEmailBody(AnalysisResult result)
    {
        var signalEmoji = result.Signal.Contains("BUY") ? "🟢" : "🔴";
        var html = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; }}
        .container {{ background-color: #f5f5f5; padding: 20px; border-radius: 8px; }}
        .header {{ color: #333; font-size: 24px; font-weight: bold; margin-bottom: 20px; }}
        .signal {{ font-size: 20px; color: {(result.Signal.Contains("BUY") ? "#00aa00" : "#cc0000")}; font-weight: bold; margin-bottom: 10px; }}
        .info {{ background-color: white; padding: 15px; border-radius: 5px; margin-bottom: 10px; }}
        .label {{ color: #666; font-weight: bold; }}
        .value {{ color: #333; }}
        .indicators {{ background-color: white; padding: 15px; border-radius: 5px; }}
        .indicator-row {{ display: flex; justify-content: space-between; padding: 5px 0; border-bottom: 1px solid #eee; }}
        .footer {{ color: #999; font-size: 12px; margin-top: 20px; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>{signalEmoji} SEGNALE DI TRADING</div>

        <div class='signal'>{result.Signal}</div>

        <div class='info'>
            <div class='label'>Criptovaluta:</div>
            <div class='value'>{result.Symbol}</div>
        </div>

        <div class='info'>
            <div class='label'>Prezzo:</div>
            <div class='value'>${result.CurrentPrice:F8}</div>
        </div>

        <div class='info'>
            <div class='label'>Strategia:</div>
            <div class='value'>{result.StrategyName}</div>
        </div>

        <div class='info'>
            <div class='label'>Orario Analisi:</div>
            <div class='value'>{result.AnalysisTime:yyyy-MM-dd HH:mm:ss}</div>
        </div>

        <div class='indicators'>
            <div class='label'>Indicatori Tecnici:</div>";

        foreach (var indicator in result.Indicators)
        {
            html += $@"
            <div class='indicator-row'>
                <span>{indicator.Key}:</span>
                <span><strong>{indicator.Value:F4}</strong></span>
            </div>";
        }

        html += @"
        </div>

        <div class='footer'>
            <p>🤖 Segnale automatico da BotCripto</p>
            <p>Non rispondere a questa email</p>
        </div>
    </div>
</body>
</html>";

        return html;
    }

    public async Task SendEmailTradeNotificationAsync(string symbol, string action, decimal price, decimal profitLoss, decimal profitLossPercent)
    {
        if (!_emailAlertsEnabled || !_emailOnTrade || string.IsNullOrEmpty(_emailFrom))
            return;

        try
        {
            var subject = $"🤖 BotCripto: Trade {action} - {symbol}";
            var emoji = profitLoss >= 0 ? "✅" : "❌";
            var color = profitLoss >= 0 ? "#00aa00" : "#cc0000";

            var html = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; }}
        .container {{ background-color: #f5f5f5; padding: 20px; border-radius: 8px; }}
        .header {{ color: #333; font-size: 24px; font-weight: bold; margin-bottom: 20px; }}
        .info {{ background-color: white; padding: 15px; border-radius: 5px; margin-bottom: 10px; }}
        .pnl {{ font-size: 20px; color: {color}; font-weight: bold; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>{emoji} Trade Chiuso: {symbol}</div>
        <div class='info'>
            <p><strong>Azione:</strong> {action}</p>
            <p><strong>Prezzo:</strong> ${price:F8}</p>
            <p class='pnl'>P&L: {profitLossPercent:F2}% ({profitLoss:F8})</p>
        </div>
    </div>
</body>
</html>";

            using (var client = new SmtpClient(_smtpServer, _smtpPort))
            {
                client.EnableSsl = true;
                client.Credentials = new NetworkCredential(_emailFrom, _emailPassword);

                var mailMessage = new MailMessage(_emailFrom, string.Join(";", _emailTo))
                {
                    Subject = subject,
                    Body = html,
                    IsBodyHtml = true
                };

                await Task.Run(() => client.Send(mailMessage));
                mailMessage.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Errore invio email trade: {ex.Message}");
        }
    }

    private string GetConfigString(string envKey, string jsonKey, string defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(envValue))
        {
            return envValue;
        }

        if (!string.IsNullOrEmpty(jsonKey) &&
            _notificationSettings != null &&
            _notificationSettings.TryGetValue(jsonKey, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? defaultValue;
        }

        return defaultValue;
    }

    private bool GetConfigBool(string envKey, string jsonKey, bool defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(envValue))
        {
            return bool.Parse(envValue);
        }

        if (_notificationSettings != null &&
            _notificationSettings.TryGetValue(jsonKey, out var prop) &&
            (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
        {
            return prop.GetBoolean();
        }

        return defaultValue;
    }

    private int GetConfigInt(string envKey, string jsonKey, int defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(envValue))
        {
            return int.Parse(envValue);
        }

        if (_notificationSettings != null &&
            _notificationSettings.TryGetValue(jsonKey, out var prop) &&
            prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }

        return defaultValue;
    }
}
