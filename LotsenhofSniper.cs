#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input; 
using NinjaTrader.Cbi;
using NinjaTrader.Gui; 
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools; 
using NinjaTrader.Core.FloatingPoint;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum SniperKey
    {
        Up, Down, Left, Right, 
        W, A, S, D, Q, E, R, F, X, C, V, 
        Space, Escape, Enter, 
        NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
        Ctrl_NumPad0, Ctrl_NumPad1, Ctrl_NumPad2, Ctrl_NumPad3, Ctrl_NumPad4, Ctrl_NumPad5, Ctrl_NumPad6, Ctrl_NumPad7, Ctrl_NumPad8, Ctrl_NumPad9,
        Ctrl_Up, Ctrl_Down, Ctrl_Right, Ctrl_Left
    }

    public enum AtmDirection { Long, Short }

    public class AtmState
    {
        public string AtmId;
        public string EntryOrderId;
        public AtmDirection Direction;
        public double EntryPrice;
        public bool BeTriggered;
        public double IntendedStopPrice; 
        public double TriggerPrice;
        public int SetupBarIndex;
        public bool Stop1Adjusted;
        public bool RunnerStopAdjusted;
        public bool DisplayNotified;
        public double ScratchTargetPrice;
        public double CurrentRunnerStop;
        public bool PositionSeen;
        public bool EntryComplete;
        public bool EntryCancelRequested;
        public bool HasRunner;
        public string TemplateName;
        public DateTime CreatedAt;
        public bool EntryStatusSeen;
        public bool T1Filled;          // Target1 gefuellt -> Runner-Trail darf laufen
        public DateTime FlatSince;     // Entry gefuellt, aber ATM meldet noch keine Position (Live-Verzoegerung)
        public double PostFillExtreme; // bester Kurs SEIT dem Fill (Long: hoechster, Short: tiefster) - fuer den Teilfill-Cancel
        public bool EntryPartial;      // Entry endete "Cancelled" mit Teilfill -> evtl. kein Runner-Bracket
        public bool RunnerProbed;      // Stop2 nach Teilfill einmal geprueft
        public bool Surprised;         // Box "SURPRISED" war beim Oeffnen der Position angehakt
        public DateTime OpenedAt;      // Bar-Zeit beim Oeffnen der Position (Trade-Log)
        // Trade-Log (gleiche Spalten wie das Trade-Log des ChartTraders)
        public TradeLogInfo Log;                                                     // Daten beim Scharfmachen
        public List<LogFill> EntryFills = new List<LogFill>();
        public List<LogFill> ExitFills = new List<LogFill>();
        public Dictionary<string, double> OrderPrices = new Dictionary<string, double>();    // letzter Preis je ATM-Order
        public Dictionary<string, double> ExpectedPrices = new Dictionary<string, double>(); // eigene Aenderungen (kein "manual")
        public List<string> ManualChanges = new List<string>();
        public bool Closed;
        public DateTime ClosedSeen;
    }

    // Daten des Setups fuer das Trade-Log (beim Scharfmachen erfasst, Bar[1] = Signal-Bar, Bar[2] = Vorbar).
    public class TradeLogInfo
    {
        public string Setup = "";              // z. B. "Smart Long", "Smart Long (No Runner)", "Momentum Short"
        public string EntryType = "Manual";    // Direct / PullbackLimit
        public DateTime SignalStart = DateTime.MinValue;
        public double SigO = double.NaN, SigH = double.NaN, SigL = double.NaN, SigC = double.NaN;
        public double PrevH = double.NaN, PrevL = double.NaN;
        public double Trigger = double.NaN, Limit = double.NaN, InitialSL = double.NaN;
        public int MaxRisk;
        public double DistEma = double.NaN;    // Close der Signal-Bar minus EMA, Punkte
        public double T1Price = double.NaN, T2Price = double.NaN;   // geplante Targets (erster Preis nach dem Fill)
        public DateTime ArmedAt = DateTime.MinValue;   // Bar-Zeit beim Scharfmachen (Zeile fuer Setups ohne Trade)
    }

    public class LogFill
    {
        public double Price;
        public int Qty;
        public DateTime Time;
        public string Name = "";               // Order-Name der ATM (Entry, Target1, Stop1, Stop2, ...)
        public bool IsStop;
        public double StopPrice;
        public bool IsLong;                    // Kauf-Ausfuehrung
    }

    // ===== SIGNALBARCHECK BEGIN =====
    // Reine Rechenlogik ohne NinjaTrader-Abhaengigkeit, damit sie sich ausserhalb
    // der Plattform gegen echte Bars testen laesst. Die Marker-Kommentare braucht
    // das Testprogramm, um genau diese Klasse aus der Datei zu schneiden.
    public struct SignalBarMetrics
    {
        public double Range;
        public double ClosePositionPercent;   // Close-Lage in Handelsrichtung, 100 = am Extrem
        public double BodyPercent;            // |Close-Open| / Range
        public double BreakoutWickPercent;    // Docht in Ausbruchsrichtung / Range
        public bool Shaved;                   // Close auf dem Extrem (kein Ausbruchsdocht)
    }

    public static class SignalBarCheck
    {
        public static SignalBarMetrics Measure(bool isLong, double open, double high, double low, double close, double tickSize)
        {
            SignalBarMetrics m = new SignalBarMetrics();
            m.Range = high - low;
            if (m.Range <= 0) return m;

            m.ClosePositionPercent = (isLong ? close - low : high - close) / m.Range * 100.0;
            m.BodyPercent          = Math.Abs(close - open) / m.Range * 100.0;
            m.BreakoutWickPercent  = (isLong ? high - close : close - low) / m.Range * 100.0;

            // Schliesst die Bar auf ihrem Extrem, gibt es in Ausbruchsrichtung keinen
            // Docht - Koerper- und Dochtpruefung sind dort gegenstandslos.
            m.Shaved = isLong ? close >= high - tickSize / 2.0 : close <= low + tickSize / 2.0;
            return m;
        }

        public static bool Evaluate(bool isLong,
            double open, double high, double low, double close, double tickSize,
            bool requireClosePosition, double closePositionPercent,
            bool requireMinBody, double minBodyPercent,
            bool requireNoBreakoutWick, double maxBreakoutWickPercent,
            out string reason)
        {
            return Evaluate(isLong, open, high, low, close, tickSize,
                requireClosePosition, closePositionPercent,
                requireMinBody, minBodyPercent,
                requireNoBreakoutWick, maxBreakoutWickPercent,
                false, 90, 10, out reason);
        }

        // Rejection Bar (Hand-Regel 5 seit 20.09.2026): Close sehr nah am Extrem in
        // Handelsrichtung und kaum Docht in Ausbruchsrichtung -> Koerper egal. Typisch:
        // langer Ablehnungsdocht auf der Gegenseite, Close 1 Tick neben dem Extrem.
        public static bool Evaluate(bool isLong,
            double open, double high, double low, double close, double tickSize,
            bool requireClosePosition, double closePositionPercent,
            bool requireMinBody, double minBodyPercent,
            bool requireNoBreakoutWick, double maxBreakoutWickPercent,
            bool allowRejectionBar, double rejectionMinClosePercent, double rejectionMaxWickPercent,
            out string reason)
        {
            SignalBarMetrics m = Measure(isLong, open, high, low, close, tickSize);
            if (m.Range <= 0)
            {
                reason = "Signal bar rejected: zero range";
                return false;
            }

            List<string> failed = new List<string>();

            bool rejection = allowRejectionBar
                && m.ClosePositionPercent >= rejectionMinClosePercent
                && m.BreakoutWickPercent <= rejectionMaxWickPercent;
            bool exempt = m.Shaved || rejection;

            if (requireClosePosition && m.ClosePositionPercent < closePositionPercent)
                failed.Add($"close {m.ClosePositionPercent:0.0}% < {closePositionPercent:0.#}%");

            if (requireMinBody && !exempt && m.BodyPercent < minBodyPercent)
                failed.Add($"body {m.BodyPercent:0.0}% < {minBodyPercent:0.#}%");

            if (requireNoBreakoutWick && !exempt && m.BreakoutWickPercent > maxBreakoutWickPercent)
                failed.Add($"breakout wick {m.BreakoutWickPercent:0.0}% > {maxBreakoutWickPercent:0.#}%");

            if (failed.Count == 0)
            {
                reason = "";
                return true;
            }

            reason = "Signal bar rejected: " + string.Join(" | ", failed);
            return false;
        }
    }
    // ===== SIGNALBARCHECK END =====

    public class LotsenhofSniper : Strategy
    {
        // UI Elements
        private Button atmToggleButton;
        private Button buyButton;
        private Button sellButton;
        private Button deepBuyButton;
        private Button deepSellButton;
        private Button momentumBuyButton;
        private Button momentumSellButton;
        private TextBlock lockoutLabel;
        private Button noRunnerBuyButton;
        private Button noRunnerSellButton;
        private Button scratchButton;
        private Button cancelButton;
        private CheckBox climaxCheckBox;
        private CheckBox surprisedCheckBox;
        private bool surprisedArmed;   // Box angehakt: die naechste Position, die oeffnet, uebernimmt es (dann geleert)
        private TextBlock climaxLabel;
        private Grid myGrid;
        
        // ATM State Machine
        private string[] availableAtms;
        private int currentAtmIndex = 0;
        private string trapAtmTemplateLong = "";
        private string trapAtmTemplateShort = "";

        // Trap State Variables
        private bool trapLongActive = false;
        private bool trapShortActive = false;
        private bool trapNoRunnerLong = false;
        private bool trapNoRunnerShort = false;
        private double trapTriggerPrice = 0.0;
        private double trapLimitPrice = 0.0;
        private double trapStopLossPrice = 0.0; 
        private int trapBarNumber = 0;
        private int MaxRiskTicks = 0;
        
        // Market Data Shadow Variables
        private double cHigh1, cLow1, cOpen1, cClose1, cHigh2, cLow2, cClose0, cTickSize;
        private int cBar;
        private object dataLock = new object();
        private object listLock = new object();
        
        private Dictionary<string, AtmState> trackedAtms = new Dictionary<string, AtmState>();

        // Wie lange eine frisch erzeugte ATM Zeit bekommt, sich zu melden,
        // bevor wir sie als nie entstanden verwerfen.
        private const double AtmStartupGraceSeconds = 5.0;

        // So lange darf eine gefuellte Entry ohne gemeldete ATM-Position bleiben,
        // bevor wir sie als (sofort wieder) geschlossen betrachten.
        private const double PositionConfirmGraceSeconds = 10.0;

        // Revenge-Trading-Sperre
        private NinjaTrader.Data.SessionIterator lockoutSession;
        private Account lockoutAccount;
        private string lockoutFilePath = "";
        private string tradeLogPath = "";   // LotsenhofSniper_trades.csv: eine Zeile je geschlossenem ATM-Trade
        private const string TradeLogHeader = "Replayed;Date;Open;Close;Instrument;Setup;Side;Qty;Entry;Exit;Points;Ticks;PnL;Duration;"
            + "SignalBarStart;SignalO;SignalH;SignalL;SignalC;PrevBarH;PrevBarL;Trigger;EntryType;LimitPrice;InitialSL;RiskTicks;"
            + "Template;MaxRiskTicks;T1Price;T2Price;ExitT1;ExitT1Time;ExitRunner;ExitRunnerTime;RunnerExitReason;ManualChanges;"
            + "DistEMA;Surprised";
        private const int JournalEmaPeriod = 21;           // DistEMA wie im ChartTrader (EMA 21 der Chart-Bars)
        private const double LogSettleSeconds = 5.0;       // so lange auf fehlende Ausfuehrungen warten
        private Account logAccount;
        private string logRunStarted = "";
        private List<AtmState> pendingLogs = new List<AtmState>();   // geschlossen, Zeile noch nicht geschrieben
        private TradeLogInfo trapLogLong, trapLogShort;
        private DateTime cSignalStart;
        private DateTime cTime0;                   // Zeit des letzten Ticks (Bar-Zeit, auch im Playback richtig)
        private double cEma1;
        private DateTime lockoutTradingDay = DateTime.MinValue;
        private int lossesToday = 0;
        private bool tradingLocked = false;
        // Sperre wirkt nur, wenn aktiviert UND (bei "Live Accounts Only") kein Playback-/Sim-Konto.
        private bool lockoutActive = false;

        // NT meldet pro geschlossenem Teil einer Position ein eigenes Trade-Objekt
        // (WADES: Stop1/Target1-OCO x2 + Stop2/Target2 = bis zu 3 Trades pro Position).
        // Deshalb wird pro Entry-Order gesammelt und erst die Netto-P&L der ganzen
        // Position als ein Verlust gezaehlt.
        private class PositionResult
        {
            public string Key;
            public string InstrumentName;
            public int ExpectedQty;      // gefuellte Menge der Entry-Order, 0 = unbekannt
            public int ClosedQty;
            public double Pnl;
            public int Parts;
            public DateTime LastUpdate;  // Wanduhr, nur fuer die Settle-Wartezeit
            public bool Finalized;
            public bool CountedAsLoss;
        }
        private readonly Dictionary<string, PositionResult> positionResults = new Dictionary<string, PositionResult>();
        // Wartezeit nach dem letzten Teil-Trade, bevor eine flache Position ohne
        // bekannte Soll-Menge abgeschlossen wird.
        private const double PositionSettleSeconds = 3.0;

        // Climax-Pause (Brooks TBTL: Ten Bars, Two Legs). Nach einem Climax wird die
        // Mit-Trend-Richtung gesperrt, bis genug Bars und Beine seit dem Extrem gelaufen sind.
        private bool climaxActive = false;
        private bool climaxIsBuy = true;        // true = Buy Climax (Hoch) -> Longs gesperrt
        private int climaxAnchorBar = -1;       // absoluter Bar-Index des Extrems
        private double climaxExtreme = 0;
        private int climaxLegs = 1;
        private bool climaxInLeg = true;
        private bool climaxBreakoutPending = false;  // kraeftiger Ausbruchsbar ueber/unter dem Extrem, wartet auf Folgebar
        private double climaxBreakoutClose = 0;
        private bool climaxResumed = false;

        // 10K-EMA (EMA 21 einer hoeheren Tick-Serie), wie die rote Linie im ChartTrader: gezeichnet nur durch die
        // exakten Werte an den Schluessen der 10K-Bars (gerade Strecken dazwischen), das letzte Stueck bis zur
        // aktuellen Bar ist vorlaeufig (laufende 10K-Bar mit ihrem bisherigen Close).
        private int htfBip = -1;                                   // BarsInProgress der 10K-Serie, -1 = aus
        private double htfK, htfEma = double.NaN, htfCurrent = double.NaN;
        private readonly List<int> htfPointBar = new List<int>();     // Index der 2000er-Bar mit dem letzten Tick der 10K-Bar
        private readonly List<double> htfPointVal = new List<double>();
        private int htfDrawn = -1;                                 // letzter fertig gezeichneter Stuetzpunkt

        [NinjaScriptProperty]
        [Display(Name="ATM Strategies List", Description="Comma separated list of ATM templates", Order=1, GroupName="1. Parameters")]
        public string AtmTemplatesList { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name="Max Pullback (%)", Description="Maximum pullback of the signal bar in percent", Order=2, GroupName="1. Parameters")]
        public double MaxPullbackPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Block Inside Bars", Description="Prevents trades if the signal bar is an inside bar.", Order=3, GroupName="1. Parameters")]
        public bool BlockInsideBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name="Max Slippage (Ticks)", Description="Accepted slippage for momentum stop-limit orders.", Order=4, GroupName="1. Parameters")]
        public int MaxSlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Max Risk (Ticks)", Description="Maximum allowed risk in ticks. Dynamically adjusts limit entry relative to safe structural stop.", Order=5, GroupName="1. Parameters")]
        public int DefaultMaxRiskTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="Smart Entry Offset (Ticks)", Description="Ticks to offset the limit order from the trigger (applies only to candles <= Max Risk).", Order=6, GroupName="1. Parameters")]
        public int SmartOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Max Runway (Ticks)", Description="Cancel the unfilled limit order if price moves this many ticks past the trigger without filling.", Order=8, GroupName="1. Parameters")]
        public int MaxRunwayTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Bars to Wait", Description="How many bars to keep a pending limit order alive before automatic cancellation.", Order=9, GroupName="1. Parameters")]
        public int BarsToWait { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Cancel Partial Fill After (Ticks)", Description="Nach einem Teilfill: storniert die Restmenge der Entry-Order, sobald der Preis so viele Ticks in Profitrichtung vom Einstand weggelaufen ist.", Order=12, GroupName="1. Parameters")]
        public int PartialFillCancelTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="No-Runner Template Suffix", Description="Wird an das aktuell gewaehlte ATM-Template angehaengt, wenn ein No-Runner-Button gedrueckt wird (z.B. WADE12 -> WADE12NR).", Order=13, GroupName="1. Parameters")]
        public string NoRunnerSuffix { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show only Smart buttons", Description="If true, only smart long/short entry buttons are showed.", Order=10, GroupName="1. Parameters")]
        public bool ShowOnlySmartButtons { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name="Enable non perfect signal bars", Description="Master-Override fuer den Signal-Bar-Filter (Gruppe 7). true = Filter wird komplett uebersprungen, jede Bar ist handelbar. false = alle aktivierten Kriterien aus Gruppe 7 muessen bestehen.", Order=11, GroupName="1. Parameters")]
        public bool EnableTradingNonPerfectBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name="Risk Tolerance (Ticks)", Description="Erlaubt der Signal-Bar, das eingestellte Max Risk um X Ticks zu überschreiten, ohne geblockt/tief gesetzt zu werden (z.B. für leichte Overshoots).", Order=6, GroupName="1. Parameters")]
        public int RiskToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Auto Break-Even", Description="Moves stop to entry as soon as Target 1 is filled.", Order=1, GroupName="2. Auto-Breakeven")]
        public bool EnableAutoBreakeven { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Auto BE Offset (Ticks)", Description="Ticks above/below entry for break-even (0 = exact entry).", Order=2, GroupName="2. Auto-Breakeven")]
        public int AutoBreakevenOffset { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Target 1 Name", Description="Name of the 1st target in ATM (Default: Target1)", Order=3, GroupName="2. Auto-Breakeven")]
        public string Target1Name { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Stop 1 Name", Description="Name of the initial stop in ATM (Default: Stop1)", Order=4, GroupName="2. Auto-Breakeven")]
        public string Stop1Name { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Runner Stop Name", Description="Name of the runner stop in ATM (Default: Stop2)", Order=5, GroupName="2. Auto-Breakeven")]
        public string RunnerStopName { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="Scratch Offset (Ticks)", Description="Ticks beyond entry (in profit direction) the targets are pulled to when Scratch is pressed.", Order=1, GroupName="4. Scratch Exit")]
        public int ScratchOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Runner Target Name", Description="Name of the runner target in ATM (Default: Target2). Silently ignored if the template has no such target.", Order=2, GroupName="4. Scratch Exit")]
        public string RunnerTargetName { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Auto Runner Trail", Description="After Auto-BE, automatically trails the runner stop behind confirmed swing points.", Order=1, GroupName="5. Runner Trail")]
        public bool EnableRunnerTrail { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Swing Strength (Bars)", Description="Bars required on each side of a pivot to confirm it as a swing point.", Order=2, GroupName="5. Runner Trail")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Min Swing Size (Ticks)", Description="Minimum prominence (pivot to opposing extreme since the pivot) for a swing to qualify. Filters out micro-swings so the trail respects meaningful structure only.", Order=3, GroupName="5. Runner Trail")]
        public int MinSwingSizeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name="Trail Buffer (Ticks)", Description="Ticks beyond the swing point where the runner stop is placed.", Order=4, GroupName="5. Runner Trail")]
        public int TrailBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Daily Loss Lockout", Description="Sperrt nach der eingestellten Anzahl Verlust-Trades auf dem gewaehlten Konto alle Einstiege bis zum naechsten Handelstag. Scratch und Cancel bleiben aktiv.", Order=1, GroupName="6. Risk Lockout")]
        public bool EnableDailyLossLockout { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Lockout Live Accounts Only", Description="True: Auf Playback- und Sim-Konten (Name beginnt mit 'Playback' oder 'Sim') ist die Sperre automatisch aus. Echtgeld-Konten bleiben geschuetzt.", Order=4, GroupName="6. Risk Lockout")]
        public bool LockoutLiveAccountsOnly { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name="Losses Until Lockout", Description="Anzahl Verlust-Trades auf dem Konto, nach denen gesperrt wird.", Order=2, GroupName="6. Risk Lockout")]
        public int LossesUntilLockout { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1000000.0)]
        [Display(Name="Min Loss to Count (Currency)", Description="Verluste unterhalb dieses Betrags werden ignoriert (0 = jeder Verlust zaehlt). Verhindert, dass ein Scratch mit Kommission den Tag sperrt.", Order=3, GroupName="6. Risk Lockout")]
        public double MinLossToCount { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Require Close Position", Description="Signal-Bar muss in Handelsrichtung weit genug schliessen (siehe Close Position %).", Order=1, GroupName="7. Signal Bar Filter")]
        public bool RequireClosePosition { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Close Position (%)", Description="Long: (Close-Low)/(High-Low), Short: (High-Close)/(High-Low) muss mindestens so gross sein.", Order=2, GroupName="7. Signal Bar Filter")]
        public int ClosePositionPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Require Min Body", Description="Koerper muss mindestens Min Body Ratio % der Range haben. Entfaellt, wenn die Bar auf ihrem Extrem schliesst.", Order=3, GroupName="7. Signal Bar Filter")]
        public bool RequireMinBody { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Min Body Ratio (%)", Description="|Close-Open|/(High-Low) muss mindestens so gross sein.", Order=4, GroupName="7. Signal Bar Filter")]
        public int MinBodyRatioPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Require No Breakout Wick", Description="Docht in Ausbruchsrichtung darf hoechstens Max Breakout Wick % der Range sein. Entfaellt, wenn die Bar auf ihrem Extrem schliesst.", Order=5, GroupName="7. Signal Bar Filter")]
        public bool RequireNoBreakoutWick { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Max Breakout Wick (%)", Description="Long: (High-Close)/(High-Low), Short: (Close-Low)/(High-Low) darf hoechstens so gross sein.", Order=6, GroupName="7. Signal Bar Filter")]
        public int MaxBreakoutWickPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Allow Rejection Bar", Description="Hand-Regel 5: Bei Close-Position >= Rejection Min Close und Ausbruchsdocht <= Rejection Max Wick entfaellt die Koerper-Pruefung (z.B. Pin Bar mit Close 1 Tick neben dem Extrem).", Order=7, GroupName="7. Signal Bar Filter")]
        public bool AllowRejectionBar { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Rejection Min Close (%)", Description="Mindest-Close-Position in Handelsrichtung fuer die Rejection-Bar-Ausnahme.", Order=8, GroupName="7. Signal Bar Filter")]
        public int RejectionMinClosePercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Rejection Max Wick (%)", Description="Hoechster Docht in Ausbruchsrichtung fuer die Rejection-Bar-Ausnahme.", Order=9, GroupName="7. Signal Bar Filter")]
        public int RejectionMaxWickPercent { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="TBTL Min Bars", Description="Geschlossene Bars seit dem Climax-Extrem (inkl. Signal Bar), bevor die Mit-Trend-Richtung wieder frei ist.", Order=1, GroupName="8. Climax (TBTL)")]
        public int TbtlMinBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="TBTL Min Legs", Description="Anzahl Korrekturbeine seit dem Extrem. Neues Bein = nach einem Bar mit hoeherem Hoch (Buy Climax) ein Bar mit tieferem Tief; Short spiegelbildlich.", Order=2, GroupName="8. Climax (TBTL)")]
        public int TbtlMinLegs { get; set; }

        [NinjaScriptProperty]
        [Range(3, 200)]
        [Display(Name="Climax Lookback Bars", Description="Beim Anklicken wird in so vielen geschlossenen Bars das juengste Extrem (Hoch oder Tief) als Climax gesucht.", Order=3, GroupName="8. Climax (TBTL)")]
        public int ClimaxLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Trend Resume", Description="Pause endet vorzeitig, wenn der Trend nach dem Climax bestaetigt weiterlaeuft: kraeftiger Bar schliesst ueber (Short: unter) dem Climax-Extrem und der Folgebar schliesst noch hoeher (tiefer). Jedes andere neue Extrem startet die Zaehlung neu.", Order=4, GroupName="8. Climax (TBTL)")]
        public bool EnableTrendResume { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Resume Min Body (%)", Description="Mindestkoerper des Ausbruchsbars in Prozent seiner Range.", Order=5, GroupName="8. Climax (TBTL)")]
        public int ResumeMinBodyPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Resume Min Close Position (%)", Description="Close des Ausbruchsbars in Trendrichtung, in Prozent der Range (Long: (Close-Low)/(High-Low)).", Order=6, GroupName="8. Climax (TBTL)")]
        public int ResumeMinClosePercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show 10K EMA", Description="Rote EMA einer hoeheren Tick-Serie im Chart (wie im ChartTrader). Aus = keine zweite Datenserie, keine Linie. Aenderung wirkt nach dem Neuladen der Strategie.", Order=1, GroupName="9. Higher EMA")]
        public bool ShowHtfEma { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Higher Series (Ticks)", Description="Ticks je Bar der hoeheren Serie (10000 = 10K).", Order=2, GroupName="9. Higher EMA")]
        public int HtfEmaTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="EMA Period", Description="Periode der EMA auf den Closes der hoeheren Serie.", Order=3, GroupName="9. Higher EMA")]
        public int HtfEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey LONG (Smart)", Description="Key for Smart Long Entry", Order=1, GroupName="3. Hotkeys")]
        public SniperKey HotkeyLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey SHORT (Smart)", Description="Key for Smart Short Entry", Order=2, GroupName="3. Hotkeys")]
        public SniperKey HotkeyShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey MOMENTUM LONG", Description="Ignores candle size", Order=3, GroupName="3. Hotkeys")]
        public SniperKey HotkeyMomentumLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey MOMENTUM SHORT", Description="Ignores candle size", Order=4, GroupName="3. Hotkeys")]
        public SniperKey HotkeyMomentumShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey CANCEL", Description="Key to cancel all traps", Order=5, GroupName="3. Hotkeys")]
        public SniperKey HotkeyCancel { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey SCRATCH", Description="Key to pull all targets to entry +/- Scratch Offset", Order=6, GroupName="3. Hotkeys")]
        public SniperKey HotkeyScratch { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Lotsenhof Sniper: Smart PA entries with structural risk caps, auto break-even and swing-based runner trailing.";
                Name                        = "LotsenhofSniper"; 
                Calculate                   = Calculate.OnEachTick;
                IsOverlay                   = true;
                
                AtmTemplatesList            = "WADE10,WADE12,WADE14,WADE16,WADE8,WADE6"; 
                MaxPullbackPercent          = 76.0; 
                BlockInsideBars             = true; 
                MaxSlippageTicks            = 2; 
                DefaultMaxRiskTicks         = 9; 
                SmartOffsetTicks            = 2;
                MaxRunwayTicks              = 10;              
                EnableAutoBreakeven         = true;
                EnableTradingNonPerfectBars = false;
                AutoBreakevenOffset         = 0;
                BarsToWait                  = 3; // Pending limit orders stay alive for 3 bars
                PartialFillCancelTicks      = 10;
                NoRunnerSuffix              = "NR";
                ShowOnlySmartButtons        = true;
                Target1Name                 = "Target1";
                Stop1Name                   = "Stop1";
                RunnerStopName              = "Stop2";
                RiskToleranceTicks          = 2; // Standardmäßig 2 Ticks "Gummiband"
                ScratchOffsetTicks          = 2;
                RunnerTargetName            = "Target2";
                EnableRunnerTrail           = true;
                SwingStrength               = 2;
                MinSwingSizeTicks           = 8;  // pivot must protrude at least 2 points on ES
                TrailBufferTicks            = 1;
                EnableDailyLossLockout      = true;
                LockoutLiveAccountsOnly     = true;
                LossesUntilLockout          = 2;
                MinLossToCount              = 50;
                RequireClosePosition        = true;
                ClosePositionPercent        = 70;
                RequireMinBody              = true;
                MinBodyRatioPercent         = 5;
                RequireNoBreakoutWick       = true;
                TbtlMinBars                 = 10;
                TbtlMinLegs                 = 2;
                ClimaxLookbackBars          = 20;
                EnableTrendResume           = true;
                ResumeMinBodyPercent        = 50;
                ResumeMinClosePercent       = 70;
                ShowHtfEma                  = true;
                HtfEmaTicks                 = 10000;
                HtfEmaPeriod                = 21;
                AddPlot(new Stroke(Brushes.Red, 2), PlotStyle.Line, "Ema10K");
                MaxBreakoutWickPercent      = 25;
                AllowRejectionBar           = true;
                RejectionMinClosePercent    = 90;
                RejectionMaxWickPercent     = 10;

                HotkeyLong                  = SniperKey.W;
                HotkeyShort                 = SniperKey.S; 
                HotkeyMomentumLong          = SniperKey.Ctrl_Up; 
                HotkeyMomentumShort         = SniperKey.Ctrl_Down; 
                HotkeyCancel                = SniperKey.X;
                HotkeyScratch               = SniperKey.C;
            }
            else if (State == State.Configure)
            {
                if (ShowHtfEma)
                {
                    AddDataSeries(NinjaTrader.Data.BarsPeriodType.Tick, HtfEmaTicks);
                    htfBip = 1;
                }
            }
            else if (State == State.DataLoaded)
            {
                htfK = 2.0 / (HtfEmaPeriod + 1);
                if (string.IsNullOrWhiteSpace(AtmTemplatesList))
                {
                    availableAtms = new string[] { "Default_ATM" };
                }
                else
                {
                    availableAtms = AtmTemplatesList.Split(',');
                    for (int i = 0; i < availableAtms.Length; i++)
                    {
                        availableAtms[i] = availableAtms[i].Trim();
                    }
                }
                currentAtmIndex = 0;
                lockoutSession = new NinjaTrader.Data.SessionIterator(Bars);
                lockoutFilePath = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "LotsenhofSniper_lockout.txt");
                Match match = Regex.Match(availableAtms[currentAtmIndex], @"\d+");
                if (match.Success) {
                    MaxRiskTicks = int.Parse(match.Value) - 1;
                } else {
                    MaxRiskTicks = DefaultMaxRiskTicks;
                }
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() => { CreateWPFControls(); });
                }
            }
            else if (State == State.Realtime)
            {
                // Erst hier steht das gewaehlte Konto sicher zur Verfuegung.
                lockoutAccount = Account;
                bool simAccount = IsSimulationAccount(lockoutAccount);
                lockoutActive = EnableDailyLossLockout && !(LockoutLiveAccountsOnly && simAccount);
                if (lockoutAccount != null) lockoutAccount.AddTrade += OnAccountAddTrade;
                else if (lockoutActive) Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] WARNING: no account attached, daily loss lockout is inactive.");
                // Trade-Log: Ausfuehrungen und Order-Aenderungen der ATM-Orders mitlesen.
                logAccount = Account;
                logRunStarted = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                if (logAccount != null) { logAccount.ExecutionUpdate += OnLogExecutionUpdate; logAccount.OrderUpdate += OnLogOrderUpdate; }
                SelectTradeLogPath();
                RefreshTradingDay(true);
                if (EnableDailyLossLockout && !lockoutActive)
                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Loss lockout OFF for {AccountLabel()} (Playback/Sim account, 'Lockout Live Accounts Only' = True).");
                if (lockoutActive)
                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Loss lockout armed for {AccountLabel()}, trading day {lockoutTradingDay:yyyy-MM-dd}, locked: {tradingLocked}. File: {lockoutFilePath}");
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() => { DisposeWPFControls(); });
                }
                RemoveDrawObject("TrapTrigger");
                RemoveDrawObject("TrapLimit");
                RemoveDrawObject("SmartOrderError");
                RemoveDrawObject("BE_Notice");
                RemoveDrawObject("SL_Notice");
                RemoveDrawObject("Scratch_Notice");
                RemoveDrawObject("PartialFill_Notice");
                RemoveDrawObject("Trail_Notice");
                if (lockoutAccount != null) { lockoutAccount.AddTrade -= OnAccountAddTrade; lockoutAccount = null; }
                if (logAccount != null) { logAccount.ExecutionUpdate -= OnLogExecutionUpdate; logAccount.OrderUpdate -= OnLogOrderUpdate; logAccount = null; }
                TryFlushLogs(true);
                lock(listLock) { trackedAtms.Clear(); }
            }
        }

        private void CreateWPFControls()
        {
            myGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 30, 10, 0)
            };

            for(int i=0; i<13; i++) myGrid.RowDefinitions.Add(new RowDefinition());
            // Spalte 0 = Standard-ATM (mit Runner), Spalte 1 = No-Runner-Variante.
            for(int i=0; i<2; i++) myGrid.ColumnDefinitions.Add(new ColumnDefinition());

            atmToggleButton = new Button 
            { 
                Content = $"ATM: {availableAtms[currentAtmIndex]}", 
                Foreground = Brushes.White, 
                Background = Brushes.MidnightBlue, 
                FontWeight = FontWeights.Bold, 
                Margin = new Thickness(2), 
                Padding = new Thickness(10, 5, 10, 5) 
            };
            atmToggleButton.Click += (o, e) => 
            {
                ChartControl.Dispatcher.InvokeAsync(() => 
                {
                    currentAtmIndex++;
                    if (currentAtmIndex >= availableAtms.Length) currentAtmIndex = 0;
                    atmToggleButton.Content = $"ATM: {availableAtms[currentAtmIndex]}";
                    Match match = Regex.Match(availableAtms[currentAtmIndex], @"\d+");
                    if (match.Success) {
                        MaxRiskTicks = int.Parse(match.Value) - 1;
                    } else {
                        MaxRiskTicks = DefaultMaxRiskTicks;
                    }
                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] ATM Switched to: {availableAtms[currentAtmIndex]} and Risk to: {MaxRiskTicks}");
                });
            };
            Grid.SetRow(atmToggleButton, 0);
            Grid.SetColumnSpan(atmToggleButton, 2);

            buyButton = new Button { Content = "LONG (Smart)", Foreground = Brushes.White, Background = Brushes.DarkGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            buyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartLong(), null);
            Grid.SetRow(buyButton, 1);
            Grid.SetColumn(buyButton, 0);

            sellButton = new Button { Content = "SHORT (Smart)", Foreground = Brushes.White, Background = Brushes.DarkRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            sellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartShort(), null);
            Grid.SetRow(sellButton, 2);
            Grid.SetColumn(sellButton, 0);

            noRunnerBuyButton = new Button { Content = "LONG (No Runner)", Foreground = Brushes.White, Background = Brushes.ForestGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            noRunnerBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartLong(true), null);
            Grid.SetRow(noRunnerBuyButton, 1);
            Grid.SetColumn(noRunnerBuyButton, 1);

            noRunnerSellButton = new Button { Content = "SHORT (No Runner)", Foreground = Brushes.White, Background = Brushes.Firebrick, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            noRunnerSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartShort(true), null);
            Grid.SetRow(noRunnerSellButton, 2);
            Grid.SetColumn(noRunnerSellButton, 1);

            deepBuyButton = new Button { Content = "DEEP LONG (Max PB)", Foreground = Brushes.White, Background = Brushes.SeaGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            deepBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteDeepSmartLong(), null);
            Grid.SetRow(deepBuyButton, 3);
            Grid.SetColumnSpan(deepBuyButton, 2);

            deepSellButton = new Button { Content = "DEEP SHORT (Max PB)", Foreground = Brushes.White, Background = Brushes.Firebrick, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            deepSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteDeepSmartShort(), null);
            Grid.SetRow(deepSellButton, 4);
            Grid.SetColumnSpan(deepSellButton, 2);

            momentumBuyButton = new Button { Content = "MOMENTUM LONG", Foreground = Brushes.White, Background = Brushes.MediumSeaGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumLong(), null);
            Grid.SetRow(momentumBuyButton, 5);
            Grid.SetColumnSpan(momentumBuyButton, 2);

            momentumSellButton = new Button { Content = "MOMENTUM SHORT", Foreground = Brushes.White, Background = Brushes.IndianRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumShort(), null);
            Grid.SetRow(momentumSellButton, 6);
            Grid.SetColumnSpan(momentumSellButton, 2);

            scratchButton = new Button { Content = $"SCRATCH TP (+{ScratchOffsetTicks})", Foreground = Brushes.Black, Background = Brushes.Gold, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            scratchButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteScratch(), null);
            Grid.SetRow(scratchButton, 7);
            Grid.SetColumnSpan(scratchButton, 2);

            cancelButton = new Button { Content = "CANCEL ALL", Foreground = Brushes.White, Background = Brushes.DarkOrange, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            cancelButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteCancel(), null);
            Grid.SetRow(cancelButton, 8);
            Grid.SetColumnSpan(cancelButton, 2);

            lockoutLabel = new TextBlock
            {
                Text = "",
                Foreground = Brushes.White,
                Background = Brushes.DarkRed,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2),
                Padding = new Thickness(6, 4, 6, 4),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(lockoutLabel, 9);
            Grid.SetColumnSpan(lockoutLabel, 2);

            climaxCheckBox = new CheckBox
            {
                Content = "CLIMAX (TBTL)",
                Foreground = Brushes.White,
                Background = Brushes.DimGray,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 4, 2, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            climaxCheckBox.Checked   += (o, e) => TriggerCustomEvent(state => StartClimax(), null);
            climaxCheckBox.Unchecked += (o, e) => TriggerCustomEvent(state => StopClimax("unchecked"), null);
            Grid.SetRow(climaxCheckBox, 10);
            Grid.SetColumnSpan(climaxCheckBox, 2);

            climaxLabel = new TextBlock
            {
                Text = "",
                Foreground = Brushes.Black,
                Background = Brushes.Orange,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2),
                Padding = new Thickness(6, 4, 6, 4),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(climaxLabel, 11);
            Grid.SetColumnSpan(climaxLabel, 2);

            // SURPRISED: vor dem Trade anhaken. Die Position uebernimmt es beim Oeffnen (Output-Fenster),
            // danach wird die Box geleert; spaeter angehakt zaehlt fuer den naechsten Trade.
            // Handeln geht auch ohne.
            surprisedCheckBox = new CheckBox
            {
                Content = "SURPRISED (before the trade)",
                Foreground = Brushes.White,
                Background = Brushes.DimGray,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 4, 2, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            surprisedCheckBox.Checked   += (o, e) => TriggerCustomEvent(state => SetSurprised(true), null);
            surprisedCheckBox.Unchecked += (o, e) => TriggerCustomEvent(state => SetSurprised(false), null);
            Grid.SetRow(surprisedCheckBox, 12);
            Grid.SetColumnSpan(surprisedCheckBox, 2);

            myGrid.Children.Add(atmToggleButton);
            myGrid.Children.Add(buyButton);
            myGrid.Children.Add(sellButton);
            myGrid.Children.Add(noRunnerBuyButton);
            myGrid.Children.Add(noRunnerSellButton);
            if (!ShowOnlySmartButtons){
                myGrid.Children.Add(deepBuyButton);
                myGrid.Children.Add(deepSellButton);
                myGrid.Children.Add(momentumBuyButton);
                myGrid.Children.Add(momentumSellButton);
            }
            myGrid.Children.Add(scratchButton);
            myGrid.Children.Add(cancelButton);
            myGrid.Children.Add(lockoutLabel);
            myGrid.Children.Add(climaxCheckBox);
            myGrid.Children.Add(climaxLabel);
            myGrid.Children.Add(surprisedCheckBox);
            
            UserControlCollection.Add(myGrid);

            if (ChartPanel != null) ChartPanel.PreviewKeyDown += ChartPanel_PreviewKeyDown;
        }

        private bool IsHotkeyPressed(SniperKey configuredKey, KeyEventArgs e)
        {
            string keyString = configuredKey.ToString();
            bool requiresCtrl = keyString.StartsWith("Ctrl_");

            if (requiresCtrl)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return false;
                keyString = keyString.Replace("Ctrl_", "");
            }
            else
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return false;
            }

            Key targetKey = (Key)Enum.Parse(typeof(Key), keyString);
            return e.Key == targetKey;
        }

        private void ChartPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsHotkeyPressed(HotkeyLong, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteSmartLong(), null); }
            else if (IsHotkeyPressed(HotkeyShort, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteSmartShort(), null); }
            else if (IsHotkeyPressed(HotkeyMomentumLong, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteMomentumLong(), null); }
            else if (IsHotkeyPressed(HotkeyMomentumShort, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteMomentumShort(), null); }
            else if (IsHotkeyPressed(HotkeyCancel, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteCancel(), null); }
            else if (IsHotkeyPressed(HotkeyScratch, e)) { e.Handled = true; TriggerCustomEvent(state => ExecuteScratch(), null); }
        }

        // Ob ein ATM-Template existiert, laesst sich nur ueber die Datei pruefen.
        // Ohne diese Vorpruefung faellt ein Tippfehler oder ein geloeschtes Template
        // erst auf, wenn der Trap ausloest - also im denkbar schlechtesten Moment.
        // Im Zweifel (Pfad unlesbar) lassen wir durch, statt den Handel zu blockieren.
        private bool IsAtmTemplateMissing(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName)) return true;

            string path;
            try { path = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy", templateName + ".xml"); }
            catch { return false; }

            try { if (System.IO.File.Exists(path)) return false; }
            catch { return false; }

            Draw.TextFixed(this, "SmartOrderError", $"ERROR: ATM template '{templateName}' not found!\n{path}", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] ATM template '{templateName}' does not exist ({path}) - entry aborted before arming.");
            return true;
        }

        // AtmStrategyCreate meldet Fehler ausschliesslich ueber diesen Callback.
        // Wird er ignoriert, sieht ein fehlgeschlagener Einstieg (z.B. fehlendes
        // Template) von aussen wie eine stille Stornierung aus.
        private void OnAtmCreateResult(ErrorCode errorCode, string errorMessage, string atmId, string templateName)
        {
            if (errorCode == ErrorCode.NoError) return;
            TriggerCustomEvent(o => ReportAtmCreateFailure(errorCode, errorMessage, atmId, templateName), null);
        }

        private void ReportAtmCreateFailure(ErrorCode errorCode, string errorMessage, string atmId, string templateName)
        {
            AtmState failed;
            lock(listLock) { trackedAtms.TryGetValue(atmId, out failed); trackedAtms.Remove(atmId); }
            if (failed != null) AtmEndedWithoutTrade(failed, "ATM create failed");
            else SetupEndedWithoutTrade("ATM create failed");

            string detail = string.IsNullOrWhiteSpace(errorMessage) ? errorCode.ToString() : $"{errorCode} - {errorMessage}";
            Draw.TextFixed(this, "SmartOrderError", $"ATM CREATE FAILED (template '{templateName}')\n{detail}", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] AtmStrategyCreate failed for template '{templateName}': {detail}. Tracking for {atmId} dropped.");
        }

        #region Climax-Pause (TBTL)

        // Wird per Checkbox gestartet (Strategie-Thread via TriggerCustomEvent).
        // Sucht das juengste Extrem der letzten ClimaxLookbackBars geschlossenen Bars
        // und spielt alle Bars danach nach, damit auch ein spaeter Klick korrekt zaehlt.
        private void StartClimax()
        {
            if (CurrentBar < 3) { StopClimax("not enough bars"); return; }

            int lookback = Math.Min(ClimaxLookbackBars, CurrentBar - 1);
            int hiAgo = 1, loAgo = 1;
            for (int b = 1; b <= lookback; b++)
            {
                if (High[b] > High[hiAgo]) hiAgo = b;
                if (Low[b] < Low[loAgo]) loAgo = b;
            }

            bool isBuy;
            if (hiAgo != loAgo) isBuy = hiAgo < loAgo;          // juengeres Extrem gewinnt
            else isBuy = Close[hiAgo] >= Open[hiAgo];            // Outside Bar: Richtung des Bars

            climaxIsBuy = isBuy;
            climaxResumed = false;
            int anchorAgo = isBuy ? hiAgo : loAgo;
            ResetClimaxAnchor(CurrentBar - anchorAgo, isBuy ? High[anchorAgo] : Low[anchorAgo]);
            climaxActive = true;

            // Ein bereits scharfer Mit-Trend-Trap wuerde die Pause sonst unterlaufen.
            if (isBuy && trapLongActive)  { trapLongActive = false;  RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); TrapEndedWithoutTrade(true, "trap cancelled by climax"); }
            if (!isBuy && trapShortActive) { trapShortActive = false; RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); TrapEndedWithoutTrade(false, "trap cancelled by climax"); }

            for (int b = anchorAgo - 1; b >= 1; b--) ProcessClimaxBar(b);

            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Climax started: {(isBuy ? "BUY climax (high)" : "SELL climax (low)")} {climaxExtreme} at {Time[CurrentBar - climaxAnchorBar]:HH:mm:ss}. {(isBuy ? "Longs" : "Shorts")} blocked until {TbtlMinBars} bars / {TbtlMinLegs} legs.");
            CheckClimaxRelease();
        }

        private void StopClimax(string reason)
        {
            // Nach "released" feuert das programmatische Abhaken erneut - dann nichts tun,
            // damit "TBTL OK" sichtbar bleibt.
            if (!climaxActive && reason == "unchecked") return;
            bool wasActive = climaxActive;
            climaxActive = false;
            RemoveDrawObject("ClimaxMark");
            if (wasActive) Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Climax pause ended ({reason}).");
            UpdateClimaxUi(reason == "released" ? "TBTL OK" : reason == "resumed" ? "TREND RESUMED" : "");

            if (ChartControl != null && climaxCheckBox != null)
                ChartControl.Dispatcher.InvokeAsync(() => { if (climaxCheckBox.IsChecked == true) climaxCheckBox.IsChecked = false; });
        }

        private void ResetClimaxAnchor(int anchorBar, double extreme)
        {
            climaxAnchorBar = anchorBar;
            climaxExtreme = extreme;
            climaxLegs = 1;
            climaxInLeg = true;
            climaxBreakoutPending = false;
        }

        // Ein geschlossener Bar (barsAgo >= 1). Buy Climax:
        // - Folgebar nach kraeftigem Ausbruch schliesst hoeher -> Trend laeuft weiter (Pause endet)
        // - neues Hoch -> Zaehlung startet neu; war es ein kraeftiger Ausbruchsbar mit Close
        //   ueber dem alten Extrem, wird auf die Folgebar gewartet
        // - im Bein: Bar mit hoeherem Hoch als der Vorbar = Gegenbewegung, Bein beendet
        // - in der Gegenbewegung: Bar mit tieferem Tief als der Vorbar = neues Bein
        // Sell Climax spiegelbildlich.
        private void ProcessClimaxBar(int barsAgo)
        {
            if (climaxResumed) return;

            double o = Open[barsAgo], h = High[barsAgo], l = Low[barsAgo], c = Close[barsAgo];
            double ph = High[barsAgo + 1], pl = Low[barsAgo + 1];

            if (climaxBreakoutPending)
            {
                climaxBreakoutPending = false;
                bool follow = climaxIsBuy ? c > climaxBreakoutClose : c < climaxBreakoutClose;
                if (follow)
                {
                    climaxResumed = true;
                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Climax: follow-through bar {Time[barsAgo]:HH:mm:ss} closed {c} beyond breakout close {climaxBreakoutClose} - trend resumed.");
                    return;
                }
            }

            if (climaxIsBuy)
            {
                if (h > climaxExtreme)
                {
                    double oldExtreme = climaxExtreme;
                    ResetClimaxAnchor(CurrentBar - barsAgo, h);
                    if (EnableTrendResume && c > oldExtreme && c > o && IsStrongResumeBar(o, h, l, c, true))
                    {
                        climaxBreakoutPending = true;
                        climaxBreakoutClose = c;
                    }
                    return;
                }
                if (climaxInLeg) { if (h > ph) climaxInLeg = false; }
                else if (l < pl) { climaxInLeg = true; climaxLegs++; }
            }
            else
            {
                if (l < climaxExtreme)
                {
                    double oldExtreme = climaxExtreme;
                    ResetClimaxAnchor(CurrentBar - barsAgo, l);
                    if (EnableTrendResume && c < oldExtreme && c < o && IsStrongResumeBar(o, h, l, c, false))
                    {
                        climaxBreakoutPending = true;
                        climaxBreakoutClose = c;
                    }
                    return;
                }
                if (climaxInLeg) { if (l < pl) climaxInLeg = false; }
                else if (h > ph) { climaxInLeg = true; climaxLegs++; }
            }
        }

        private bool IsStrongResumeBar(double o, double h, double l, double c, bool isLong)
        {
            double range = h - l;
            if (range <= 0) return false;
            double bodyPct = Math.Abs(c - o) / range * 100.0;
            double closePct = (isLong ? (c - l) : (h - c)) / range * 100.0;
            return bodyPct >= ResumeMinBodyPercent && closePct >= ResumeMinClosePercent;
        }

        private int ClimaxBarsSince()
        {
            // Geschlossene Bars nach dem Extrem, der letzte (= Signal Bar) eingeschlossen.
            return Math.Max(0, (CurrentBar - 1) - climaxAnchorBar);
        }

        private void UpdateClimax()
        {
            if (!climaxActive || CurrentBar < 3) return;
            int before = climaxAnchorBar;
            ProcessClimaxBar(1);
            if (climaxAnchorBar != before)
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Climax extended: new {(climaxIsBuy ? "high" : "low")} {climaxExtreme} - TBTL count restarts.");
            CheckClimaxRelease();
        }

        private void CheckClimaxRelease()
        {
            if (!climaxActive) return;

            int bars = ClimaxBarsSince();
            int barsAgo = CurrentBar - climaxAnchorBar;
            if (barsAgo >= 0 && barsAgo <= CurrentBar)
            {
                double y = climaxIsBuy ? climaxExtreme + 2 * TickSize : climaxExtreme - 2 * TickSize;
                Draw.Text(this, "ClimaxMark", "C", barsAgo, y, Brushes.Orange);
            }

            if (climaxResumed)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] TREND RESUMED after climax - {(climaxIsBuy ? "Longs" : "Shorts")} released. Entry still needs a valid setup.");
                StopClimax("resumed");
                return;
            }

            if (bars >= TbtlMinBars && climaxLegs >= TbtlMinLegs)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] TBTL complete: {bars} bars, {climaxLegs} legs since {climaxExtreme}. {(climaxIsBuy ? "Longs" : "Shorts")} released.");
                StopClimax("released");
                return;
            }
            UpdateClimaxUi();
        }

        private bool IsClimaxBlocked(AtmDirection dir)
        {
            if (!climaxActive) return false;
            bool withTrend = (climaxIsBuy && dir == AtmDirection.Long) || (!climaxIsBuy && dir == AtmDirection.Short);
            if (!withTrend) return false;

            string msg = $"BLOCKED: climax pause (TBTL) - {ClimaxBarsSince()}/{TbtlMinBars} bars, {Math.Min(climaxLegs, TbtlMinLegs)}/{TbtlMinLegs} legs since {climaxExtreme}.";
            Draw.TextFixed(this, "SmartOrderError", msg, TextPosition.Center, Brushes.Orange, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Entry {dir} blocked: {msg}");
            return true;
        }

        // Strategie-Thread (TriggerCustomEvent aus der Box).
        private void SetSurprised(bool on)
        {
            if (surprisedArmed == on) return;
            surprisedArmed = on;
            if (!on) return;
            bool inTrade = false;
            lock(listLock) { foreach (AtmState st in trackedAtms.Values) if (st.PositionSeen) { inTrade = true; break; } }
            if (inTrade)
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Surprised: counts for the next trade (the open position was entered without it).");
        }

        #region Trade-Log (gleiche Spalten und Formate wie journal/trade_log.csv des ChartTraders)

        // Excel auf deutschem System: ';', Dezimalkomma, UTF-8 mit BOM, CRLF. Die Datei wird nur angehaengt (Live-Daten
        // gehen nie verloren); "Replayed" = Start dieser Strategie-Instanz. Datei mit anderem Kopf -> *_v2.csv.
        private void SelectTradeLogPath()
        {
            string dir = NinjaTrader.Core.Globals.UserDataDir;
            tradeLogPath = System.IO.Path.Combine(dir, "LotsenhofSniper_trades.csv");
            try
            {
                if (System.IO.File.Exists(tradeLogPath))
                {
                    string first;
                    using (System.IO.StreamReader r = new System.IO.StreamReader(tradeLogPath, true)) first = r.ReadLine();
                    if (first != TradeLogHeader) tradeLogPath = System.IO.Path.Combine(dir, "LotsenhofSniper_trades_v2.csv");
                }
            }
            catch (Exception) { }
        }

        private TradeLogInfo MakeLogInfo(string setup, double trigger, double limit, double stop)
        {
            TradeLogInfo i = new TradeLogInfo { Setup = setup, Trigger = trigger, InitialSL = stop, MaxRisk = MaxRiskTicks };
            bool pullback = !double.IsNaN(limit) && Math.Abs(limit - trigger) > TickSize / 2;
            i.EntryType = pullback ? "PullbackLimit" : "Direct";
            i.Limit = pullback ? limit : double.NaN;
            lock(dataLock)
            {
                i.SignalStart = cSignalStart;
                i.ArmedAt = cTime0;
                i.SigO = cOpen1; i.SigH = cHigh1; i.SigL = cLow1; i.SigC = cClose1;
                i.PrevH = cHigh2; i.PrevL = cLow2;
                i.DistEma = cClose1 - cEma1;
            }
            return i;
        }

        // Eigene Aenderungen an Stops/Targets (Initial-Stop, Auto-BE, Trail, Scratch) zaehlen nicht als "ManualChanges".
        private void ChangeStopTargetLogged(double limitPrice, double stopPrice, string orderName, string atmId)
        {
            AtmState st;
            lock(listLock) { trackedAtms.TryGetValue(atmId, out st); }
            if (st != null && !string.IsNullOrEmpty(orderName)) st.ExpectedPrices[orderName] = limitPrice > 0 ? limitPrice : stopPrice;
            AtmStrategyChangeStopTarget(limitPrice, stopPrice, orderName, atmId);
        }

        private bool IsOurInstrument(NinjaTrader.Cbi.Instrument inst)
        {
            return inst != null && Instrument != null && inst.FullName == Instrument.FullName;
        }

        // Account-Thread: Werte kopieren, Verarbeitung auf dem Strategie-Thread.
        private void OnLogExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e == null || e.Execution == null || !IsOurInstrument(e.Execution.Instrument)) return;
            NinjaTrader.Cbi.Execution ex = e.Execution;
            NinjaTrader.Cbi.Order o = ex.Order;
            LogFill f = new LogFill
            {
                Price = ex.Price, Qty = ex.Quantity, Time = ex.Time,
                Name = (o != null ? o.Name : ex.Name) ?? "",
                IsStop = o != null && (o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit),
                StopPrice = o != null ? o.StopPrice : 0,
                IsLong = ex.MarketPosition == MarketPosition.Long
            };
            TriggerCustomEvent(x => AddLogFill(f), null);
        }

        private void OnLogOrderUpdate(object sender, OrderEventArgs e)
        {
            if (e == null || e.Order == null || !IsOurInstrument(e.Order.Instrument)) return;
            if (e.OrderState != OrderState.Accepted && e.OrderState != OrderState.Working) return;
            string name = e.Order.Name ?? "";
            if (name == "" || name == "Entry") return;
            bool isStop = e.Order.OrderType == OrderType.StopMarket || e.Order.OrderType == OrderType.StopLimit;
            double price = isStop ? e.StopPrice : e.LimitPrice;
            if (price <= 0) return;
            TriggerCustomEvent(x => TrackOrderPrice(name, isStop, price), null);
        }

        private List<AtmState> LogCandidates()
        {
            List<AtmState> all = new List<AtmState>();
            lock(listLock) { all.AddRange(trackedAtms.Values); }
            foreach (AtmState st in pendingLogs) if (!all.Contains(st)) all.Add(st);
            return all;
        }

        private static int SumQty(List<LogFill> fills) { int q = 0; foreach (LogFill f in fills) q += f.Qty; return q; }

        private static double AvgPrice(List<LogFill> fills)
        {
            double pq = 0; int q = 0;
            foreach (LogFill f in fills) { pq += f.Price * f.Qty; q += f.Qty; }
            return q > 0 ? pq / q : double.NaN;
        }

        // Zuordnung ohne ATM-ID an der Ausfuehrung: zuerst Exit (aelteste ATM der Gegenrichtung mit offenen
        // Kontrakten), sonst Entry (juengste offene ATM gleicher Richtung).
        private void AddLogFill(LogFill f)
        {
            AtmState target = null;
            foreach (AtmState st in LogCandidates())
            {
                bool stLong = st.Direction == AtmDirection.Long;
                if (f.IsLong != stLong && SumQty(st.EntryFills) > SumQty(st.ExitFills)
                    && (target == null || st.CreatedAt < target.CreatedAt)) target = st;
            }
            if (target != null) target.ExitFills.Add(f);
            else
            {
                foreach (AtmState st in LogCandidates())
                {
                    bool stLong = st.Direction == AtmDirection.Long;
                    if (f.IsLong == stLong && !st.Closed && (target == null || st.CreatedAt > target.CreatedAt)) target = st;
                }
                if (target == null) return;
                target.EntryFills.Add(f);
            }
            TryFlushLogs(false);
        }

        private void TrackOrderPrice(string name, bool isStop, double price)
        {
            AtmState st = null;
            foreach (AtmState c in LogCandidates())
                if (!c.Closed && (st == null || c.CreatedAt > st.CreatedAt)) st = c;
            if (st == null) return;

            double old;
            if (!st.OrderPrices.TryGetValue(name, out old))
            {
                st.OrderPrices[name] = price;
                if (st.Log != null && name == Target1Name && double.IsNaN(st.Log.T1Price)) st.Log.T1Price = price;
                if (st.Log != null && name == RunnerTargetName && double.IsNaN(st.Log.T2Price)) st.Log.T2Price = price;
                return;
            }
            if (Math.Abs(price - old) < TickSize / 2) return;
            st.OrderPrices[name] = price;

            double expected;
            if (st.ExpectedPrices.TryGetValue(name, out expected) && Math.Abs(expected - price) < TickSize / 2)
            {
                st.ExpectedPrices.Remove(name);
                return;
            }
            double entry = SumQty(st.EntryFills) > 0 ? AvgPrice(st.EntryFills) : st.EntryPrice;
            string newText = isStop && entry > 0 && Math.Abs(price - entry) < TickSize ? "BE" : Px(price);
            st.ManualChanges.Add($"{name} {Px(old)}->{newText}");
        }

        private void TryFlushLogs(bool force)
        {
            for (int i = pendingLogs.Count - 1; i >= 0; i--)
            {
                AtmState st = pendingLogs[i];
                int inQty = SumQty(st.EntryFills), outQty = SumQty(st.ExitFills);
                bool complete = inQty > 0 && outQty >= inQty;
                if (!force && !complete && (DateTime.Now - st.ClosedSeen).TotalSeconds < LogSettleSeconds) continue;
                pendingLogs.RemoveAt(i);
                WriteTradeLog(st);
            }
        }

        private static readonly System.Globalization.CultureInfo LogCulture = new System.Globalization.CultureInfo("de-DE");

        // Preis wie im ChartTrader: zwei Nachkommastellen, Dezimalkomma; NaN = leer.
        private static string Px(double x) { return double.IsNaN(x) ? "" : x.ToString("0.00", LogCulture); }

        // Zahl ohne ueberfluessige Nullen (ChartTrader _num).
        private static string Num(double x)
        {
            if (double.IsNaN(x)) return "";
            string t = x.ToString("0.##########", LogCulture);
            return t == "-0" ? "0" : t;
        }

        private static string Hms(DateTime t) { return t == DateTime.MinValue ? "" : t.ToString("HH:mm:ss"); }

        // ES 06-25 -> ESM5 (wie die Instrument-Spalte des ChartTraders)
        private string LogSymbol()
        {
            if (Instrument == null) return "";
            try
            {
                if (Instrument.MasterInstrument.InstrumentType == InstrumentType.Future && Instrument.Expiry.Year > 2000)
                    return Instrument.MasterInstrument.Name + "FGHJKMNQUVXZ"[Instrument.Expiry.Month - 1] + (Instrument.Expiry.Year % 10);
            }
            catch (Exception) { }
            return Instrument.FullName;
        }

        private void WriteTradeLog(AtmState st)
        {
            if (string.IsNullOrEmpty(tradeLogPath)) return;
            try
            {
                TradeLogInfo info = st.Log ?? new TradeLogInfo();
                int sign = st.Direction == AtmDirection.Long ? 1 : -1;
                int qty = SumQty(st.EntryFills);
                double entry = qty > 0 ? AvgPrice(st.EntryFills) : st.EntryPrice;
                double exit = AvgPrice(st.ExitFills);
                DateTime opened = st.EntryFills.Count > 0 ? st.EntryFills[0].Time : st.OpenedAt;
                DateTime closed = st.ExitFills.Count > 0 ? st.ExitFills[st.ExitFills.Count - 1].Time : opened;
                double points = double.IsNaN(exit) ? double.NaN : Math.Round((exit - entry) * sign, 2);
                double pnl = double.NaN;
                if (!double.IsNaN(exit))
                {
                    pnl = 0;
                    foreach (LogFill f in st.ExitFills) pnl += (f.Price - entry) * f.Qty * sign;
                    pnl = Math.Round(pnl * Instrument.MasterInstrument.PointValue, 2);
                }
                TimeSpan dur = closed - opened;
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;

                List<LogFill> t1 = new List<LogFill>(), rest = new List<LogFill>();
                foreach (LogFill f in st.ExitFills) { if (f.Name == Target1Name && !f.IsStop) t1.Add(f); else rest.Add(f); }
                string reason = "";
                if (st.ExitFills.Count > 0)
                {
                    LogFill last = st.ExitFills[st.ExitFills.Count - 1];
                    if (last.IsStop) reason = Math.Abs(last.StopPrice - entry) < TickSize ? "BE" : "Stop";
                    else if (last.Name.StartsWith("Target")) reason = "Target";
                    else reason = "Manual";
                }

                string[] cols =
                {
                    logRunStarted,
                    opened == DateTime.MinValue ? "" : opened.ToString("yyyy-MM-dd"), Hms(opened), Hms(closed),
                    LogSymbol(), (info.Setup + " " + st.TemplateName).Trim(), sign > 0 ? "LONG" : "SHORT", qty.ToString(),
                    Num(entry), double.IsNaN(exit) ? "" : Num(Math.Round(exit, 2)), Num(points),
                    double.IsNaN(points) ? "" : Num(Math.Round(points / TickSize, 2)), Num(pnl),
                    $"{(int)dur.TotalHours}:{dur.Minutes:00}:{dur.Seconds:00}",
                    Hms(info.SignalStart), Px(info.SigO), Px(info.SigH), Px(info.SigL), Px(info.SigC), Px(info.PrevH), Px(info.PrevL),
                    Px(info.Trigger), info.EntryType, Px(info.Limit), Px(info.InitialSL),
                    double.IsNaN(info.InitialSL) ? "" : Num(Math.Round(Math.Abs(entry - info.InitialSL) / TickSize, 2)),
                    st.TemplateName, st.Log == null ? "" : info.MaxRisk.ToString(), Px(info.T1Price), Px(info.T2Price),
                    t1.Count > 0 ? Px(AvgPrice(t1)) : "", t1.Count > 0 ? Hms(t1[t1.Count - 1].Time) : "",
                    rest.Count > 0 ? Px(AvgPrice(rest)) : "", rest.Count > 0 ? Hms(rest[rest.Count - 1].Time) : "",
                    reason, string.Join(" | ", st.ManualChanges).Replace(";", ","), Px(info.DistEma),
                    st.Surprised ? "yes" : "no"
                };
                bool isNew = !System.IO.File.Exists(tradeLogPath);
                using (System.IO.StreamWriter w = new System.IO.StreamWriter(tradeLogPath, true, new System.Text.UTF8Encoding(isNew)))
                {
                    if (isNew) w.Write(TradeLogHeader + "\r\n");
                    w.Write(string.Join(";", cols) + "\r\n");
                }
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] JOURNAL {(sign > 0 ? "LONG" : "SHORT")} {qty} {Num(entry)} -> {(double.IsNaN(exit) ? "?" : Num(Math.Round(exit, 2)))}  PnL {Num(pnl)}  ({(info.Setup + " " + st.TemplateName).Trim()})");
            }
            catch (Exception ex)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] WARNING: trade log {tradeLogPath} not written ({ex.Message}).");
            }
        }

        #endregion

        #region 10K EMA

        // BarsInProgress der 10K-Serie. Historisch (ohne Tick Replay) ist [0] die gerade geschlossene Bar,
        // in Echtzeit (OnEachTick) meldet der erste Tick der neuen Bar den Schluss von [1].
        private void UpdateHtfEma()
        {
            bool historicalBarClose = State == State.Historical && !BarsArray[htfBip].IsTickReplay;
            int closedAgo = historicalBarClose ? 0 : 1;
            if ((historicalBarClose || IsFirstTickOfBar) && CurrentBars[htfBip] >= closedAgo)
            {
                double c = Closes[htfBip][closedAgo];
                htfEma = double.IsNaN(htfEma) ? c : htfEma + htfK * (c - htfEma);
                int idx = BarsArray[0].GetBar(Times[htfBip][closedAgo]);
                int n = htfPointBar.Count;
                if (n > 0 && idx <= htfPointBar[n - 1]) htfPointVal[n - 1] = htfEma;   // gleiche Chart-Bar: Wert ersetzen
                else { htfPointBar.Add(idx); htfPointVal.Add(htfEma); }
            }
            double last = Closes[htfBip][0];
            htfCurrent = historicalBarClose ? htfEma : (double.IsNaN(htfEma) ? last : htfEma + htfK * (last - htfEma));
        }

        private void SetHtfPlot(int barIndex, double value)
        {
            int ago = CurrentBar - barIndex;
            if (ago >= 0 && ago < 250) Values[0][ago] = value;
        }

        // Chart-Serie: neue Stuetzpunkte mit geraden Strecken verbinden, dann das vorlaeufige Stueck bis zur aktuellen Bar.
        private void DrawHtfEma()
        {
            while (htfDrawn + 1 < htfPointBar.Count && htfPointBar[htfDrawn + 1] <= CurrentBar)
            {
                int b = htfPointBar[htfDrawn + 1];
                double vb = htfPointVal[htfDrawn + 1];
                if (htfDrawn >= 0)
                {
                    int a = htfPointBar[htfDrawn];
                    double va = htfPointVal[htfDrawn];
                    for (int i = a + 1; i < b; i++) SetHtfPlot(i, va + (vb - va) * (i - a) / (double)(b - a));
                }
                SetHtfPlot(b, vb);
                htfDrawn++;
            }
            if (htfDrawn < 0 || double.IsNaN(htfCurrent)) return;
            int la = htfPointBar[htfDrawn];
            double lv = htfPointVal[htfDrawn];
            if (CurrentBar <= la) return;
            for (int i = la + 1; i <= CurrentBar; i++) SetHtfPlot(i, lv + (htfCurrent - lv) * (i - la) / (double)(CurrentBar - la));
        }

        #endregion

        // Trap (noch ohne ATM) endet ohne Position: Zeile im Trade-Log, dann "SURPRISED" leeren.
        private void TrapEndedWithoutTrade(bool isLong, string why)
        {
            if (isLong) LogNoTrade(why, trapLogLong, true, trapAtmTemplateLong);
            else LogNoTrade(why, trapLogShort, false, trapAtmTemplateShort);
            SetupEndedWithoutTrade(why);
        }

        // ATM-Entry endet ohne Fill (Runway, Bars to Wait, storniert, Create-Fehler): Zeile im Trade-Log, dann "SURPRISED" leeren.
        private void AtmEndedWithoutTrade(AtmState st, string why)
        {
            LogNoTrade(why, st.Log, st.Direction == AtmDirection.Long, st.TemplateName);
            SetupEndedWithoutTrade(why);
        }

        // Zeile fuer ein Setup ohne Position (wie record_no_trade im ChartTrader): Qty 0, kein Entry/Exit/PnL,
        // Open = Scharfmachen, Close = Ende, RiskTicks gegen den geplanten Einstieg, RunnerExitReason = "NoTrade (<Grund>)",
        // Surprised = Stand der Box beim Ende des Setups.
        private void LogNoTrade(string why, TradeLogInfo info, bool isLong, string template)
        {
            if (string.IsNullOrEmpty(tradeLogPath)) return;
            try
            {
                if (info == null) info = new TradeLogInfo();
                DateTime ended;
                lock(dataLock) { ended = cTime0; }
                DateTime armed = info.ArmedAt != DateTime.MinValue ? info.ArmedAt : ended;
                double planned = !double.IsNaN(info.Limit) ? info.Limit : info.Trigger;
                string setup = (info.Setup + " " + template).Trim();
                string side = isLong ? "LONG" : "SHORT";
                string[] cols =
                {
                    logRunStarted,
                    armed == DateTime.MinValue ? "" : armed.ToString("yyyy-MM-dd"), Hms(armed), Hms(ended),
                    LogSymbol(), setup, side, "0", "", "", "", "", "", "",
                    Hms(info.SignalStart), Px(info.SigO), Px(info.SigH), Px(info.SigL), Px(info.SigC), Px(info.PrevH), Px(info.PrevL),
                    Px(info.Trigger), info.EntryType, Px(info.Limit), Px(info.InitialSL),
                    double.IsNaN(info.InitialSL) || double.IsNaN(planned) ? "" : Num(Math.Round(Math.Abs(planned - info.InitialSL) / TickSize, 2)),
                    template, info.MaxRisk > 0 ? info.MaxRisk.ToString() : "", "", "",
                    "", "", "", "",
                    "NoTrade (" + why + ")", "", Px(info.DistEma),
                    surprisedArmed ? "yes" : "no"
                };
                bool isNew = !System.IO.File.Exists(tradeLogPath);
                using (System.IO.StreamWriter w = new System.IO.StreamWriter(tradeLogPath, true, new System.Text.UTF8Encoding(isNew)))
                {
                    if (isNew) w.Write(TradeLogHeader + "\r\n");
                    w.Write(string.Join(";", cols) + "\r\n");
                }
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] JOURNAL NO TRADE {side} ({setup}): {why}");
            }
            catch (Exception ex)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] WARNING: trade log {tradeLogPath} not written ({ex.Message}).");
            }
        }

        // Ein Setup endet ohne Position (Trap verfallen, Entry storniert, Cancel All ...): "SURPRISED" galt diesem
        // Setup und wird geleert.
        private void SetupEndedWithoutTrade(string why)
        {
            if (!surprisedArmed) return;
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Surprised cleared: setup ended without a trade ({why}).");
            ClearSurprisedBox();
        }

        // Die Position hat die Box beim Oeffnen uebernommen: Box leeren (UI-Thread).
        private void ClearSurprisedBox()
        {
            surprisedArmed = false;
            if (ChartControl != null && surprisedCheckBox != null)
                ChartControl.Dispatcher.InvokeAsync(() => { if (surprisedCheckBox.IsChecked == true) surprisedCheckBox.IsChecked = false; });
        }

        private void UpdateClimaxUi(string overrideText = null)
        {
            if (ChartControl == null) return;

            bool active = climaxActive;
            string text = overrideText;
            if (text == null && active)
                text = $"{(climaxIsBuy ? "BUY CLIMAX " : "SELL CLIMAX ")}{climaxExtreme}\n{(climaxIsBuy ? "Long" : "Short")} gesperrt: {ClimaxBarsSince()}/{TbtlMinBars} Bars, {Math.Min(climaxLegs, TbtlMinLegs)}/{TbtlMinLegs} Legs";
            bool ok = overrideText == "TBTL OK" || overrideText == "TREND RESUMED";

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (climaxLabel == null) return;
                climaxLabel.Text = text ?? "";
                climaxLabel.Background = ok ? Brushes.LimeGreen : Brushes.Orange;
                climaxLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        #endregion

        #region Revenge-Trading-Sperre

        // Laeuft auf dem Account-Thread. Hier nur die Daten abgreifen, alles Weitere
        // wandert ueber TriggerCustomEvent auf den Strategie-Thread.
        // Gewinner-Teile werden NICHT mehr verworfen: T1-Gewinn + Runner-Verlust
        // ergibt netto einen Gewinn und darf nicht als Verlust zaehlen.
        private void OnAccountAddTrade(object sender, AddTradeEventArgs e)
        {
            if (!lockoutActive || e == null || e.Trade == null) return;

            NinjaTrader.Cbi.Trade t = e.Trade;
            double pnl = t.ProfitCurrency;
            int qty = t.Quantity;
            NinjaTrader.Cbi.Execution entry = t.Entry;

            string key = null;
            int expected = 0;
            string instrument = "";
            if (entry != null)
            {
                key = !string.IsNullOrEmpty(entry.OrderId) ? entry.OrderId : entry.ExecutionId;
                if (entry.Order != null) expected = entry.Order.Filled;
                if (entry.Instrument != null) instrument = entry.Instrument.FullName;
            }

            TriggerCustomEvent(o => CollectTradePart(key, instrument, qty, expected, pnl), null);
        }

        private void CollectTradePart(string key, string instrument, int qty, int expected, double pnl)
        {
            RefreshTradingDay();

            // Ohne Entry-Zuordnung bleibt nur das alte Verhalten: Teil = Position.
            if (string.IsNullOrEmpty(key))
            {
                if (IsCountableLoss(pnl)) RegisterLoss(pnl, 1);
                return;
            }

            PositionResult r;
            if (!positionResults.TryGetValue(key, out r))
            {
                r = new PositionResult { Key = key, InstrumentName = instrument };
                positionResults[key] = r;
            }

            r.ClosedQty += qty;
            r.Pnl += pnl;
            r.Parts++;
            r.LastUpdate = DateTime.Now;
            if (expected > r.ExpectedQty) r.ExpectedQty = expected;

            if (r.Finalized)
            {
                // Nachzuegler nach dem Abschluss: nur neu bewerten, wenn die Position
                // bisher nicht als Verlust gezaehlt wurde - nie doppelt zaehlen.
                if (!r.CountedAsLoss) FinalizePosition(r, "late part");
                return;
            }

            if (r.ExpectedQty > 0 && r.ClosedQty >= r.ExpectedQty)
                FinalizePosition(r, "all parts closed");
            else
                SettlePositions(false);
        }

        // Schliesst offene Sammlungen ab, deren Instrument auf dem Konto flat ist.
        // force = true: ohne Wartezeit (vor einem neuen Entry).
        private void SettlePositions(bool force)
        {
            if (positionResults.Count == 0) return;

            DateTime now = DateTime.Now;
            foreach (PositionResult r in new List<PositionResult>(positionResults.Values))
            {
                if (r.Finalized) continue;
                if (!force && (now - r.LastUpdate).TotalSeconds < PositionSettleSeconds) continue;
                if (!IsAccountFlat(r.InstrumentName)) continue;
                FinalizePosition(r, force ? "flat before new entry" : "position flat");
            }
        }

        private void FinalizePosition(PositionResult r, string reason)
        {
            r.Finalized = true;
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Position closed on {AccountLabel()} ({reason}): {r.Parts} part(s), {r.ClosedQty} contract(s), net {r.Pnl:0.00}.");

            if (!IsCountableLoss(r.Pnl)) return;

            r.CountedAsLoss = true;
            RegisterLoss(r.Pnl, r.Parts);
        }

        private bool IsCountableLoss(double pnl)
        {
            if (pnl >= 0) return false;
            if (MinLossToCount > 0 && -pnl < MinLossToCount) return false;
            return true;
        }

        private bool IsAccountFlat(string instrumentName)
        {
            if (lockoutAccount == null || string.IsNullOrEmpty(instrumentName)) return true;
            try
            {
                lock (lockoutAccount.Positions)
                {
                    foreach (NinjaTrader.Cbi.Position p in lockoutAccount.Positions)
                    {
                        if (p == null || p.Instrument == null) continue;
                        if (p.Instrument.FullName != instrumentName) continue;
                        if (p.MarketPosition != MarketPosition.Flat && p.Quantity > 0) return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Position check failed ({ex.Message}), treating as flat.");
            }
            return true;
        }

        private void RegisterLoss(double pnl, int parts)
        {
            RefreshTradingDay();
            if (tradingLocked) return;

            lossesToday++;
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Loss registered on {AccountLabel()}: {pnl:0.00} from {parts} part(s) ({lossesToday}/{LossesUntilLockout}).");

            if (lossesToday >= LossesUntilLockout) ApplyLockout();
        }

        private void ApplyLockout()
        {
            tradingLocked = true;

            // Ein bereits scharfer Trap wuerde sonst nach der Sperre noch ausloesen.
            if (trapLongActive) TrapEndedWithoutTrade(true, "trading locked");
            if (trapShortActive) TrapEndedWithoutTrade(false, "trading locked");
            trapLongActive = false;
            trapShortActive = false;
            RemoveDrawObject("TrapTrigger");
            RemoveDrawObject("TrapLimit");

            PersistLockout();
            UpdateLockoutUi();

            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] TRADING LOCKED for {lockoutTradingDay:yyyy-MM-dd} on {AccountLabel()} after {lossesToday} loss(es). Scratch and Cancel stay active.");
        }

        // Handelstag = Session-Rollover des Instruments, damit die Sperre auf derselben
        // Grenze zurueckgesetzt wird, auf der NT die Tages-P&L zuruecksetzt.
        private void RefreshTradingDay(bool force = false)
        {
            // Faellt der Session-Kalender aus, ist der Kalendertag immer noch
            // besser als gar keine Sperre.
            // Globals.Now = Wanduhr live, Playback-Zeit im Playback. So gehoert ein
            // Backtest-Verlust zum simulierten Handelstag und nicht zum echten Datum.
            DateTime now = NinjaTrader.Core.Globals.Now;
            DateTime day = now.Date;
            if (lockoutSession != null)
            {
                try { day = lockoutSession.GetTradingDay(now).Date; }
                catch { day = now.Date; }
            }

            if (day == lockoutTradingDay && !force) return;

            lockoutTradingDay = day;
            lossesToday = 0;
            positionResults.Clear();
            tradingLocked = lockoutActive && IsLockoutPersisted(day);
            UpdateLockoutUi();

            if (tradingLocked)
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Lockout restored from disk: {AccountLabel()} stays blocked for {day:yyyy-MM-dd}.");
        }

        private bool IsTradingBlocked()
        {
            if (!lockoutActive) return false;

            RefreshTradingDay();
            // Eine eben geschlossene Position muss vor dem naechsten Entry gezaehlt sein.
            SettlePositions(true);
            if (!tradingLocked) return false;

            Draw.TextFixed(this, "SmartOrderError", $"BLOCKED: daily loss lockout active on {AccountLabel()}.\nNo new entries until the next trading day.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Entry blocked: daily loss lockout active on {AccountLabel()} for {lockoutTradingDay:yyyy-MM-dd}.");
            return true;
        }

        private static bool IsSimulationAccount(Account acc)
        {
            if (acc == null || string.IsNullOrEmpty(acc.Name)) return false;
            string n = acc.Name;
            return n.StartsWith("Playback", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Sim", StringComparison.OrdinalIgnoreCase);
        }

        private string AccountLabel()
        {
            return lockoutAccount != null ? lockoutAccount.Name : "<no account>";
        }

        private string LockoutKey(string accountName, DateTime day)
        {
            return accountName + "|" + day.ToString("yyyy-MM-dd");
        }

        private bool IsLockoutPersisted(DateTime day)
        {
            if (string.IsNullOrEmpty(lockoutFilePath) || lockoutAccount == null) return false;

            string key = LockoutKey(lockoutAccount.Name, day);
            try
            {
                if (!System.IO.File.Exists(lockoutFilePath)) return false;
                foreach (string line in System.IO.File.ReadAllLines(lockoutFilePath))
                    if (line.Trim() == key) return true;
            }
            catch (Exception ex)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Lockout file not readable ({ex.Message}).");
            }
            return false;
        }

        // Die Sperre muss einen Neustart der Strategie ueberleben - sonst ist sie
        // genau in dem Moment weg, in dem man sie braucht.
        private void PersistLockout()
        {
            if (string.IsNullOrEmpty(lockoutFilePath) || lockoutAccount == null) return;

            string key = LockoutKey(lockoutAccount.Name, lockoutTradingDay);
            try
            {
                List<string> keep = new List<string>();
                if (System.IO.File.Exists(lockoutFilePath))
                {
                    DateTime cutoff = DateTime.Today.AddDays(-7);
                    foreach (string line in System.IO.File.ReadAllLines(lockoutFilePath))
                    {
                        string entry = line.Trim();
                        if (entry.Length == 0 || entry == key) continue;

                        int sep = entry.LastIndexOf('|');
                        if (sep < 0) continue;

                        DateTime parsed;
                        if (!DateTime.TryParseExact(entry.Substring(sep + 1), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed)) continue;
                        if (parsed >= cutoff) keep.Add(entry);
                    }
                }
                keep.Add(key);
                System.IO.File.WriteAllLines(lockoutFilePath, keep.ToArray());
            }
            catch (Exception ex)
            {
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] WARNING: lockout could not be written to {lockoutFilePath} ({ex.Message}). It is active for this session only.");
            }
        }

        private void UpdateLockoutUi()
        {
            if (ChartControl == null) return;

            bool locked = tradingLocked;
            int losses = lossesToday;
            string account = AccountLabel();

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (buyButton != null)          buyButton.IsEnabled          = !locked;
                if (sellButton != null)         sellButton.IsEnabled         = !locked;
                if (noRunnerBuyButton != null)  noRunnerBuyButton.IsEnabled  = !locked;
                if (noRunnerSellButton != null) noRunnerSellButton.IsEnabled = !locked;
                if (deepBuyButton != null)      deepBuyButton.IsEnabled      = !locked;
                if (deepSellButton != null)     deepSellButton.IsEnabled     = !locked;
                if (momentumBuyButton != null)  momentumBuyButton.IsEnabled  = !locked;
                if (momentumSellButton != null) momentumSellButton.IsEnabled = !locked;

                if (lockoutLabel != null)
                {
                    lockoutLabel.Text = losses > 0
                        ? $"TRADING LOCKED - {losses} loss(es) on {account}"
                        : $"TRADING LOCKED on {account}";
                    lockoutLabel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }

        #endregion

        private void ExecuteCancel()
        {
            bool trapLong = trapLongActive, trapShort = trapShortActive;
            List<AtmState> unfilled = new List<AtmState>();
            trapLongActive = false;
            trapShortActive = false;
            
            RemoveDrawObject("TrapTrigger");
            RemoveDrawObject("TrapLimit");
            RemoveDrawObject("SmartOrderError");
            RemoveDrawObject("BE_Notice");
            RemoveDrawObject("SL_Notice");
            RemoveDrawObject("Scratch_Notice");
            RemoveDrawObject("PartialFill_Notice");
            RemoveDrawObject("Trail_Notice");

            lock(listLock)
            {
                foreach (var kvp in trackedAtms)
                {
                    AtmStrategyCancelEntryOrder(kvp.Value.EntryOrderId);
                    AtmStrategyClose(kvp.Key);
                    AtmState st = kvp.Value;
                    if (st.PositionSeen || st.EntryFills.Count > 0)
                    {
                        // Position wird geschlossen: Zeile im Trade-Log schreiben, sobald die Exits da sind.
                        if (!st.Closed) { st.Closed = true; st.ClosedSeen = DateTime.Now; pendingLogs.Add(st); }
                    }
                    else unfilled.Add(st);
                }
                trackedAtms.Clear();
            }
            if (trapLong) LogNoTrade("cancel all", trapLogLong, true, trapAtmTemplateLong);
            if (trapShort) LogNoTrade("cancel all", trapLogShort, false, trapAtmTemplateShort);
            foreach (AtmState u in unfilled) LogNoTrade("cancel all", u.Log, u.Direction == AtmDirection.Long, u.TemplateName);
            if (trapLong || trapShort || unfilled.Count > 0) SetupEndedWithoutTrade("cancel all");
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Killswitch activated. All traps and active orders destroyed.");
        }

        private void ExecuteScratch()
        {
            RemoveDrawObject("SmartOrderError");
            RemoveDrawObject("Scratch_Notice");

            double tSize;
            lock(dataLock) { tSize = cTickSize; }
            if (tSize <= 0) return;

            int livePositions = 0;
            int adjustedTargets = 0;
            int trackedCount = 0;

            lock(listLock)
            {
                trackedCount = trackedAtms.Count;
                foreach (var kvp in trackedAtms)
                {
                    AtmState state = kvp.Value;

                    if (state.EntryPrice == 0) continue;
                    if (GetAtmStrategyPositionQuantity(state.AtmId) == 0) continue;
                    livePositions++;

                    double scratchPrice = state.Direction == AtmDirection.Long
                        ? state.EntryPrice + (ScratchOffsetTicks * tSize)
                        : state.EntryPrice - (ScratchOffsetTicks * tSize);

                    // Wir ziehen nur nach innen: ein bereits engeres Ziel bleibt unangetastet.
                    if (state.ScratchTargetPrice != 0)
                    {
                        bool isCloser = state.Direction == AtmDirection.Long
                            ? scratchPrice < state.ScratchTargetPrice
                            : scratchPrice > state.ScratchTargetPrice;
                        if (!isCloser) continue;
                    }

                    if (PullTargetToScratch(Target1Name, state.AtmId, scratchPrice)) adjustedTargets++;
                    if (state.HasRunner && state.EntryComplete && PullTargetToScratch(RunnerTargetName, state.AtmId, scratchPrice)) adjustedTargets++;
                    state.ScratchTargetPrice = scratchPrice;
                }
            }

            if (livePositions == 0)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: No active position to scratch!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Scratch ignored: no filled ATM position ({trackedCount} tracked).");
                return;
            }

            if (adjustedTargets == 0)
            {
                Draw.TextFixed(this, "Scratch_Notice", $"SCRATCH: Targets already at or tighter than +{ScratchOffsetTicks} ticks.", TextPosition.TopLeft, Brushes.Gold, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Scratch skipped: {livePositions} position(s) already at or tighter than {ScratchOffsetTicks} ticks.");
                return;
            }

            Draw.TextFixed(this, "Scratch_Notice", $"SCRATCH: {adjustedTargets} target(s) pulled to {ScratchOffsetTicks} ticks in profit", TextPosition.TopLeft, Brushes.Gold, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Scratch executed on {livePositions} position(s): {adjustedTargets} target(s) pulled to {ScratchOffsetTicks} ticks beyond entry.");
        }

        private bool PullTargetToScratch(string targetName, string atmId, double price)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return false;

            string[,] status = GetAtmStrategyStopTargetOrderStatus(targetName, atmId);
            if (status == null || status.GetLength(0) == 0 || status.GetLength(1) <= 2) return false;

            string orderState = status[0, 2];
            if (orderState != "Accepted" && orderState != "Working") return false;

            // Achtung Reihenfolge: (limitPrice, stopPrice, ...). Targets sind Limit-Orders,
            // der Preis gehoert also in den ERSTEN Parameter (Stops in den zweiten).
            ChangeStopTargetLogged(price, 0, targetName, atmId);
            return true;
        }

        #region Signal-Bar-Filter

        // Gemeinsame Qualitaetspruefung der Signal-Bar (Bar[1]) fuer alle sechs
        // Einstiege. Sitzt in den Execute-Methoden selbst und greift deshalb bei
        // Button und Hotkey gleichermassen.
        private bool IsValidSignalBar(bool isLong, out string reason)
        {
            reason = "";
            if (EnableTradingNonPerfectBars) return true;

            double o, h, l, c, tSize;
            lock(dataLock) { o = cOpen1; h = cHigh1; l = cLow1; c = cClose1; tSize = cTickSize; }

            if (SignalBarCheck.Evaluate(isLong, o, h, l, c, tSize,
                    RequireClosePosition, ClosePositionPercent,
                    RequireMinBody, MinBodyRatioPercent,
                    RequireNoBreakoutWick, MaxBreakoutWickPercent,
                    AllowRejectionBar, RejectionMinClosePercent, RejectionMaxWickPercent,
                    out reason))
                return true;

            Draw.TextFixed(this, "SmartOrderError", reason.Replace(" | ", "\n"), TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] {(isLong ? "Long" : "Short")}: {reason} (O={o} H={h} L={l} C={c})");
            return false;
        }

        #endregion

        private void ExecuteMomentumLong()
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Long)) return;

            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            string signalReason;
            if (!IsValidSignalBar(true, out signalReason)) return;

            double entryPrice = h1 + tSize; 
            double limitPrice = entryPrice + (MaxSlippageTicks * tSize); 
            double structuralStop = l1 - tSize; 
            double riskTicks = (entryPrice - structuralStop) / tSize;
            
            double intendedStop = structuralStop;
            if (riskTicks > MaxRiskTicks)
            {
                intendedStop = entryPrice - (MaxRiskTicks * tSize);
            }

            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MomLong_" + atmId; 
            string selectedAtm = availableAtms[currentAtmIndex];
            if (IsAtmTemplateMissing(selectedAtm)) return;
            
            lock(listLock) 
            { 
                trackedAtms[atmId] = new AtmState 
                { 
                    AtmId = atmId, 
                    EntryOrderId = uniqueOrderId, 
                    Direction = AtmDirection.Long, 
                    EntryPrice = 0, 
                    BeTriggered = false, 
                    IntendedStopPrice = intendedStop, 
                    TriggerPrice = entryPrice, 
                    SetupBarIndex = bar, 
                    Stop1Adjusted = false, 
                    RunnerStopAdjusted = false, 
                    DisplayNotified = false,
                    ScratchTargetPrice = 0,
                    HasRunner = true,
                    TemplateName = selectedAtm,
                    CreatedAt = DateTime.Now
                }; 
            }
            
            TradeLogInfo momLog = MakeLogInfo("Momentum Long", entryPrice, double.NaN, intendedStop);
            lock(listLock) { trackedAtms[atmId].Log = momLog; }
            if(c0 >= entryPrice) AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, selectedAtm));
            else AtmStrategyCreate(OrderAction.Buy, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, selectedAtm));
        }

        private void ExecuteMomentumShort()
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Short)) return;

            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            string signalReason;
            if (!IsValidSignalBar(false, out signalReason)) return;

            double entryPrice = l1 - tSize;
            double limitPrice = entryPrice - (MaxSlippageTicks * tSize); 
            double structuralStop = h1 + tSize; 
            double riskTicks = (structuralStop - entryPrice) / tSize;
            
            double intendedStop = structuralStop;
            if (riskTicks > MaxRiskTicks)
            {
                intendedStop = entryPrice + (MaxRiskTicks * tSize);
            }

            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MomShort_" + atmId; 
            string selectedAtm = availableAtms[currentAtmIndex];
            if (IsAtmTemplateMissing(selectedAtm)) return;
            
            lock(listLock) 
            { 
                trackedAtms[atmId] = new AtmState 
                { 
                    AtmId = atmId, 
                    EntryOrderId = uniqueOrderId, 
                    Direction = AtmDirection.Short, 
                    EntryPrice = 0, 
                    BeTriggered = false, 
                    IntendedStopPrice = intendedStop, 
                    TriggerPrice = entryPrice, 
                    SetupBarIndex = bar, 
                    Stop1Adjusted = false, 
                    RunnerStopAdjusted = false, 
                    DisplayNotified = false,
                    ScratchTargetPrice = 0,
                    HasRunner = true,
                    TemplateName = selectedAtm,
                    CreatedAt = DateTime.Now
                }; 
            }
            
            TradeLogInfo momLog = MakeLogInfo("Momentum Short", entryPrice, double.NaN, intendedStop);
            lock(listLock) { trackedAtms[atmId].Log = momLog; }
            if(c0 <= entryPrice) AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, selectedAtm));
            else AtmStrategyCreate(OrderAction.Sell, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, selectedAtm));
        }

        private void ExecuteSmartLong(bool noRunner = false)
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Long)) return;

            // Ohne Suffix wuerde der No-Runner-Button das normale Template feuern,
            // die Runner-Verwaltung aber trotzdem abschalten.
            if (noRunner && string.IsNullOrWhiteSpace(NoRunnerSuffix))
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: No-Runner Template Suffix is empty!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double structuralStop = Math.Min(l1, l2) - tSize;
            double triggerPrice = h1 + tSize;
            double structuralRiskTicks = Math.Round((triggerPrice - structuralStop) / tSize);
            double totalStructureSize = Math.Round((h1 - Math.Min(l1, l2)) / tSize); 


            string signalReason;
            if (!IsValidSignalBar(true, out signalReason)) return;
            
            trapTriggerPrice = triggerPrice;
            trapStopLossPrice = structuralStop; 
            
            // Wir prüfen gegen das eingestellte Risiko PLUS unsere neue Toleranz
            if (structuralRiskTicks > (MaxRiskTicks + RiskToleranceTicks))
            {
                double requiredPullbackTicks = structuralRiskTicks - MaxRiskTicks;
                double maxPullbackTicks = Math.Floor(totalStructureSize * (MaxPullbackPercent / 100.0));

                if (requiredPullbackTicks > maxPullbackTicks)
                {
                    Draw.TextFixed(this, "SmartOrderError", $"ERROR: Double bar structure too large!\nRequired risk pullback ({requiredPullbackTicks} ticks) exceeds max allowed limit ({maxPullbackTicks} ticks).", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                    return;
                }

                trapLimitPrice = structuralStop + (MaxRiskTicks * tSize); 
            }
            else 
            {
                trapLimitPrice = triggerPrice; 
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Long: Risk OK. Direct trigger entry.");
            }
            string templateLong = noRunner ? availableAtms[currentAtmIndex] + NoRunnerSuffix : availableAtms[currentAtmIndex];
            if (IsAtmTemplateMissing(templateLong)) return;

            trapBarNumber = bar;
            trapNoRunnerLong = noRunner;
            trapAtmTemplateLong = templateLong;
            trapLogLong = MakeLogInfo(noRunner ? "Smart Long (No Runner)" : "Smart Long", trapTriggerPrice, trapLimitPrice, trapStopLossPrice);
            trapLongActive = true;
            trapShortActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Long Trap Armed. Limit: {trapLimitPrice}, SL: {trapStopLossPrice}, ATM: {trapAtmTemplateLong}");
        }

        private void ExecuteSmartShort(bool noRunner = false)
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Short)) return;

            // Ohne Suffix wuerde der No-Runner-Button das normale Template feuern,
            // die Runner-Verwaltung aber trotzdem abschalten.
            if (noRunner && string.IsNullOrWhiteSpace(NoRunnerSuffix))
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: No-Runner Template Suffix is empty!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double structuralStop = Math.Max(h1, h2) + tSize;
            double triggerPrice = l1 - tSize;
            double structuralRiskTicks = Math.Round((structuralStop - triggerPrice) / tSize);
            double totalStructureSize = Math.Round((Math.Max(h1, h2) - l1) / tSize); 


            string signalReason;
            if (!IsValidSignalBar(false, out signalReason)) return;
            
            trapTriggerPrice = triggerPrice;
            trapStopLossPrice = structuralStop; 
            
            // Wir prüfen gegen das eingestellte Risiko PLUS unsere neue Toleranz
            if (structuralRiskTicks > (MaxRiskTicks + RiskToleranceTicks))
            {
                double requiredPullbackTicks = structuralRiskTicks - MaxRiskTicks;
                double maxPullbackTicks = Math.Floor(totalStructureSize * (MaxPullbackPercent / 100.0));

                if (requiredPullbackTicks > maxPullbackTicks)
                {
                    Draw.TextFixed(this, "SmartOrderError", $"ERROR: Double bar structure too large!\nRequired risk pullback ({requiredPullbackTicks} ticks) exceeds max allowed limit ({maxPullbackTicks} ticks).", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                    return;
                }

                trapLimitPrice = structuralStop - (MaxRiskTicks * tSize); 
            }
            else 
            {
                trapLimitPrice = triggerPrice; 
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Short: Risk OK. Direct trigger entry.");
            }

            string templateShort = noRunner ? availableAtms[currentAtmIndex] + NoRunnerSuffix : availableAtms[currentAtmIndex];
            if (IsAtmTemplateMissing(templateShort)) return;

            trapBarNumber = bar;
            trapNoRunnerShort = noRunner;
            trapAtmTemplateShort = templateShort;
            trapLogShort = MakeLogInfo(noRunner ? "Smart Short (No Runner)" : "Smart Short", trapTriggerPrice, trapLimitPrice, trapStopLossPrice);
            trapShortActive = true;
            trapLongActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Short Trap Armed. Limit: {trapLimitPrice}, SL: {trapStopLossPrice}, ATM: {trapAtmTemplateShort}");
        }

        private void ExecuteDeepSmartLong()
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Long)) return;

            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            string signalReason;
            if (!IsValidSignalBar(true, out signalReason)) return;

            // Gleiche Stop-Basis wie Smart: liegt Bar 2 tiefer, zaehlt deren Tief.
            double structuralStop = Math.Min(l1, l2) - tSize;
            double triggerPrice = h1 + tSize;
            double candleSize = Math.Round((h1 - l1) / tSize);

            double maxPullbackTicks = Math.Floor(candleSize * (MaxPullbackPercent / 100.0));
            double limitPrice = h1 - (maxPullbackTicks * tSize); 
            double deepRiskTicks = Math.Round((limitPrice - structuralStop) / tSize);

            trapTriggerPrice = triggerPrice;

            if (deepRiskTicks > MaxRiskTicks)
            {
                trapLimitPrice = structuralStop + (MaxRiskTicks * tSize);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Deep Long pulled deeper to meet strict Max Risk requirements.");
            }
            else
            {
                trapLimitPrice = limitPrice;
            }

            trapStopLossPrice = structuralStop;
            if (IsAtmTemplateMissing(availableAtms[currentAtmIndex])) return;

            trapBarNumber = bar;
            trapNoRunnerLong = false;
            trapAtmTemplateLong = availableAtms[currentAtmIndex];
            trapLogLong = MakeLogInfo("Deep Long", trapTriggerPrice, trapLimitPrice, trapStopLossPrice);
            trapLongActive = true;
            trapShortActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DarkCyan, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
        }

        private void ExecuteDeepSmartShort()
        {
            if (IsTradingBlocked()) return;
            if (IsClimaxBlocked(AtmDirection.Short)) return;

            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            string signalReason;
            if (!IsValidSignalBar(false, out signalReason)) return;

            // Gleiche Stop-Basis wie Smart: liegt Bar 2 hoeher, zaehlt deren Hoch.
            double structuralStop = Math.Max(h1, h2) + tSize;
            double triggerPrice = l1 - tSize;
            double candleSize = Math.Round((h1 - l1) / tSize);

            double maxPullbackTicks = Math.Floor(candleSize * (MaxPullbackPercent / 100.0));
            double limitPrice = l1 + (maxPullbackTicks * tSize); 
            double deepRiskTicks = Math.Round((structuralStop - limitPrice) / tSize);

            trapTriggerPrice = triggerPrice;

            if (deepRiskTicks > MaxRiskTicks)
            {
                trapLimitPrice = structuralStop - (MaxRiskTicks * tSize);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Deep Short pulled deeper to meet strict Max Risk requirements.");
            }
            else
            {
                trapLimitPrice = limitPrice;
            }

            trapStopLossPrice = structuralStop;
            if (IsAtmTemplateMissing(availableAtms[currentAtmIndex])) return;

            trapBarNumber = bar;
            trapNoRunnerShort = false;
            trapAtmTemplateShort = availableAtms[currentAtmIndex];
            trapLogShort = MakeLogInfo("Deep Short", trapTriggerPrice, trapLimitPrice, trapStopLossPrice);
            trapLongActive = false;
            trapShortActive = true;
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DarkCyan, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
        }

        private void ManageRunnerTrail(AtmState state)
        {
            if (CurrentBar < (2 * SwingStrength) + 2) return;

            double tSize = TickSize;
            int lookback = Math.Min(CurrentBar - SwingStrength - 1, 100);

            // Scan from the most recent confirmed pivot backwards; stop at the setup bar.
            for (int p = SwingStrength + 1; p <= lookback; p++)
            {
                int pivotAbsoluteBar = CurrentBar - p;
                if (pivotAbsoluteBar <= state.SetupBarIndex) break; // only swings formed after entry

                if (state.Direction == AtmDirection.Long)
                {
                    double pivotLow = Low[p];
                    bool isPivot = true;
                    for (int k = 1; k <= SwingStrength; k++)
                    {
                        // newer side must be strictly higher; older side may be equal (double bottom)
                        if (Low[p - k] <= pivotLow || Low[p + k] < pivotLow) { isPivot = false; break; }
                    }
                    if (!isPivot) continue;

                    // Prominence: how far price has rallied off this low since the pivot formed
                    double maxHighSince = High[0];
                    for (int j = 0; j < p; j++) if (High[j] > maxHighSince) maxHighSince = High[j];
                    if ((maxHighSince - pivotLow) / tSize < MinSwingSizeTicks) continue; // micro-swing: ignore

                    double candidate = pivotLow - (TrailBufferTicks * tSize);
                    if (candidate <= state.CurrentRunnerStop) return; // tighten-only
                    if (candidate >= Close[0]) return;                // never place at/through market

                    TryMoveRunnerStop(state, candidate);
                    return;
                }
                else
                {
                    double pivotHigh = High[p];
                    bool isPivot = true;
                    for (int k = 1; k <= SwingStrength; k++)
                    {
                        if (High[p - k] >= pivotHigh || High[p + k] > pivotHigh) { isPivot = false; break; }
                    }
                    if (!isPivot) continue;

                    double minLowSince = Low[0];
                    for (int j = 0; j < p; j++) if (Low[j] < minLowSince) minLowSince = Low[j];
                    if ((pivotHigh - minLowSince) / tSize < MinSwingSizeTicks) continue;

                    double candidate = pivotHigh + (TrailBufferTicks * tSize);
                    if (state.CurrentRunnerStop != 0 && candidate >= state.CurrentRunnerStop) return; // tighten-only
                    if (candidate <= Close[0]) return;

                    TryMoveRunnerStop(state, candidate);
                    return;
                }
            }
        }

        private void TryMoveRunnerStop(AtmState state, double price)
        {
            string[,] status = GetAtmStrategyStopTargetOrderStatus(RunnerStopName, state.AtmId);
            if (status == null || status.GetLength(0) == 0 || status.GetLength(1) <= 2) return;

            string orderState = status[0, 2];
            if (orderState != "Accepted" && orderState != "Working") return;

            ChangeStopTargetLogged(0, price, RunnerStopName, state.AtmId);
            state.CurrentRunnerStop = price;
            RemoveDrawObject("BE_Notice");
            Draw.TextFixed(this, "Trail_Notice", $"TRAIL: {RunnerStopName} secured at {price}", TextPosition.BottomRight, Brushes.DeepSkyBlue, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Runner trail: {RunnerStopName} moved to {price} (swing-based).");
        }

        protected override void OnBarUpdate()
        {
            // Die 10K-Serie liefert nur die EMA; alle Order- und ATM-Logik laeuft ausschliesslich auf der Chart-Serie.
            if (BarsInProgress != 0)
            {
                if (BarsInProgress == htfBip) UpdateHtfEma();
                return;
            }
            if (htfBip > 0) DrawHtfEma();

            if (CurrentBar < 2) return; 

            bool isNewBar = IsFirstTickOfBar;
            if (isNewBar && lockoutActive && State == State.Realtime) { RefreshTradingDay(); SettlePositions(false); }
            if (isNewBar && climaxActive) UpdateClimax();

            lock(dataLock)
            {
                cHigh2 = High[2];
                cLow2 = Low[2];
                cHigh1 = High[1];
                cLow1 = Low[1];
                cClose1 = Close[1];
                cOpen1 = Open[1];
                cClose0 = Close[0];
                cSignalStart = Time[2];                    // Start der Signal-Bar = Ende der Vorbar
                cTime0 = Time[0];
                cEma1 = EMA(Close, JournalEmaPeriod)[1];
                cBar = CurrentBar;
                cTickSize = TickSize;
            }

            if (pendingLogs.Count > 0) TryFlushLogs(false);

            if (trapLongActive && CurrentBar > trapBarNumber) { trapLongActive = false; RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); TrapEndedWithoutTrade(true, "trap expired"); }
            if (trapShortActive && CurrentBar > trapBarNumber) { trapShortActive = false; RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); TrapEndedWithoutTrade(false, "trap expired"); }

            if (trapLongActive && !tradingLocked && High[0] >= trapTriggerPrice)
            {
                trapLongActive = false; 
                RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); 
                string atmId = GetAtmStrategyUniqueId();
                string uniqueOrderId = "TrapLong_" + atmId;
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Long, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, TriggerPrice = trapTriggerPrice, SetupBarIndex = cBar, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false, ScratchTargetPrice = 0, HasRunner = !trapNoRunnerLong, TemplateName = trapAtmTemplateLong, CreatedAt = DateTime.Now, Log = trapLogLong }; }
                AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, trapAtmTemplateLong, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, trapAtmTemplateLong));
            }

            if (trapShortActive && !tradingLocked && Low[0] <= trapTriggerPrice)
            {
                trapShortActive = false; 
                RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); 
                string atmId = GetAtmStrategyUniqueId();
                string uniqueOrderId = "TrapShort_" + atmId;
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Short, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, TriggerPrice = trapTriggerPrice, SetupBarIndex = cBar, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false, ScratchTargetPrice = 0, HasRunner = !trapNoRunnerShort, TemplateName = trapAtmTemplateShort, CreatedAt = DateTime.Now, Log = trapLogShort }; }
                AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, trapAtmTemplateShort, atmId, (errorCode, errorMessage) => OnAtmCreateResult(errorCode, errorMessage, atmId, trapAtmTemplateShort));
            }

            lock(listLock)
            {
                if (trackedAtms.Count > 0)
                {
                    List<string> deadAtms = new List<string>();
                    foreach (var kvp in trackedAtms)
                    {
                        AtmState state = kvp.Value;

                        // Eine Abfrage pro Tick genuegt - vorher liefen zwei davon nebeneinander
                        // und haben bei einer nicht existierenden ATM das Log doppelt geflutet.
                        string[] entryStatus = state.EntryComplete ? null : GetAtmStrategyEntryOrderStatus(state.EntryOrderId);
                        bool entryStatusValid = entryStatus != null && entryStatus.Length > 2;
                        if (entryStatusValid) state.EntryStatusSeen = true;

                        // Meldet eine ATM innerhalb des Anlauffensters nie einen Status, wurde sie
                        // nie erzeugt - in aller Regel ein fehlendes ATM-Template. Ohne diese
                        // Pruefung pollen wir die Phantom-ATM bis zum Bar-Timeout weiter.
                        if (!state.EntryComplete && !entryStatusValid && !state.EntryStatusSeen
                            && state.EntryPrice == 0 && !state.PositionSeen
                            && (DateTime.Now - state.CreatedAt).TotalSeconds > AtmStartupGraceSeconds)
                        {
                            deadAtms.Add(state.AtmId);
                            Draw.TextFixed(this, "SmartOrderError", $"ATM NEVER STARTED (template '{state.TemplateName}')\nNo entry status within {AtmStartupGraceSeconds}s - does this template exist?", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] ATM '{state.AtmId}' never reported an entry status within {AtmStartupGraceSeconds}s (template '{state.TemplateName}') - dropping it. Most likely that ATM template does not exist.");
                            continue;
                        }

                        if (state.EntryPrice == 0)
                        {
                            if (state.TriggerPrice > 0)
                            {
                                if (state.Direction == AtmDirection.Long && High[0] >= state.TriggerPrice + (MaxRunwayTicks * cTickSize))
                                {
                                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Long EXPIRED: Price ran {MaxRunwayTicks} Ticks past trigger. Cancelling Limit Order.");
                                    AtmStrategyCancelEntryOrder(state.EntryOrderId);
                                    AtmStrategyClose(state.AtmId);
                                    deadAtms.Add(state.AtmId);
                                    continue;
                                }
                                else if (state.Direction == AtmDirection.Short && Low[0] <= state.TriggerPrice - (MaxRunwayTicks * cTickSize))
                                {
                                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Short EXPIRED: Price ran {MaxRunwayTicks} Ticks past trigger. Cancelling Limit Order.");
                                    AtmStrategyCancelEntryOrder(state.EntryOrderId);
                                    AtmStrategyClose(state.AtmId);
                                    deadAtms.Add(state.AtmId);
                                    continue;
                                }
                            }

                            // --- BAR EXPIRATION CHECK ---
                            int maxBarsToWait = BarsToWait;
                            if (cBar >= state.SetupBarIndex + maxBarsToWait)
                            {
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Order EXPIRED: Allowed bars exceeded (Current: {cBar}, Setup: {state.SetupBarIndex}, Allowed: {maxBarsToWait}). Cancelling Live Order.");
                                AtmStrategyCancelEntryOrder(state.EntryOrderId);
                                AtmStrategyClose(state.AtmId);
                                deadAtms.Add(state.AtmId);
                                continue;
                            }
                            // --- END BAR EXPIRATION CHECK ---

                            if (entryStatusValid)
                            {
                                string status = entryStatus[2]; 
                                if (status == "Filled" || status == "PartFilled")
                                {
                                    double parsedPrice;
                                    if (double.TryParse(entryStatus[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsedPrice)) 
                                    {
                                        state.EntryPrice = parsedPrice;
                                        // Der Lauf fuer den Teilfill-Cancel zaehlt erst AB dem Fill. High/Low des
                                        // laufenden Bars enthalten sonst die Bewegung VOR dem Fill (Bug 17.12.2025:
                                        // Restmenge wurde eine Sekunde nach dem Fill storniert).
                                        state.PostFillExtreme = parsedPrice;
                                    }
                                }
                            }
                        }

                        // Sobald die Position einmal da war und jetzt weg ist, ist die ATM terminiert.
                        // Ab hier keine Statusabfragen mehr, sonst loggt NT "ATM strategy ID does not exist".
                        int atmQty = state.EntryPrice != 0 ? GetAtmStrategyPositionQuantity(state.AtmId) : 0;
                        if (atmQty > 0 && !state.PositionSeen)
                        {
                            // Position oeffnet: "SURPRISED" zaehlt nur, wenn die Box schon vorher angehakt war.
                            state.OpenedAt = Time[0];
                            state.Surprised = surprisedArmed;
                            if (surprisedArmed)
                            {
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Trade marked as surprised ({state.TemplateName}, {state.Direction}).");
                                ClearSurprisedBox();
                            }
                        }
                        if (atmQty > 0) state.PositionSeen = true;
                        if (state.PositionSeen && atmQty == 0)
                        {
                            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Trade closed ({state.TemplateName}, {state.Direction}), surprised: {(state.Surprised ? "yes" : "no")}.");
                            if (!state.Closed) { state.Closed = true; state.ClosedSeen = DateTime.Now; pendingLogs.Add(state); }
                            // Position ist zu (Stop/Target), aber eine Restmenge der Entry-Order kann
                            // noch im Markt haengen -> sonst wirft sie uns spaeter erneut in eine Position.
                            if (!state.EntryComplete && !state.EntryCancelRequested)
                            {
                                state.EntryCancelRequested = true;
                                AtmStrategyCancelEntryOrder(state.EntryOrderId);
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Position flat, remaining entry quantity cancelled ({state.EntryOrderId}).");
                            }
                            deadAtms.Add(state.AtmId);
                            continue;
                        }

                        // Teilfill: die ATM verteilt Brackets je Ausfuehrung. Fehlt nach dem Cancel der
                        // Restmenge das Runner-Bracket (Stop2), behandeln wir die Position als No-Runner,
                        // sonst schreibt jeder Tick "Order name 'Stop2' is invalid" ins Log.
                        if (state.EntryPartial && state.HasRunner && !state.RunnerProbed)
                        {
                            state.RunnerProbed = true;
                            string[,] probe = GetAtmStrategyStopTargetOrderStatus(RunnerStopName, state.AtmId);
                            if (probe == null || probe.GetLength(0) == 0)
                            {
                                state.HasRunner = false;
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Partial fill: no runner bracket ({RunnerStopName}) on this position - managing it as no-runner (Target1/Stop1 only).");
                            }
                        }

                        // Der Runner-Bracket entsteht erst mit der letzten Entry-Execution.
                        // Solange die Entry-Order noch Fills liefern kann, fragen wir Stop2/Target2
                        // gar nicht erst ab - sonst loggt NT "Order name 'Stop2' is invalid".
                        if (!state.EntryComplete && entryStatusValid)
                        {
                            string eState = entryStatus[2];
                            if (eState == "Filled" || eState == "Cancelled" || eState == "Rejected") state.EntryComplete = true;
                            if (eState == "Cancelled" && state.EntryPrice != 0) state.EntryPartial = true;
                        }

                        // Teilfill: die Restmenge haengt sonst weiter im Markt und kann uns nach TP1
                        // erneut in eine Position werfen. Ist der Preis PartialFillCancelTicks in
                        // Profitrichtung weggelaufen, fuellt sie ohnehin nicht mehr sauber -> stornieren.
                        if (!state.EntryComplete && !state.EntryCancelRequested && state.EntryPrice != 0)
                        {
                            // Nur der Weg seit dem Fill zaehlt: Close[0] ist bei Calculate=OnPriceChange der letzte Kurs.
                            if (state.PostFillExtreme == 0) state.PostFillExtreme = state.EntryPrice;
                            state.PostFillExtreme = state.Direction == AtmDirection.Long
                                ? Math.Max(state.PostFillExtreme, Close[0])
                                : Math.Min(state.PostFillExtreme, Close[0]);
                            double runTicks = state.Direction == AtmDirection.Long
                                ? (state.PostFillExtreme - state.EntryPrice) / cTickSize
                                : (state.EntryPrice - state.PostFillExtreme) / cTickSize;

                            if (runTicks >= PartialFillCancelTicks)
                            {
                                state.EntryCancelRequested = true;
                                AtmStrategyCancelEntryOrder(state.EntryOrderId);
                                Draw.TextFixed(this, "PartialFill_Notice", $"PARTIAL FILL: rest of entry cancelled ({PartialFillCancelTicks} ticks run)", TextPosition.TopRight, Brushes.Orange, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Partial fill: cancelling remaining entry quantity, price ran {Math.Floor(runTicks)} ticks past entry {state.EntryPrice}.");
                            }
                        }

                        if (state.EntryPrice != 0 && state.PositionSeen && state.IntendedStopPrice > 0)
                        {
                            if (!state.Stop1Adjusted)
                            {
                                string[,] s1Status = GetAtmStrategyStopTargetOrderStatus(Stop1Name, state.AtmId);
                                if (s1Status != null && s1Status.GetLength(0) > 0 && s1Status.GetLength(1) > 2)
                                {
                                    string status = s1Status[0, 2];
                                    if (status == "Accepted" || status == "Working")
                                    {
                                        ChangeStopTargetLogged(0, state.IntendedStopPrice, Stop1Name, state.AtmId);
                                        state.Stop1Adjusted = true;
                                    }
                                }
                            }

                            // Ohne Runner existiert kein Stop2 - jede Abfrage wuerde nur
                            // "Order name 'Stop2' is invalid" ins Log schreiben.
                            if (state.HasRunner && state.EntryComplete && !state.RunnerStopAdjusted)
                            {
                                string[,] s2Status = GetAtmStrategyStopTargetOrderStatus(RunnerStopName, state.AtmId);
                                if (s2Status != null && s2Status.GetLength(0) > 0 && s2Status.GetLength(1) > 2)
                                {
                                    string status = s2Status[0, 2];
                                    if (status == "Accepted" || status == "Working")
                                    {
                                        ChangeStopTargetLogged(0, state.IntendedStopPrice, RunnerStopName, state.AtmId);
                                        state.RunnerStopAdjusted = true;
                                        state.CurrentRunnerStop = state.IntendedStopPrice;
                                    }
                                }
                            }

                            if (state.Stop1Adjusted && !state.DisplayNotified)
                            {
                                state.DisplayNotified = true;
                                Draw.TextFixed(this, "SL_Notice", $"STRUCTURAL SL: Secured at {state.IntendedStopPrice}", TextPosition.BottomLeft, Brushes.Orange, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                            }
                        }

                        // Ohne Runner schliesst Target1 die komplette Position, ein BE-Stop
                        // waere also gegenstandslos.
                        if (EnableAutoBreakeven && state.HasRunner && state.EntryPrice != 0 && state.PositionSeen && !state.BeTriggered)
                        {
                            string[,] t1Status = GetAtmStrategyStopTargetOrderStatus(Target1Name, state.AtmId);
                            if (t1Status != null && t1Status.GetLength(0) > 0 && t1Status.GetLength(1) > 2)
                            {
                                if (t1Status[0, 2] == "Filled") 
                                {
                                    double bePrice = state.EntryPrice;
                                    if (AutoBreakevenOffset != 0)
                                    {
                                        bePrice = state.Direction == AtmDirection.Long 
                                            ? state.EntryPrice + (AutoBreakevenOffset * cTickSize) 
                                            : state.EntryPrice - (AutoBreakevenOffset * cTickSize);
                                    }

                                    ChangeStopTargetLogged(0, bePrice, RunnerStopName, state.AtmId);
                                    state.BeTriggered = true;
                                    state.CurrentRunnerStop = bePrice;
                                    
                                    RemoveDrawObject("SL_Notice");
                                    Draw.TextFixed(this, "BE_Notice", $"AUTO-BE: {RunnerStopName} secured at entry!", TextPosition.BottomRight, Brushes.LimeGreen, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                                }
                            }
                        }

                        // Der Trail startet, sobald Target1 gefuellt ist - UNABHAENGIG vom
                        // Auto-Breakeven. Vorher hing er an state.BeTriggered, das nur der
                        // BE-Block setzt: Mit EnableAutoBreakeven = false lief der Trail nie
                        // und der Runner blieb bis zum Schluss auf dem Initial-Stop.
                        if (EnableRunnerTrail && state.HasRunner && state.EntryPrice != 0 && state.PositionSeen && !state.T1Filled)
                        {
                            string[,] t1Trail = GetAtmStrategyStopTargetOrderStatus(Target1Name, state.AtmId);
                            if (t1Trail != null && t1Trail.GetLength(0) > 0 && t1Trail.GetLength(1) > 2
                                && t1Trail[0, 2] == "Filled")
                            {
                                state.T1Filled = true;
                            }
                        }

                        if (EnableRunnerTrail && (state.BeTriggered || state.T1Filled) && isNewBar)
                        {
                            ManageRunnerTrail(state);
                        }

                        // Fallback fuer den Fall, dass Fill und Close auf denselben Tick fallen.
                        // LIVE-BUG 24.09.2026: Beim Broker meldet die Entry-Order "Filled", bevor die
                        // ATM-Position aktualisiert ist (Menge noch 0). Ohne Wartezeit wurde die ATM im
                        // selben Durchlauf verworfen -> kein Struktur-Stop, kein Scratch, kein BE/Trail.
                        // Deshalb erst nach PositionConfirmGraceSeconds ohne Position aufgeben.
                        if (state.EntryPrice != 0 && atmQty == 0 && !state.PositionSeen)
                        {
                            if (state.FlatSince == DateTime.MinValue) state.FlatSince = DateTime.Now;
                            else if ((DateTime.Now - state.FlatSince).TotalSeconds > PositionConfirmGraceSeconds)
                            {
                                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] ATM '{state.AtmId}': entry filled but no position for {PositionConfirmGraceSeconds}s - treating as closed.");
                                deadAtms.Add(state.AtmId);
                            }
                        }
                    }
                    
                    foreach(string id in deadAtms)
                    {
                        AtmState gone;
                        if (trackedAtms.TryGetValue(id, out gone) && !gone.PositionSeen && gone.EntryPrice == 0 && gone.EntryFills.Count == 0)
                            AtmEndedWithoutTrade(gone, "entry not filled");
                        trackedAtms.Remove(id);
                        RemoveDrawObject("BE_Notice");
                        RemoveDrawObject("Trail_Notice");
                        RemoveDrawObject("SL_Notice");
                        RemoveDrawObject("Scratch_Notice");
                        RemoveDrawObject("PartialFill_Notice");
                    }
                }
            }
        }

        private void DisposeWPFControls()
        {
            if (ChartPanel != null) ChartPanel.PreviewKeyDown -= ChartPanel_PreviewKeyDown;
            if (myGrid != null) UserControlCollection.Remove(myGrid);
        }
    }
}