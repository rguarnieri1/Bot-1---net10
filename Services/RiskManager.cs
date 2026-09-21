using BotCripto.Models;

namespace BotCripto.Services;

public class RiskManager
{
    private const int MaxConcurrentTrades = 100;

    private readonly decimal _accountSize;
    private readonly decimal _riskPerTrade;
    private readonly decimal _minRewardRatio;
    private readonly decimal _maxPositionSizePercent;
    private readonly decimal _commissionsPercent;
    private readonly decimal _taxRate;

    public RiskManager(
        decimal accountSize,
        decimal riskPercentPerTrade = 0.01m,
        decimal rewardRiskRatio = 2.0m,
        decimal maxPositionSizePercent = 0.10m,
        decimal commissionsPercent = 0.10m,
        decimal taxRate = 0.26m)
    {
        _accountSize = accountSize;
        _riskPerTrade = accountSize * riskPercentPerTrade;
        _minRewardRatio = rewardRiskRatio;
        _maxPositionSizePercent = maxPositionSizePercent;
        _commissionsPercent = commissionsPercent;
        _taxRate = taxRate;
    }

    public PositionSizingResult CalculatePosition(
        string symbol,
        decimal entryPrice,
        decimal volatilityPercent,
        List<Trade> openTrades,
        decimal currentAccountValue)
    {
        var result = new PositionSizingResult
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            IsValid = false,
            Reason = ""
        };

        // 1️⃣ Calcola stop loss a 1.5%
        var stopLossPercent = 0.015m;
        var stopLossPrice = entryPrice * (1 - stopLossPercent);
        var riskPerUnit = entryPrice - stopLossPrice;

        if (riskPerUnit <= 0)
        {
            result.Reason = "Stop loss calculation error";
            return result;
        }

        // 2️⃣ Calcola position size (in unità dell'asset) basato su rischio fisso
        var positionSize = _riskPerTrade / riskPerUnit;

        // 3️⃣ Valida contro massimale per trade: confronto in € sul valore nozionale
        // (positionSize è in unità di asset, non in euro - va convertito prima di confrontarlo
        // con maxPositionValue, altrimenti il cap non scatta quasi mai).
        var maxPositionValue = currentAccountValue * _maxPositionSizePercent;
        var positionValue = positionSize * entryPrice;
        if (positionValue > maxPositionValue)
        {
            positionSize = maxPositionValue / entryPrice;
        }

        // 4️⃣ Calcola target a 3%
        var targetPercent = 0.03m;
        var targetPrice = entryPrice * (1 + targetPercent);
        var profitPerUnit = targetPrice - entryPrice;

        // 5️⃣ Verifica numero massimo di trade simultanei
        if (openTrades.Count >= MaxConcurrentTrades)
        {
            result.Reason = $"Max concurrent trades reached: {openTrades.Count} >= {MaxConcurrentTrades}";
            return result;
        }

        // Esposizione totale (indicatore informativo, non blocca più il trade)
        var totalExposure = openTrades.Sum(t => t.EntryPrice * 1) + positionSize;
        var leverageRatio = totalExposure / currentAccountValue;

        // 6️⃣ Verifica che il profitto atteso copra commissioni
        var expectedProfit = positionSize * profitPerUnit;
        var commissionsOnTrade = (entryPrice * positionSize * _commissionsPercent / 100) * 2; // Entry + Exit
        var netProfit = expectedProfit - commissionsOnTrade;

        if (netProfit <= 0)
        {
            result.Reason = "Expected profit doesn't cover commissions";
            return result;
        }

        // 7️⃣ Calcola rischio-reward effettivo
        var totalRisk = positionSize * riskPerUnit;
        var riskRewardRatio = netProfit / totalRisk;

        if (riskRewardRatio < 1.2m)
        {
            result.Reason = $"Risk-reward too low: {riskRewardRatio:F2} < 1.2 required";
            return result;
        }

        // ✅ TRADE VALIDO
        result.IsValid = true;
        result.PositionSize = positionSize;
        result.StopLossPrice = stopLossPrice;
        result.StopLossPercent = stopLossPercent;
        result.TargetPrice = targetPrice;
        result.TargetPercent = profitPerUnit / entryPrice;
        result.RiskAmount = totalRisk;
        result.ExpectedProfit = netProfit;
        result.RiskRewardRatio = riskRewardRatio;
        result.LeverageRatio = leverageRatio;
        result.Commissions = commissionsOnTrade;
        result.Reason = $"✅ Valid | R:R {riskRewardRatio:F2} | Leverage {leverageRatio:F2}x";

