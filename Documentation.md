# Documentazione - Bot 1 (BotCripto v1.1)

Documentazione tecnica basata sul codice sorgente attuale del progetto `1 - Bot Cripto` (namespace `BotCripto`, target `.NET 10.0`).

## 1. Panoramica

Bot 1 è un'applicazione console .NET 10 per il trading automatico di criptovalute. Analizza periodicamente un ampio paniere di crypto (filtrato per volume 24h ≥ $2.000.000 tramite CoinGecko), applica una strategia trend-following basata su EMA Ribbon, valida ogni segnale con un modulo di risk management e notifica l'utente (console, desktop toast, email). Traccia inoltre i motivi di scarto di ogni ciclo (filtro strategia o RiskManager) per diagnosticare perché non vengono generati segnali. Include anche due modalità di backtest (una "reale" basata su candele storiche e una sintetica basata su distribuzioni statistiche).

Punto di ingresso: `Program.cs`.

## 2. Modalità di esecuzione

L'app legge gli argomenti da riga di comando:

- **Modalità Live (default)**: `dotnet run` → esegue `RunLiveAsync()`, che istanzia `BotSchedulerService` con capitale iniziale €150 e avvia il ciclo di monitoraggio continuo. Il banner iniziale stampa "Risk per Trade: 1% (€1.50)", ma il valore realmente passato al `RiskManager` è 2% (vedi §6 e §14): il testo del banner è disallineato dal comportamento effettivo.
- **Modalità Backtest sintetico**: `dotnet run -- --backtest` → esegue `RunBacktestAsync()`, che istanzia `SyntheticBacktest` (capitale €100) e genera un report su 365 giorni, 20 simboli, win-rate atteso 55%, 8 trade per simbolo.

## 3. Struttura del progetto

```
Program.cs                                  Entry point (Live / Backtest)
Models/
  Cryptocurrency.cs                         Modelli dati: Cryptocurrency, Candle, AnalysisResult, Trade
Strategies/
  EmaRibbonTrendFollowingStrategy.cs         Strategia principale (EMA Ribbon + candela + volume + RSI)
  TechnicalIndicators.cs                     Libreria indicatori: EMA, SMA, RSI, MACD, Bollinger Bands
Services/
  BotSchedulerService.cs                     Orchestratore del ciclo live (timer, scansione, notifiche)
  CryptoDataService.cs                       Recupero dati mercato (Crypto.com → Bybit → dati demo)
  RiskManager.cs                             Position sizing, calcolo P&L/tasse, metriche di performance
  NotificationService.cs                     Notifiche console, log file, toast Windows, email
  ReportingService.cs                        Persistenza trade (Data/trades.json), report settimanale
  BacktestService.cs                         Backtest su dati storici reali (parzialmente implementato)
  SyntheticBacktest.cs                       Backtest sintetico basato su distribuzione statistica
config.json                                  Configurazione "di progetto" (parametri strategia, risk, reporting)
appsettings.json / appsettings.local.json    Configurazione runtime (.NET), incl. credenziali email
```

> Nota: `config.json` e `appsettings.json` sono due file di configurazione paralleli con schema diverso. Solo `appsettings.json`/`appsettings.local.json` vengono effettivamente letti dal codice (sezione `Notifications` in `NotificationService`); `config.json` non risulta referenziato da nessuna classe — sembra un file descrittivo/di riferimento non ancora cablato al codice.

## 4. Modelli dati (`Models/Cryptocurrency.cs`)

