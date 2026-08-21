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
    }

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
        private Button scratchButton;
        private Button cancelButton;
        private Grid myGrid;
        
        // ATM State Machine
        private string[] availableAtms;
        private int currentAtmIndex = 0;
        private string trapAtmTemplateLong = "";
        private string trapAtmTemplateShort = "";

        // Trap State Variables
        private bool trapLongActive = false;
        private bool trapShortActive = false;
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
        [Display(Name="Min Body Ratio (%)", Description="Minimum required percentage of the candle body relative to the total candle size to filter out indecisive Dojis.", Order=7, GroupName="1. Parameters")]
        public int MinBodyRatioPercent { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name="Max Runway (Ticks)", Description="Cancel the unfilled limit order if price moves this many ticks past the trigger without filling.", Order=8, GroupName="1. Parameters")]
        public int MaxRunwayTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Bars to Wait", Description="How many bars to keep a pending limit order alive before automatic cancellation.", Order=9, GroupName="1. Parameters")]
        public int BarsToWait { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Show only Smart buttons", Description="If true, only smart long/short entry buttons are showed.", Order=10, GroupName="1. Parameters")]
        public bool ShowOnlySmartButtons { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name="Enable non perfect signal bars", Description="If enabled, you can enter long on a red bar and vice versa and a doji as well", Order=11, GroupName="1. Parameters")]
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
                MaxPullbackPercent          = 49.0; 
                BlockInsideBars             = true; 
                MaxSlippageTicks            = 2; 
                DefaultMaxRiskTicks         = 9; 
                SmartOffsetTicks            = 2;
                MinBodyRatioPercent         = 10; 
                MaxRunwayTicks              = 10;              
                EnableAutoBreakeven         = true;
                EnableTradingNonPerfectBars = false;
                AutoBreakevenOffset         = 0;
                BarsToWait                  = 3; // Pending limit orders stay alive for 3 bars
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

                HotkeyLong                  = SniperKey.W;
                HotkeyShort                 = SniperKey.S; 
                HotkeyMomentumLong          = SniperKey.Ctrl_Up; 
                HotkeyMomentumShort         = SniperKey.Ctrl_Down; 
                HotkeyCancel                = SniperKey.X;
                HotkeyScratch               = SniperKey.C;
            }
            else if (State == State.DataLoaded)
            {
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
                RemoveDrawObject("Trail_Notice");
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

            for(int i=0; i<9; i++) myGrid.RowDefinitions.Add(new RowDefinition());

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

            buyButton = new Button { Content = "LONG (Smart)", Foreground = Brushes.White, Background = Brushes.DarkGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            buyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartLong(), null);
            Grid.SetRow(buyButton, 1);

            sellButton = new Button { Content = "SHORT (Smart)", Foreground = Brushes.White, Background = Brushes.DarkRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            sellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartShort(), null);
            Grid.SetRow(sellButton, 2);

            deepBuyButton = new Button { Content = "DEEP LONG (Max PB)", Foreground = Brushes.White, Background = Brushes.SeaGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            deepBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteDeepSmartLong(), null);
            Grid.SetRow(deepBuyButton, 3);

            deepSellButton = new Button { Content = "DEEP SHORT (Max PB)", Foreground = Brushes.White, Background = Brushes.Firebrick, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            deepSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteDeepSmartShort(), null);
            Grid.SetRow(deepSellButton, 4);

            momentumBuyButton = new Button { Content = "MOMENTUM LONG", Foreground = Brushes.White, Background = Brushes.MediumSeaGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumLong(), null);
            Grid.SetRow(momentumBuyButton, 5);

            momentumSellButton = new Button { Content = "MOMENTUM SHORT", Foreground = Brushes.White, Background = Brushes.IndianRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumShort(), null);
            Grid.SetRow(momentumSellButton, 6);

            scratchButton = new Button { Content = $"SCRATCH TP (+{ScratchOffsetTicks})", Foreground = Brushes.Black, Background = Brushes.Gold, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            scratchButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteScratch(), null);
            Grid.SetRow(scratchButton, 7);

            cancelButton = new Button { Content = "CANCEL ALL", Foreground = Brushes.White, Background = Brushes.DarkOrange, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            cancelButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteCancel(), null);
            Grid.SetRow(cancelButton, 8);

            myGrid.Children.Add(atmToggleButton);
            myGrid.Children.Add(buyButton);
            myGrid.Children.Add(sellButton);
            if (!ShowOnlySmartButtons){
                myGrid.Children.Add(deepBuyButton);
                myGrid.Children.Add(deepSellButton);
                myGrid.Children.Add(momentumBuyButton);
                myGrid.Children.Add(momentumSellButton);
            }
            myGrid.Children.Add(scratchButton);
            myGrid.Children.Add(cancelButton);
            
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

        private void ExecuteCancel()
        {
            trapLongActive = false;
            trapShortActive = false;
            
            RemoveDrawObject("TrapTrigger");
            RemoveDrawObject("TrapLimit");
            RemoveDrawObject("SmartOrderError");
            RemoveDrawObject("BE_Notice");
            RemoveDrawObject("SL_Notice");
            RemoveDrawObject("Scratch_Notice");
            RemoveDrawObject("Trail_Notice");

            lock(listLock)
            {
                foreach (var kvp in trackedAtms)
                {
                    AtmStrategyCancelEntryOrder(kvp.Key);
                    AtmStrategyClose(kvp.Key); 
                }
                trackedAtms.Clear();
            }
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
                    if (state.EntryComplete && PullTargetToScratch(RunnerTargetName, state.AtmId, scratchPrice)) adjustedTargets++;
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
            AtmStrategyChangeStopTarget(price, 0, targetName, atmId);
            return true;
        }

        private void ExecuteMomentumLong()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

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
                    ScratchTargetPrice = 0
                }; 
            }
            
            if(c0 >= entryPrice) AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Buy, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorId) => { });
        }

        private void ExecuteMomentumShort()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

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
                    ScratchTargetPrice = 0
                }; 
            }
            
            if(c0 <= entryPrice) AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Sell, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, selectedAtm, atmId, (errorCode, errorId) => { });
        }

        private void ExecuteSmartLong()
        {
            double h1, l1, h2, l2, c0, open1, close1, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; open1 = cOpen1; c0 = cClose0; close1 = cClose1; bar = cBar; tSize = cTickSize; }
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


            if (!EnableTradingNonPerfectBars && close1 < ((h1 + l1) / 2)) {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Bar did not close in the upper half!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            if (!EnableTradingNonPerfectBars && close1 < open1) {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: You are trying to go long on a red bar?", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }
            
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
            trapBarNumber = bar;
            trapAtmTemplateLong = availableAtms[currentAtmIndex]; 
            trapLongActive = true;
            trapShortActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Long Trap Armed. Limit: {trapLimitPrice}, SL: {trapStopLossPrice}");
        }

        private void ExecuteSmartShort()
        {
            double h1, l1, h2, l2, c0, open1, close1, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; open1 = cOpen1; c0 = cClose0; close1 = cClose1; bar = cBar; tSize = cTickSize; }
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


            if (!EnableTradingNonPerfectBars && close1 > ((h1 + l1) / 2) ) {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Bar did not close in the lower half!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }
            if (!EnableTradingNonPerfectBars && close1 > open1) {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: You are trying to go short on a green bar?", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }
            
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

            trapBarNumber = bar;
            trapAtmTemplateShort = availableAtms[currentAtmIndex]; 
            trapShortActive = true;
            trapLongActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Short Trap Armed. Limit: {trapLimitPrice}, SL: {trapStopLossPrice}");
        }

        private void ExecuteDeepSmartLong()
        {
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double structuralStop = l1 - tSize;
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
            trapBarNumber = bar;
            trapAtmTemplateLong = availableAtms[currentAtmIndex]; 
            trapLongActive = true;
            trapShortActive = false; 
            
            Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DarkCyan, DashStyleHelper.Solid, 2);
            RemoveDrawObject("SmartOrderError");
        }

        private void ExecuteDeepSmartShort()
        {
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", "ERROR: Inside Bar blocked!", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                return;
            }

            double structuralStop = h1 + tSize;
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
            trapBarNumber = bar;
            trapAtmTemplateShort = availableAtms[currentAtmIndex]; 
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

            AtmStrategyChangeStopTarget(0, price, RunnerStopName, state.AtmId);
            state.CurrentRunnerStop = price;
            RemoveDrawObject("BE_Notice");
            Draw.TextFixed(this, "Trail_Notice", $"TRAIL: {RunnerStopName} secured at {price}", TextPosition.BottomRight, Brushes.DeepSkyBlue, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Runner trail: {RunnerStopName} moved to {price} (swing-based).");
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2) return; 

            bool isNewBar = IsFirstTickOfBar;

            lock(dataLock)
            {
                cHigh2 = High[2];
                cLow2 = Low[2];
                cHigh1 = High[1];
                cLow1 = Low[1];
                cClose1 = Close[1];
                cOpen1 = Open[1]; 
                cClose0 = Close[0];
                cBar = CurrentBar;
                cTickSize = TickSize;
            }

            if (trapLongActive && CurrentBar > trapBarNumber) { trapLongActive = false; RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); }
            if (trapShortActive && CurrentBar > trapBarNumber) { trapShortActive = false; RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); }

            if (trapLongActive && High[0] >= trapTriggerPrice)
            {
                trapLongActive = false; 
                RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); 
                string atmId = GetAtmStrategyUniqueId();
                string uniqueOrderId = "TrapLong_" + atmId;
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Long, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, TriggerPrice = trapTriggerPrice, SetupBarIndex = cBar, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false, ScratchTargetPrice = 0 }; }
                AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, trapAtmTemplateLong, atmId, (errorCode, errorId) => { });
            }

            if (trapShortActive && Low[0] <= trapTriggerPrice)
            {
                trapShortActive = false; 
                RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); 
                string atmId = GetAtmStrategyUniqueId();
                string uniqueOrderId = "TrapShort_" + atmId;
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Short, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, TriggerPrice = trapTriggerPrice, SetupBarIndex = cBar, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false, ScratchTargetPrice = 0 }; }
                AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, trapAtmTemplateShort, atmId, (errorCode, errorId) => { });
            }

            lock(listLock)
            {
                if (trackedAtms.Count > 0)
                {
                    List<string> deadAtms = new List<string>();
                    foreach (var kvp in trackedAtms)
                    {
                        AtmState state = kvp.Value;
                        
                        if (state.EntryPrice == 0)
                        {
                            if (state.TriggerPrice > 0)
                            {
                                if (state.Direction == AtmDirection.Long && High[0] >= state.TriggerPrice + (MaxRunwayTicks * cTickSize))
                                {
                                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Long EXPIRED: Price ran {MaxRunwayTicks} Ticks past trigger. Cancelling Limit Order.");
                                    AtmStrategyCancelEntryOrder(state.AtmId);
                                    AtmStrategyClose(state.AtmId);
                                    deadAtms.Add(state.AtmId);
                                    continue;
                                }
                                else if (state.Direction == AtmDirection.Short && Low[0] <= state.TriggerPrice - (MaxRunwayTicks * cTickSize))
                                {
                                    Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Smart Short EXPIRED: Price ran {MaxRunwayTicks} Ticks past trigger. Cancelling Limit Order.");
                                    AtmStrategyCancelEntryOrder(state.AtmId);
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
                                AtmStrategyCancelEntryOrder(state.AtmId);
                                AtmStrategyClose(state.AtmId);
                                deadAtms.Add(state.AtmId);
                                continue;
                            }
                            // --- END BAR EXPIRATION CHECK ---

                            string[] entryStatus = GetAtmStrategyEntryOrderStatus(state.EntryOrderId);
                            if (entryStatus != null && entryStatus.Length > 2)
                            {
                                string status = entryStatus[2]; 
                                if (status == "Filled" || status == "PartFilled")
                                {
                                    double parsedPrice;
                                    if (double.TryParse(entryStatus[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsedPrice)) 
                                    {
                                        state.EntryPrice = parsedPrice;
                                    }
                                }
                            }
                        }

                        // Sobald die Position einmal da war und jetzt weg ist, ist die ATM terminiert.
                        // Ab hier keine Statusabfragen mehr, sonst loggt NT "ATM strategy ID does not exist".
                        int atmQty = state.EntryPrice != 0 ? GetAtmStrategyPositionQuantity(state.AtmId) : 0;
                        if (atmQty > 0) state.PositionSeen = true;
                        if (state.PositionSeen && atmQty == 0)
                        {
                            deadAtms.Add(state.AtmId);
                            continue;
                        }

                        // Der Runner-Bracket entsteht erst mit der letzten Entry-Execution.
                        // Solange die Entry-Order noch Fills liefern kann, fragen wir Stop2/Target2
                        // gar nicht erst ab - sonst loggt NT "Order name 'Stop2' is invalid".
                        if (!state.EntryComplete)
                        {
                            string[] eStatus = GetAtmStrategyEntryOrderStatus(state.EntryOrderId);
                            if (eStatus != null && eStatus.Length > 2)
                            {
                                string eState = eStatus[2];
                                if (eState == "Filled" || eState == "Cancelled" || eState == "Rejected") state.EntryComplete = true;
                            }
                        }

                        if (state.EntryPrice != 0 && state.IntendedStopPrice > 0)
                        {
                            if (!state.Stop1Adjusted)
                            {
                                string[,] s1Status = GetAtmStrategyStopTargetOrderStatus(Stop1Name, state.AtmId);
                                if (s1Status != null && s1Status.GetLength(0) > 0 && s1Status.GetLength(1) > 2)
                                {
                                    string status = s1Status[0, 2];
                                    if (status == "Accepted" || status == "Working")
                                    {
                                        AtmStrategyChangeStopTarget(0, state.IntendedStopPrice, Stop1Name, state.AtmId);
                                        state.Stop1Adjusted = true;
                                    }
                                }
                            }

                            if (state.EntryComplete && !state.RunnerStopAdjusted)
                            {
                                string[,] s2Status = GetAtmStrategyStopTargetOrderStatus(RunnerStopName, state.AtmId);
                                if (s2Status != null && s2Status.GetLength(0) > 0 && s2Status.GetLength(1) > 2)
                                {
                                    string status = s2Status[0, 2];
                                    if (status == "Accepted" || status == "Working")
                                    {
                                        AtmStrategyChangeStopTarget(0, state.IntendedStopPrice, RunnerStopName, state.AtmId);
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

                        if (EnableAutoBreakeven && state.EntryPrice != 0 && !state.BeTriggered)
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

                                    AtmStrategyChangeStopTarget(0, bePrice, RunnerStopName, state.AtmId);
                                    state.BeTriggered = true;
                                    state.CurrentRunnerStop = bePrice;
                                    
                                    RemoveDrawObject("SL_Notice");
                                    Draw.TextFixed(this, "BE_Notice", $"AUTO-BE: {RunnerStopName} secured at entry!", TextPosition.BottomRight, Brushes.LimeGreen, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                                }
                            }
                        }

                        if (EnableRunnerTrail && state.BeTriggered && isNewBar)
                        {
                            ManageRunnerTrail(state);
                        }

                        // Fallback fuer den Fall, dass Fill und Close auf denselben Tick fallen.
                        if (state.EntryPrice != 0 && atmQty == 0) deadAtms.Add(state.AtmId);
                    }
                    
                    foreach(string id in deadAtms) 
                    {
                        trackedAtms.Remove(id);
                        RemoveDrawObject("BE_Notice");
                        RemoveDrawObject("Trail_Notice");
                        RemoveDrawObject("SL_Notice");
                        RemoveDrawObject("Scratch_Notice");
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