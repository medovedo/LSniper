#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
        
        public bool Stop1Adjusted;        
        public bool RunnerStopAdjusted;
        public bool DisplayNotified;
    }

    public class LotsenhofSniper : Strategy
    {
        private Button buyButton;
        private Button sellButton;
        private Button momentumBuyButton;
        private Button momentumSellButton;
        // NEW MACK BUTTONS
        private Button mackMomentumBuyButton;
        private Button mackMomentumSellButton;
        
        private Button cancelButton;
        private Grid myGrid;
        
        private bool trapLongActive = false;
        private bool trapShortActive = false;
        private double trapTriggerPrice = 0.0;
        private double trapLimitPrice = 0.0;
        private double trapStopLossPrice = 0.0; 
        private int trapBarNumber = 0;
        
        private double cHigh1, cLow1, cHigh2, cLow2, cClose0, cTickSize;
        private int cBar;
        private object dataLock = new object();
        private object listLock = new object();
        
        private Dictionary<string, AtmState> trackedAtms = new Dictionary<string, AtmState>();
        
        [NinjaScriptProperty]
        [Display(Name="ATM Template Name (WADE)", Description="Exakter Name des ATM Templates", Order=1, GroupName="1. Parameters")]
        public string AtmTemplateName { get; set; }

        // NEW: ATM TEMPLATE PROPERTY FOR MACK
        [NinjaScriptProperty]
        [Display(Name="ATM Template MACK", Description="Exakter Name des ATM Templates fuer MACK", Order=2, GroupName="1. Parameters")]
        public string AtmTemplateMack { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name="Max Pullback (%)", Description="Maximaler Pullback der Signalkerze in Prozent", Order=3, GroupName="1. Parameters")]
        public double MaxPullbackPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Inside-Bars blockieren", Description="Verhindert Trades, wenn die Signalkerze eine Inside-Bar ist.", Order=4, GroupName="1. Parameters")]
        public bool BlockInsideBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name="Max Slippage (Ticks)", Description="Akzeptierte Slippage für Momentum Stop-Limit Orders.", Order=5, GroupName="1. Parameters")]
        public int MaxSlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Auto Break-Even aktivieren", Description="Zieht den Stop auf Entry, sobald Target 1 gefillt ist.", Order=1, GroupName="2. Auto-Breakeven")]
        public bool EnableAutoBreakeven { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Auto BE Offset (Ticks)", Description="Ticks über/unter Entry für Break-Even (0 = exakt Entry).", Order=2, GroupName="2. Auto-Breakeven")]
        public int AutoBreakevenOffset { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Name von Target 1", Description="Name des 1. Targets im ATM (Standard: Target1)", Order=3, GroupName="2. Auto-Breakeven")]
        public string Target1Name { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Name des 1. Stops", Description="Name des initialen Stops im ATM (Standard: Stop1)", Order=4, GroupName="2. Auto-Breakeven")]
        public string Stop1Name { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Name des Runner-Stops", Description="Name des Stops für den Runner (Standard: Stop2)", Order=5, GroupName="2. Auto-Breakeven")]
        public string RunnerStopName { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey LONG (Smart)", Description="Taste für Smart Long Entry", Order=1, GroupName="3. Hotkeys")]
        public SniperKey HotkeyLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey SHORT (Smart)", Description="Taste für Smart Short Entry", Order=2, GroupName="3. Hotkeys")]
        public SniperKey HotkeyShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey MOMENTUM LONG", Description="Ignoriert Kerzengröße", Order=3, GroupName="3. Hotkeys")]
        public SniperKey HotkeyMomentumLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey MOMENTUM SHORT", Description="Ignoriert Kerzengröße", Order=4, GroupName="3. Hotkeys")]
        public SniperKey HotkeyMomentumShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Hotkey CANCEL", Description="Taste für Trap Abbruch", Order=5, GroupName="3. Hotkeys")]
        public SniperKey HotkeyCancel { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "MAX STABILITY: Smart Pullback, Momentum Stop-Limit Airbag & Unabhängige SL Anpassung.";
                Name                        = "Lotsenhof Sniper"; 
                Calculate                   = Calculate.OnEachTick;
                IsOverlay                   = true;
                
                AtmTemplateName             = "ATM 1 - 1"; 
                AtmTemplateMack             = "Mack_4_1"; // Default MACK template name
                MaxPullbackPercent          = 49.0; 
                BlockInsideBars             = true; 
                MaxSlippageTicks            = 2; 
                
                EnableAutoBreakeven         = true;
                AutoBreakevenOffset         = 0;
                Target1Name                 = "Target1";
                Stop1Name                   = "Stop1";
                RunnerStopName              = "Stop2";
                
                HotkeyLong                  = SniperKey.W; 
                HotkeyShort                 = SniperKey.S; 
                HotkeyMomentumLong          = SniperKey.Ctrl_Up; 
                HotkeyMomentumShort         = SniperKey.Ctrl_Down; 
                HotkeyCancel                = SniperKey.X; 
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

            // INCREASED ROW COUNT TO ACCOMMODATE MACK BUTTONS
            for(int i=0; i<7; i++) myGrid.RowDefinitions.Add(new RowDefinition());

            buyButton = new Button { Content = "LONG (Smart)", Foreground = Brushes.White, Background = Brushes.DarkGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            buyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartLong(), null);
            Grid.SetRow(buyButton, 0);

            sellButton = new Button { Content = "SHORT (Smart)", Foreground = Brushes.White, Background = Brushes.DarkRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            sellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteSmartShort(), null);
            Grid.SetRow(sellButton, 1);

            momentumBuyButton = new Button { Content = "MOMENTUM LONG", Foreground = Brushes.White, Background = Brushes.MediumSeaGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumLong(), null);
            Grid.SetRow(momentumBuyButton, 2);

            momentumSellButton = new Button { Content = "MOMENTUM SHORT", Foreground = Brushes.White, Background = Brushes.IndianRed, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            momentumSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMomentumShort(), null);
            Grid.SetRow(momentumSellButton, 3);

            // MACK BUTTONS
            mackMomentumBuyButton = new Button { Content = "MACK MOM. LONG", Foreground = Brushes.White, Background = Brushes.Green, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            mackMomentumBuyButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMackMomentumLong(), null);
            Grid.SetRow(mackMomentumBuyButton, 4);

            mackMomentumSellButton = new Button { Content = "MACK MOM. SHORT", Foreground = Brushes.White, Background = Brushes.Firebrick, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            mackMomentumSellButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteMackMomentumShort(), null);
            Grid.SetRow(mackMomentumSellButton, 5);

            cancelButton = new Button { Content = "CANCEL ALL", Foreground = Brushes.White, Background = Brushes.DarkOrange, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Padding = new Thickness(10, 5, 10, 5) };
            cancelButton.Click += (o, e) => TriggerCustomEvent(state => ExecuteCancel(), null);
            Grid.SetRow(cancelButton, 6);

            myGrid.Children.Add(buyButton);
            myGrid.Children.Add(sellButton);
            myGrid.Children.Add(momentumBuyButton);
            myGrid.Children.Add(momentumSellButton);
            myGrid.Children.Add(mackMomentumBuyButton);
            myGrid.Children.Add(mackMomentumSellButton);
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
            
            lock(listLock)
            {
                foreach (var kvp in trackedAtms)
                {
                    AtmStrategyCancelEntryOrder(kvp.Key);
                    AtmStrategyClose(kvp.Key); 
                }
                trackedAtms.Clear();
            }
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] Lotsenhof Sniper: Killswitch aktiviert. Alle Orders & Traps GELÖSCHT.");
        }

        private void ExecuteMomentumLong()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MOMENTUM REJECTED (LONG): Inside-Bar erkannt.");
                return;
            }

            double entryPrice = h1 + tSize; 
            double limitPrice = entryPrice + (MaxSlippageTicks * tSize); 
            double intendedStop = l1 - tSize; 
            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MomLong_" + atmId; 
            
            lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Long, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = intendedStop, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
            
            if(c0 >= entryPrice) AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Buy, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
            
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MOMENTUM EXEKUTION: LONG platziert bei {entryPrice} (Max Limit: {limitPrice}).");
        }

        private void ExecuteMomentumShort()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MOMENTUM REJECTED (SHORT): Inside-Bar erkannt.");
                return;
            }

            double entryPrice = l1 - tSize;
            double limitPrice = entryPrice - (MaxSlippageTicks * tSize); 
            double intendedStop = h1 + tSize; 
            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MomShort_" + atmId; 
            
            lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Short, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = intendedStop, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
            
            if(c0 <= entryPrice) AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Sell, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
            
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MOMENTUM EXEKUTION: SHORT platziert bei {entryPrice} (Max Limit: {limitPrice}).");
        }

        // NEW: MACK MOMENTUM EXECUTION METHODS
        private void ExecuteMackMomentumLong()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MACK MOMENTUM REJECTED (LONG): Inside-Bar erkannt.");
                return;
            }

            double entryPrice = h1 + tSize; 
            double limitPrice = entryPrice + (MaxSlippageTicks * tSize); 
            double intendedStop = l1 - tSize; 
            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MackMomLong_" + atmId; 
            
            lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Long, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = intendedStop, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
            
            // Uses AtmTemplateMack instead of AtmTemplateName
            if(c0 >= entryPrice) AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateMack, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Buy, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, AtmTemplateMack, atmId, (errorCode, errorId) => { });
            
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MACK MOMENTUM EXEKUTION: LONG platziert bei {entryPrice} (Max Limit: {limitPrice}).");
        }

        private void ExecuteMackMomentumShort()
        {
            RemoveDrawObject("SmartOrderError");
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;

            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MACK MOMENTUM REJECTED (SHORT): Inside-Bar erkannt.");
                return;
            }

            double entryPrice = l1 - tSize;
            double limitPrice = entryPrice - (MaxSlippageTicks * tSize); 
            double intendedStop = h1 + tSize; 
            string atmId = GetAtmStrategyUniqueId();
            string uniqueOrderId = "MackMomShort_" + atmId; 
            
            lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Short, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = intendedStop, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
            
            // Uses AtmTemplateMack instead of AtmTemplateName
            if(c0 <= entryPrice) AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, limitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateMack, atmId, (errorCode, errorId) => { });
            else AtmStrategyCreate(OrderAction.Sell, OrderType.StopLimit, limitPrice, entryPrice, TimeInForce.Gtc, uniqueOrderId, AtmTemplateMack, atmId, (errorCode, errorId) => { });
            
            Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] MACK MOMENTUM EXEKUTION: SHORT platziert bei {entryPrice} (Max Limit: {limitPrice}).");
        }

        private void ExecuteSmartLong()
        {
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] SMART REJECTED (LONG): Inside-Bar erkannt.");
                return;
            }

            double candleSize = Math.Round((h1 - l1) / tSize);
            
            if (candleSize > 10)
            {
                double requiredPullback = candleSize - 11;
                double maxPullback = candleSize * (MaxPullbackPercent / 100.0);

                if (requiredPullback > maxPullback)
                {
                    Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Signalkerze zu groß ({candleSize} Ticks)!\nBenötigter Pullback ({requiredPullback} Ticks) ist > {MaxPullbackPercent}% der Kerze ({maxPullback:F1} Ticks).\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                    return;
                }

                trapTriggerPrice = h1 + tSize;
                trapLimitPrice = (l1 - tSize) + (12 * tSize); 
                trapStopLossPrice = l1 - tSize; 
                trapBarNumber = bar;
                trapLongActive = true;
                trapShortActive = false; 
                
                Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
                Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
                RemoveDrawObject("SmartOrderError");
                
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] SMART TRAP GESETZT (LONG). Kerze = {candleSize} Ticks.");
            }
            else { ExecuteMomentumLong(); }
        }

        private void ExecuteSmartShort()
        {
            double h1, l1, h2, l2, c0, tSize; int bar;
            lock(dataLock) { h1 = cHigh1; l1 = cLow1; h2 = cHigh2; l2 = cLow2; c0 = cClose0; bar = cBar; tSize = cTickSize; }
            if (bar < 2) return;
            
            if (BlockInsideBars && h1 <= h2 && l1 >= l2)
            {
                Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Inside-Bar erkannt!\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] SMART REJECTED (SHORT): Inside-Bar erkannt.");
                return;
            }

            double candleSize = Math.Round((h1 - l1) / tSize);
            
            if (candleSize > 10)
            {
                double requiredPullback = candleSize - 11;
                double maxPullback = candleSize * (MaxPullbackPercent / 100.0);

                if (requiredPullback > maxPullback)
                {
                    Draw.TextFixed(this, "SmartOrderError", $"FEHLER: Signalkerze zu groß ({candleSize} Ticks)!\nBenötigter Pullback ({requiredPullback} Ticks) ist > {MaxPullbackPercent}% der Kerze ({maxPullback:F1} Ticks).\nTrade blockiert. Cancel drücken zum Ausblenden.", TextPosition.Center, Brushes.Red, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 14) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                    return;
                }

                trapTriggerPrice = l1 - tSize;
                trapLimitPrice = (h1 + tSize) - (12 * tSize); 
                trapStopLossPrice = h1 + tSize; 
                trapBarNumber = bar;
                trapShortActive = true;
                trapLongActive = false; 
                
                Draw.HorizontalLine(this, "TrapTrigger", trapTriggerPrice, Brushes.Orange, DashStyleHelper.Dash, 2);
                Draw.HorizontalLine(this, "TrapLimit", trapLimitPrice, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
                RemoveDrawObject("SmartOrderError");
                
                Print($"[{DateTime.Now.ToString("HH:mm:ss.fff")}] SMART TRAP GESETZT (SHORT). Kerze = {candleSize} Ticks.");
            }
            else { ExecuteMomentumShort(); }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2) return; 

            lock(dataLock)
            {
                cHigh2 = High[2];
                cLow2 = Low[2];
                cHigh1 = High[1];
                cLow1 = Low[1];
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
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Long, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
                
                AtmStrategyCreate(OrderAction.Buy, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
            }

            if (trapShortActive && Low[0] <= trapTriggerPrice)
            {
                trapShortActive = false; 
                RemoveDrawObject("TrapTrigger"); RemoveDrawObject("TrapLimit"); 
                string atmId = GetAtmStrategyUniqueId();
                string uniqueOrderId = "TrapShort_" + atmId;
                
                lock(listLock) { trackedAtms[atmId] = new AtmState { AtmId = atmId, EntryOrderId = uniqueOrderId, Direction = AtmDirection.Short, EntryPrice = 0, BeTriggered = false, IntendedStopPrice = trapStopLossPrice, Stop1Adjusted = false, RunnerStopAdjusted = false, DisplayNotified = false }; }
                
                AtmStrategyCreate(OrderAction.Sell, OrderType.Limit, trapLimitPrice, 0, TimeInForce.Gtc, uniqueOrderId, AtmTemplateName, atmId, (errorCode, errorId) => { });
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

                            if (!state.RunnerStopAdjusted)
                            {
                                string[,] s2Status = GetAtmStrategyStopTargetOrderStatus(RunnerStopName, state.AtmId);
                                if (s2Status != null && s2Status.GetLength(0) > 0 && s2Status.GetLength(1) > 2)
                                {
                                    string status = s2Status[0, 2];
                                    if (status == "Accepted" || status == "Working")
                                    {
                                        AtmStrategyChangeStopTarget(0, state.IntendedStopPrice, RunnerStopName, state.AtmId);
                                        state.RunnerStopAdjusted = true;
                                    }
                                }
                            }

                            if (state.Stop1Adjusted && !state.DisplayNotified)
                            {
                                state.DisplayNotified = true;
                                Draw.TextFixed(this, "SL_Notice", $"SMART STOP: Gesichert auf {state.IntendedStopPrice}", TextPosition.BottomLeft, Brushes.Orange, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
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
                                    
                                    RemoveDrawObject("SL_Notice");
                                    Draw.TextFixed(this, "BE_Notice", $"AUTO-BE: {RunnerStopName} auf Entry gesichert!", TextPosition.BottomRight, Brushes.LimeGreen, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 12) { Bold = true }, Brushes.Transparent, Brushes.Transparent, 0);
                                }
                            }
                        }

                        if (state.EntryPrice != 0 && GetAtmStrategyPositionQuantity(state.AtmId) == 0)
                        {
                            deadAtms.Add(state.AtmId);
                        }
                    }
                    
                    foreach(string id in deadAtms) 
                    {
                        trackedAtms.Remove(id);
                        RemoveDrawObject("BE_Notice"); 
                        RemoveDrawObject("SL_Notice"); 
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