- **`Cryptocurrency`**: `Symbol`, `Name`, `CurrentPrice`, `LastUpdate`.
- **`Candle`**: `Time`, `Open`, `High`, `Low`, `Close`, `Volume`.
- **`AnalysisResult`**: esito dell'analisi di una strategia su un simbolo — `Symbol`, `StrategyName`, `IsSignal`, `Signal` (testo descrittivo, incluso il motivo di rigetto), `CurrentPrice`, `AnalysisTime`, `Indicators` (dizionario nome→valore usato per portare EMA, RSI, stop loss, target, ecc.).
- **`Trade`**: rappresenta un'operazione — `Id` (stringa, di default `Guid.NewGuid().ToString()` ma sovrascrivibile, es. `SyntheticBacktest` usa id testuali tipo `"BTC_0"`), `Symbol`, `OpenTime`, `CloseTime`, `EntryPrice`, `ExitPrice`, `Strategy`, `Status` (`Open`/`Closed`), `Profit`, `ProfitPercentage`.

## 5. Strategia di trading

### 5.1 EMA Ribbon Trend Following + Candle Confirmation (`Strategies/EmaRibbonTrendFollowingStrategy.cs`)

Unica strategia attiva nel bot live. Richiede almeno 60 candele. Pipeline di filtri sequenziali (ogni filtro fallito interrompe l'analisi con un `Signal` di rigetto specifico):

1. **EMA Ribbon** — calcola EMA a 5, 10, 20, 50 periodi (`TechnicalIndicators.CalculateEMA`).
2. **Trend filter (allineamento ribbon)** — richiede allineamento stretto crescente (uptrend: EMA5>EMA10>EMA20>EMA50) o decrescente (downtrend); altrimenti rigetta ("no clear trend").
3. **Volume filter** — il volume dell'ultima candela deve essere ≥ 80% della media mobile a 20 periodi; altrimenti rigetta.
4. **Candle confirmation** — l'ultima candela deve avere un corpo "solido" (`bodyStrength ≥ 0.6`) nella direzione del trend, e il prezzo deve essere oltre EMA10 nella direzione corretta.
5. **RSI filter** — calcolato su 14 periodi; rigetta segnali long con RSI > 85 (ipercomprato estremo) o segnali short con RSI < 15 (ipervenduto estremo).
6. **Breakout confirmation** — richiede che il prezzo abbia appena rotto EMA5 o EMA10 rispetto alla candela precedente, nella direzione del trend.

Se tutti i filtri passano, genera un segnale `BUY` o `SELL` con:
- `StopLoss` = min/max tra EMA20 e low/high dell'ultima candela, con margine ±2%.
- `Target` = calcolato con rapporto rischio:rendimento 2:1 rispetto allo stop loss.
- `SignalStrength` = combinazione di forza del corpo candela e rapporto volume.

Parametri chiave (hardcoded nella classe, non letti da `config.json`): EMA periods `[5,10,20,50]`, volume threshold `1.2`, body strength `0.6`, RSI overbought/oversold `70/30` (dichiarati ma i controlli effettivi usano `85/15`).

### 5.2 Indicatori tecnici (`Strategies/TechnicalIndicators.cs`)

Libreria statica condivisa:
- `CalculateEMA(prices, period)` — media mobile esponenziale, seed con SMA del primo periodo.
- `CalculateSMA(prices, period)` — media mobile semplice.
- `CalculateRSI(prices, period=14)` — RSI con smoothing di Wilder.
- `CalculateMACD(prices, fast=12, slow=26, signal=9)` — MACD, signal line, istogramma (definito ma non usato dalla strategia attiva).
- `CalculateBollingerBands(prices, period=20, stdDevMultiplier=2)` — bande di Bollinger (definito ma non usato dalla strategia attiva).

## 6. Ciclo live (`Services/BotSchedulerService.cs`)

`BotSchedulerService` orchestra il funzionamento continuo del bot:

- Costruttore: inizializza `CryptoDataService`, `NotificationService`, `ReportingService`, `EmaRibbonTrendFollowingStrategy` e `RiskManager` (rischio 2% per trade, R:R 2:1, max posizione 10%, leva max 2.0x, commissioni 0.6%, tasse 26%). Nota: questi valori sono hardcoded nel costruttore e differiscono in parte da quelli in `config.json` (che indica 1% di rischio).
- `StartAsync()`:
  - Esegue subito un primo ciclo di controllo mercato.
  - Imposta un `Timer` che richiama `CheckMarketAsync()` ogni 60 minuti.
  - Imposta un secondo `Timer` per generare il report settimanale ogni lunedì alle 00:00 UTC.
  - Rimane in attesa indefinita (`Task.Delay(-1)`).
- `CheckMarketAsync()` (ciclo principale):
  1. Recupera fino a 500 criptovalute tramite `CryptoDataService.GetLargeCapCryptocurrenciesAsync()`.
  2. Per ciascuna crypto, scarica 100 candele a 4h (`GetCandlesAsync`); salta (e conta come scarto "Dati insufficienti") se meno di 50 candele disponibili.
  3. Calcola la volatilità (deviazione standard dei rendimenti sulle ultime 20 candele, clampata tra 1% e 8%).
  4. Esegue `EmaRibbonTrendFollowingStrategy.Analyze()`; se non genera segnale, classifica il motivo di rigetto tramite `ClassifyStrategyRejection()` (vedi sotto).
  5. Se viene generato un segnale, chiama `RiskManager.CalculatePosition()` per validare dimensione posizione, leva ed expected profit rispetto al capitale corrente; se la posizione non è valida, classifica il motivo tramite `ClassifyRiskRejection()`.
  6. Se la posizione è valida: invia notifica (`NotificationService`), registra il trade (`ReportingService.RecordTrade`), aggiorna contatori.
  7. Applica un piccolo delay (50ms) tra una crypto e l'altra per non sovraccaricare le API.
  8. A fine ciclo stampa un riepilogo (segnali trovati/filtrati, trade aperti, valore account stimato) seguito dai **motivi di scarto del ciclo corrente** (conteggio per categoria, in ordine decrescente).
  9. Ogni 10 cicli, calcola e stampa le metriche di performance cumulative (win rate, profit factor, P&L totale, expectancy) tramite `RiskManager.CalculatePerformanceMetrics`, seguite dai **motivi di scarto cumulativi** (dall'avvio del bot).

**Diagnostica dei motivi di scarto** — per capire quale filtro blocca i segnali, ogni candidato scartato viene classificato in una categoria leggibile e conteggiato sia per il ciclo corrente (`rejectionCounts`, azzerato a ogni ciclo) sia cumulativamente (`_cumulativeRejectionCounts`, per l'intera durata del processo):
- `ClassifyStrategyRejection(signal)` — mappa il testo del `Signal` di rigetto della strategia (§5.1) in categorie: trend non allineato, volume insufficiente, candela non abbastanza forte, prezzo non conferma vs EMA10, RSI estremo, nessun breakout EMA, dati insufficienti; qualsiasi altro testo finisce in "Strategia: altro (...)".
- `ClassifyRiskRejection(reason)` — mappa il `Reason` di rigetto del `RiskManager` (§8) in categorie: leva troppo alta, profitto non copre commissioni, rapporto rischio/rendimento insufficiente, errore calcolo stop loss; altrimenti "Risk Manager: altro (...)".

> Nota: `_currentAccountValue` non viene mai aggiornato dopo l'inizializzazione (nessuna logica di chiusura trade nel loop live), quindi il position sizing usa sempre il capitale iniziale.

## 7. Recupero dati di mercato (`Services/CryptoDataService.cs`)

`GetLargeCapCryptocurrenciesAsync()` combina due catene di fallback: una per il ticker/prezzo e una per le capitalizzazioni, che vengono poi incrociate.

**Ticker** — catena di fallback a tre livelli:
1. **Crypto.com API** (`https://api.crypto.com/v2/public/get-ticker`, `get-candlestick`) — sorgente primaria.
2. **Bybit API** (`https://api.bybit.com/v5/market/tickers`, `market/kline`, categoria `spot`) — fallback se Crypto.com fallisce o non risponde.
3. **Dati demo hardcoded** — se entrambe le API falliscono: lista statica di ~50 crypto (`GetDemoData`) per i ticker, e candele generate casualmente con random walk (`GenerateDemoCandles`) per le serie storiche. Nota: la modalità demo non passa dal filtro di volume (viene ritornata direttamente).

**Filtro volume via CoinGecko** (`GetVolumesAsync` + `FilterByVolume`):
- Prima di interrogare Crypto.com/Bybit, il servizio scarica il volume 24h da `GET https://api.coingecko.com/api/v3/coins/markets` (2 pagine da 250 risultati, ordinate per market cap decrescente → fino a 500 simboli), costruendo un dizionario simbolo→volume 24h (case-insensitive, primo valore vince in caso di duplicati).
- I ticker ottenuti da Crypto.com o Bybit vengono poi filtrati tenendo solo i simboli presenti nel dizionario con `Volume24h >= $2.000.000` (costante `MinVolume24hUsd`, non letta da `config.json`); il filtro esclude le crypto che scambiano un volume troppo esiguo per essere affidabili (spread larghi, prezzi stantii). Non c'è più alcun filtro di capitalizzazione: è stato rimosso perché ridondante rispetto al volume come indicatore di liquidità/affidabilità.
- Se CoinGecko non è raggiungibile o non risponde entro i tentativi previsti, il dizionario risulta vuoto e il filtro viene **saltato per quel ciclo** (vengono restituiti tutti i ticker non filtrati), con un warning in console.

Caratteristiche generali:
- Rate limiting semplice tra chiamate API (100ms minimo, `RateLimitDelay`), applicato anche alle chiamate CoinGecko.
- Timeframe supportati per le candele: `1m, 5m, 15m, 30m, 1h, 4h, 1d` (mappati ai formati specifici di ciascun exchange).
- Le candele Bybit vengono ordinate e limitate; le candele minime richieste per l'analisi sono 50.
- I ticker Crypto.com/Bybit vengono ordinati per prezzo decrescente e limitati (in codice) a 500 **prima** del filtro di volume; per Bybit la richiesta HTTP specifica comunque `limit=200`, quindi il numero effettivo restituito non supera 200 anche a filtro disattivato.

## 8. Gestione del rischio (`Services/RiskManager.cs`)

Classe centrale per money management, indipendente dalla strategia:

- **`CalculatePosition(symbol, entryPrice, volatilityPercent, openTrades, currentAccountValue)`**:
  - Stop loss fisso all'1.5% dal prezzo di entrata, target fisso al 3% (rapporto 2:1).
  - Position size = rischio per trade (in valuta) / rischio per unità, cappato al max % di posizione configurato.
  - Verifica leva totale (esposizione aperta + nuova posizione / valore account) contro `maxLeverage`; rigetta se superata.
  - Verifica che il profitto atteso netto (dopo commissioni doppie entry+exit) sia positivo.
  - Verifica che il rapporto rischio/rendimento effettivo sia ≥ 1.2; altrimenti rigetta.
  - Ritorna `PositionSizingResult` con size, stop/target, rischio, profitto atteso, leva, commissioni e motivazione.
- **`CalculateProfitAndTaxes(entryPrice, exitPrice, positionSize, isWinningTrade)`**: calcola P&L lordo, commissioni (entry+exit), tasse (26% solo sui profitti netti positivi), profitto finale netto.
- **`CalculatePerformanceMetrics(trades, initialCapital)`**: calcola su tutti i trade chiusi — win rate, profit factor, ROI, expectancy, medie win/loss, massime serie consecutive di vittorie/sconfitte, commissioni totali.

## 9. Notifiche (`Services/NotificationService.cs`)

Canali di notifica per ogni segnale generato:

1. **Log su file** — `Logs/signals_yyyy-MM-dd.log`.
2. **Console** — output colorato (verde per BUY, rosso per SELL) con tutti gli indicatori, più un beep.
3. **Toast Windows** — tramite script PowerShell (`Windows.UI.Notifications`) lanciato come processo esterno.
4. **Email (opzionale)** — via SMTP (default Gmail, porta 587, SSL); corpo HTML formattato con dettagli del segnale.

Configurazione email caricata con priorità: variabili d'ambiente (`ENABLE_EMAIL_ALERTS`, `SMTP_SERVER`, `SMTP_PORT`, `EMAIL_FROM`, `EMAIL_APP_PASSWORD`, `EMAIL_TO`, `EMAIL_ON_SIGNAL`, `EMAIL_ON_TRADE`, `EMAIL_ON_ERROR`) → sezione `Notifications` di `appsettings.json`/`appsettings.local.json` → valori di default. Se `EmailFrom`/`EmailAppPassword` mancano, l'invio email viene saltato con warning.

Esiste anche `SendClosedTradeNotificationAsync` e `SendEmailTradeNotificationAsync` per notificare la chiusura di un trade (usati da `ReportingService.CloseTradeAsync`, non ancora richiamato dal ciclo live automatico).

## 10. Persistenza e reportistica (`Services/ReportingService.cs`)

- Mantiene la lista dei trade in memoria, sincronizzata su file `Data/trades.json` (serializzazione con Newtonsoft.Json).
- `RecordTrade(symbol, entryPrice, strategy)` — crea un nuovo `Trade` con stato `Open`.
- `CloseTradeAsync(symbol, exitPrice)` — chiude il primo trade aperto per il simbolo, calcola profitto/percentuale, scrive log dettagliato in `Logs/Operazioni_chiuse.log`, invia notifica di chiusura.
- `GenerateWeeklyReport()` — produce un report testuale (statistiche generali, performance, elenco trade aperti/chiusi della settimana corrente) stampato in console e salvato in `Data/Reports/Weekly_Report_yyyy-MM-dd.txt`.
- `GetAllTrades()` — espone la lista completa dei trade per il calcolo metriche nello scheduler.

## 11. Backtest

### 11.1 `Services/SyntheticBacktest.cs` (usato da `Program.cs` in modalità `--backtest`)

Genera trade **simulati statisticamente** (non basati su dati di mercato reali):
- Per ogni combinazione simbolo/trade, genera un prezzo di entrata casuale, decide se vincente in base al `expectedWinRate` fornito, quindi calcola un'uscita con profitto (1-6%) o perdita (0.5-3%) casuale entro range coerenti con SL/TP configurati.
- Distribuisce le date di apertura/chiusura casualmente entro il periodo richiesto (default 365 giorni, seed `Random(42)` per riproducibilità).
- Calcola le metriche con `RiskManager.CalculatePerformanceMetrics` e stampa un report esteso (ROI annualizzato, top 5 win/loss, validazione rispetto a soglie 50%/55% di win-rate), salvato in `Data/Reports/Backtest_Synthetic_*.txt`.
- Istanzia un proprio `RiskManager` con parametri **diversi da quelli usati in live** (§6): rischio 1% per trade, leva max 1.5x, commissioni 0.8% (contro 2% / 2.0x / 0.6% del `BotSchedulerService`). Il report stampa inoltre l'etichetta "Commissioni (0.1%)", disallineata dal valore realmente configurato (0.8%).

### 11.2 `Services/BacktestService.cs` (non collegato a `Program.cs`, invocabile solo programmaticamente)

Pensato per un backtest su **dati storici reali** scaricati da `CryptoDataService`:
- Scarica fino a 300 candele per simbolo e filtra quelle nel range di date richiesto.
- **Limite noto**: il ciclo che dovrebbe eseguire l'analisi strategia-per-candela (`for i in candlesInRange`) attualmente non chiama la strategia né genera trade reali — è un placeholder che itera senza produrre segnali.
- `SimulateTradeClosures` chiude eventuali trade aperti con un esito casuale (55% win) per permettere comunque il calcolo delle metriche.
- Genera un report dettagliato simile a quello sintetico, salvato in `Data/Reports/Backtest_Report_*.txt`.
- Istanzia un proprio `RiskManager` con gli stessi parametri di `SyntheticBacktest` (rischio 1%, leva max 1.5x, commissioni 0.8%), anch'essi diversi da quelli live.

## 12. Configurazione

### 12.1 `appsettings.json` / `appsettings.local.json`

Effettivamente letti dal codice (sezione `Notifications` da `NotificationService`). Sezioni presenti nel file: `CryptoExchange`, `TradingSettings`, `MarketMonitoring`, `Notifications`, `Logging` — ma solo `Notifications` risulta consumata a runtime; le altre sezioni sono documentative/predisposte per uso futuro.

### 12.2 `config.json`

File di configurazione "di progetto" con schema più ampio (strategie, risk management, reporting, storage, API, ottimizzazioni) — **non referenziato da alcuna classe C#** nel codice attuale. Da considerare come specifica/riferimento per future estensioni, non come sorgente di configurazione runtime. Anche la soglia minima di volume 24h usata dal filtro CoinGecko (§7) è hardcoded in `CryptoDataService` (`MinVolume24hUsd = $2.000.000`) e non proviene da questo file.

## 13. Dipendenze principali (`1 - Bot Cripto.csproj`)

- Target framework: `.NET 10.0`, `Nullable` e `ImplicitUsings` abilitati.
- `Newtonsoft.Json` 13.0.4 — serializzazione JSON (trade, risposte API).
- Nessun pacchetto di compatibilità aggiuntivo: `System.Net.Http` e `System.Runtime.InteropServices` (necessari sotto .NET 8) sono stati rimossi nel porting a .NET 10 perché superati dal runtime moderno.

## 14. Limitazioni note / debito tecnico osservato nel codice

- `config.json` non è cablato al codice: i parametri realmente attivi sono quelli hardcoded nelle classi (`EmaRibbonTrendFollowingStrategy`, `BotSchedulerService`, `RiskManager`).
- `BotSchedulerService` non aggiorna mai `_currentAccountValue` né chiude trade automaticamente: il position sizing nel ciclo live usa sempre il capitale iniziale e i trade restano sempre `Open` finché non viene chiamato manualmente `ReportingService.CloseTradeAsync`.
- `BacktestService.RunBacktestAsync` non esegue realmente l'analisi della strategia sui dati storici (ciclo placeholder); solo `SyntheticBacktest` produce risultati end-to-end, ma basati su dati simulati anziché su prezzi reali.
- I livelli RSI di ipercomprato/ipervenduto dichiarati come campo (`70/30`) non corrispondono alle soglie realmente applicate nei controlli di rigetto (`85/15`).
- Il banner di avvio in `Program.cs` (`RunLiveAsync`) stampa "Risk per Trade: 1% (€1.50)", ma `BotSchedulerService` passa realmente `riskPercentPerTrade: 0.02m` (2%) al `RiskManager`: il testo mostrato all'utente non riflette il rischio effettivamente applicato.
- Il filtro di volume 24h (§7) dipende da CoinGecko, un terzo provider aggiuntivo rispetto a Crypto.com/Bybit già usati per prezzi e candele: se CoinGecko è irraggiungibile o applica rate limiting, il filtro viene silenziosamente disattivato per il ciclo (nessun retry, nessun backoff), quindi in quel ciclo possono passare anche crypto poco scambiate.
- I parametri di `RiskManager` usati dai due backtest (§11.1, §11.2) — rischio 1%, leva max 1.5x, commissioni 0.8% — non coincidono con quelli usati realmente in live da `BotSchedulerService` (2%, 2.0x, 0.6%): i risultati dei backtest non sono quindi direttamente comparabili con il comportamento live attuale. `SyntheticBacktest` mostra inoltre un'etichetta "Commissioni (0.1%)" nel report che non riflette il valore realmente configurato (0.8%).