        return result;
    }

    public ProfitCalculation CalculateProfitAndTaxes(
        decimal entryPrice,
        decimal exitPrice,
        decimal positionSize,
        bool isWinningTrade)
    {
        var result = new ProfitCalculation();

        // Profitto/Perdita lordo
        var priceChange = exitPrice - entryPrice;
        var profitLoss = priceChange * positionSize;

        // Commissioni (entry + exit)
        var entryCommission = (entryPrice * positionSize * _commissionsPercent / 100);
        var exitCommission = (exitPrice * positionSize * _commissionsPercent / 100);
        var totalCommissions = entryCommission + exitCommission;

        // Profitto netto prima tasse
        var netProfitBeforeTax = profitLoss - totalCommissions;

        // Tasse (solo sui guadagni)
        decimal taxes = 0;
        if (netProfitBeforeTax > 0)
        {
            taxes = netProfitBeforeTax * _taxRate;
        }

        var finalProfit = netProfitBeforeTax - taxes;

        result.GrossProfit = profitLoss;
        result.Commissions = totalCommissions;
        result.NetProfitBeforeTax = netProfitBeforeTax;
        result.Taxes = taxes;
        result.NetProfit = finalProfit;
        result.ProfitPercent = (finalProfit / (entryPrice * positionSize)) * 100;

        return result;
    }

    public PerformanceMetrics CalculatePerformanceMetrics(
        List<Trade> trades,
        decimal initialCapital)
    {
        var metrics = new PerformanceMetrics();

        if (trades.Count == 0)
            return metrics;

        var closedTrades = trades.Where(t => t.Status == "Closed").ToList();
        if (closedTrades.Count == 0)
            return metrics;

        // Win/Loss stats
        var winningTrades = closedTrades.Where(t => t.Profit > 0).ToList();
        var losingTrades = closedTrades.Where(t => t.Profit <= 0).ToList();

        metrics.TotalTrades = closedTrades.Count;
        metrics.WinningTrades = winningTrades.Count;
        metrics.LosingTrades = losingTrades.Count;
        metrics.WinRate = (decimal)winningTrades.Count / closedTrades.Count;

        // Profitto totale
        metrics.TotalProfit = closedTrades.Sum(t => t.Profit ?? 0);
        metrics.TotalCommissions = closedTrades.Sum(t =>
        {
            var commissions = (t.EntryPrice * 1 * _commissionsPercent / 100) * 2;
            return commissions;
        });

        // Average win/loss
        if (winningTrades.Count > 0)
            metrics.AverageWin = winningTrades.Average(t => t.Profit ?? 0);

        if (losingTrades.Count > 0)
            metrics.AverageLoss = losingTrades.Average(t => t.Profit ?? 0);

        // Profit factor
        var totalWins = winningTrades.Sum(t => t.Profit ?? 0);
        var totalLosses = Math.Abs(losingTrades.Sum(t => t.Profit ?? 0));

        if (totalLosses > 0)
            metrics.ProfitFactor = totalWins / totalLosses;
        else if (totalWins > 0)
            metrics.ProfitFactor = decimal.MaxValue;

        // ROI
        // TotalProfit è la somma di Trade.Profit: quando i trade sono chiusi via
        // CalculateProfitAndTaxes() è già al netto di commissioni e tasse, quindi qui
        // non va sottratto di nuovo TotalCommissions (che tra l'altro usa una formula
        // che non tiene conto della position size reale del trade).
        metrics.ROI = (metrics.TotalProfit / initialCapital) * 100;

        // Consecutive wins/losses
        int maxConsecutiveWins = 0;
        int maxConsecutiveLosses = 0;
        int currentWins = 0;
        int currentLosses = 0;

        foreach (var trade in closedTrades.OrderBy(t => t.CloseTime))
        {
            if (trade.Profit > 0)
            {
                currentWins++;
                currentLosses = 0;
                maxConsecutiveWins = Math.Max(maxConsecutiveWins, currentWins);
            }
            else
            {
                currentLosses++;
                currentWins = 0;
                maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, currentLosses);
            }
        }

        metrics.MaxConsecutiveWins = maxConsecutiveWins;
        metrics.MaxConsecutiveLosses = maxConsecutiveLosses;

        // Expectancy (average profit per trade)
        metrics.Expectancy = metrics.TotalProfit / closedTrades.Count;

        return metrics;
    }
}

// Data classes
public class PositionSizingResult
{
    public string Symbol { get; set; }
    public decimal EntryPrice { get; set; }
    public bool IsValid { get; set; }
    public string Reason { get; set; }
    public decimal PositionSize { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal StopLossPercent { get; set; }
    public decimal TargetPrice { get; set; }
    public decimal TargetPercent { get; set; }
    public decimal RiskAmount { get; set; }
    public decimal ExpectedProfit { get; set; }
    public decimal RiskRewardRatio { get; set; }
    public decimal LeverageRatio { get; set; }
    public decimal Commissions { get; set; }
}

public class ProfitCalculation
{
    public decimal GrossProfit { get; set; }
    public decimal Commissions { get; set; }
    public decimal NetProfitBeforeTax { get; set; }
    public decimal Taxes { get; set; }
    public decimal NetProfit { get; set; }
    public decimal ProfitPercent { get; set; }
}

public class PerformanceMetrics
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal ROI { get; set; }
    public int MaxConsecutiveWins { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public decimal Expectancy { get; set; }
}
