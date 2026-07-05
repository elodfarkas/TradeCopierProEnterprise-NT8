
#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// TradeCopierPro Enterprise - Add-On build with robust UI bootstrap.
// Install path: Documents\NinjaTrader 8\bin\Custom\AddOns\TradeCopierProEnterpriseV3.cs
// IMPORTANT: Do NOT add: using NinjaTrader.Gui.ControlCenter;
// In this NT8 build ControlCenter is a type, not a namespace.

public enum TcpV3RunState { Off = 0, Arming = 1, Ready = 2, On = 3, Paused = 4, Error = 5, KillSwitch = 6, Disconnected = 7 }
public enum TcpV3OperationMode { TestRun = 0, Live = 1 }
public enum TcpV3RuntimeState { Healthy = 0, Warning = 1, Error = 2, Disabled = 3, RiskBlocked = 4, OutOfSync = 5, Unknown = 6 }
public enum TcpV3QuantityRoundingMode { Floor = 0, Ceiling = 1, RoundNearest = 2, MinimumOneIfMasterNonZero = 3, SkipIfBelowOne = 4 }
public enum TcpV3SlippageMode { Off = 0, WarnOnly = 1, BlockOrder = 2, ConvertToLimit = 3 }
public enum TcpV3AuditLevel { Error = 0, Warning = 1, Info = 2, Debug = 3, Trace = 4 }
public enum TcpV3PartialFillMode { CopyPartialFillsImmediately = 0, WaitForFullFill = 1, AggregatePartialFillsWithinMs = 2 }
public enum TcpV3SyncStatus { InSync = 0, Pending = 1, Delayed = 2, OutOfSync = 3, Unknown = 4, ReconcileFailed = 5 }
public enum TcpV3RiskResult { Allowed = 0, Warning = 1, Blocked = 2, Disabled = 3 }

namespace NinjaTrader.NinjaScript.AddOns
{
    #region Diagnostics
    public static class TcpV3Diagnostics
    {
        public static void Info(string msg) { SafeOutput("INFO", msg, null); }
        public static void Warn(string msg) { SafeOutput("WARN", msg, null); }
        public static void Error(string msg, Exception ex) { SafeOutput("ERROR", msg, ex); }

        public static void ShowError(string title, Exception ex)
        {
            try
            {
                string text = title + Environment.NewLine + Environment.NewLine + (ex == null ? "Unknown error" : ex.ToString());
                MessageBox.Show(text, "TradeCopier Pro Enterprise", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        public static void SafeUi(Action action, string context)
        {
            try
            {
                Application app = Application.Current;
                if (app != null && app.Dispatcher != null)
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { action(); }
                        catch (Exception ex) { Error("SafeUi failed: " + context, ex); ShowError("UI action failed: " + context, ex); }
                    }));
                }
                else
                    action();
            }
            catch (Exception ex)
            {
                Error("SafeUi dispatch failed: " + context, ex);
                ShowError("UI dispatch failed: " + context, ex);
            }
        }

        private static void SafeOutput(string level, string msg, Exception ex)
        {
            try
            {
                string line = "TCPV3 " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + msg + (ex == null ? string.Empty : Environment.NewLine + ex.ToString());
                NinjaTrader.Code.Output.Process(line, PrintTo.OutputTab1);
            }
            catch { }
        }
    }
    #endregion

    #region AddOn bootstrap
    public class TradeCopierProEnterpriseV3 : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem toolsMenu;
        private NinjaTrader.Gui.ControlCenter controlCenter;
        private TradeCopierV3ControlCenter window;
        private bool menuAdded;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TradeCopierPro Enterprise";
                TcpV3Diagnostics.Info("AddOn SetDefaults");
            }
            else if (State == State.Terminated)
            {
                TcpV3Diagnostics.Info("AddOn Terminated");
                try { TradeCopierV3Services.Instance.SafeShutdown(); }
                catch (Exception ex) { TcpV3Diagnostics.Error("Shutdown failed", ex); }
            }
        }

        protected override void OnWindowCreated(Window w)
        {
            try
            {
                TcpV3Diagnostics.Info("OnWindowCreated: " + (w == null ? "null" : w.GetType().FullName));

                NinjaTrader.Gui.ControlCenter cc = w as NinjaTrader.Gui.ControlCenter;
                if (cc == null)
                    return;

                controlCenter = cc;
                TcpV3Diagnostics.Info("Control Center detected");

                if (menuAdded)
                {
                    TcpV3Diagnostics.Info("Menu item already added; skipping duplicate registration");
                    return;
                }

                toolsMenu = cc.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
                if (toolsMenu == null)
                {
                    TcpV3Diagnostics.Warn("Tools menu not found: ControlCenterMenuItemTools");
                    return;
                }

                foreach (object item in toolsMenu.Items)
                {
                    NTMenuItem existing = item as NTMenuItem;
                    if (existing != null && Convert.ToString(existing.Header) == "TradeCopier Pro Enterprise")
                    {
                        menuItem = existing;
                        menuItem.Click -= OnMenuClick;
                        menuItem.Click += OnMenuClick;
                        menuAdded = true;
                        TcpV3Diagnostics.Info("Existing menu item found and click handler refreshed");
                        return;
                    }
                }

                menuItem = new NTMenuItem
                {
                    Header = "TradeCopier Pro Enterprise",
                    Style = Application.Current != null ? Application.Current.TryFindResource("MainMenuItem") as Style : null
                };
                menuItem.Click += OnMenuClick;
                toolsMenu.Items.Add(menuItem);
                menuAdded = true;
                TcpV3Diagnostics.Info("Menu item added");
            }
            catch (Exception ex)
            {
                LogException("OnWindowCreated failed", ex);
            }
        }

        protected override void OnWindowDestroyed(Window w)
        {
            try
            {
                TcpV3Diagnostics.Info("OnWindowDestroyed: " + (w == null ? "null" : w.GetType().FullName));
                if (w != controlCenter)
                    return;

                if (menuItem != null)
                    menuItem.Click -= OnMenuClick;
                if (toolsMenu != null && menuItem != null && toolsMenu.Items.Contains(menuItem))
                    toolsMenu.Items.Remove(menuItem);

                menuItem = null;
                toolsMenu = null;
                controlCenter = null;
                menuAdded = false;
                TcpV3Diagnostics.Info("Menu item removed for destroyed Control Center");
            }
            catch (Exception ex)
            {
                LogException("OnWindowDestroyed failed", ex);
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            try
            {
                LogDiagnostic("Menu click received");
                Window owner = controlCenter as Window;
                if (owner == null && Application.Current != null)
                    owner = Application.Current.MainWindow;

                if (owner != null && owner.Dispatcher != null)
                    owner.Dispatcher.BeginInvoke(new Action(() => OpenOrActivateControlCenter(owner)));
                else if (Application.Current != null && Application.Current.Dispatcher != null)
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => OpenOrActivateControlCenter(null)));
                else
                    OpenOrActivateControlCenter(null);
            }
            catch (Exception ex)
            {
                ShowStartupError("Menu click failed", ex);
            }
        }

        private void OpenOrActivateControlCenter(Window owner)
        {
            try
            {
                LogDiagnostic("OpenOrActivateControlCenter start");

                if (window == null)
                {
                    CreateControlCenterWindow(owner);
                    return;
                }

                if (!window.IsLoaded || !window.IsVisible)
                {
                    LogDiagnostic("Existing window not visible; calling Show");
                    window.Show();
                }

                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
                window.Focus();
                window.Topmost = true;
                window.Topmost = false;
                LogDiagnostic("Window activated");
            }
            catch (Exception ex)
            {
                ShowStartupError("Open or activate Control Center failed", ex);
            }
        }

        private void CreateControlCenterWindow(Window owner)
        {
            try
            {
                LogDiagnostic("Creating Control Center window");
                window = new TradeCopierV3ControlCenter();

                if (owner != null && !object.ReferenceEquals(owner, window))
                {
                    window.Owner = owner;
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    window.ShowInTaskbar = false;
                }
                else
                {
                    window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    window.ShowInTaskbar = true;
                }

                window.Closed += OnControlCenterClosed;
                window.Show();

                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
                window.Focus();
                window.Topmost = true;
                window.Topmost = false;
                LogDiagnostic("Window Show called and activated");
            }
            catch (Exception ex)
            {
                window = null;
                ShowStartupError("Create Control Center window failed", ex);
            }
        }

        private void OnControlCenterClosed(object sender, EventArgs e)
        {
            try
            {
                LogDiagnostic("Window Closed");
                if (window != null)
                    window.Closed -= OnControlCenterClosed;
                window = null;
            }
            catch (Exception ex)
            {
                LogException("Window closed handler failed", ex);
            }
        }

        private void ShowStartupError(string title, Exception ex)
        {
            LogException(title, ex);
            TcpV3Diagnostics.ShowError(title, ex);
            try
            {
                TradeCopierV3StartupErrorWindow fallback = new TradeCopierV3StartupErrorWindow(title, ex);
                fallback.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                fallback.Show();
                fallback.Activate();
            }
            catch { }
        }

        private void LogDiagnostic(string message)
        {
            TcpV3Diagnostics.Info(message);
            TryAuditEvent("DIAGNOSTIC", TcpV3AuditLevel.Info, message);
        }

        private void LogException(string context, Exception ex)
        {
            TcpV3Diagnostics.Error(context, ex);
            TryAuditEvent("EXCEPTION", TcpV3AuditLevel.Error, context + ": " + (ex == null ? "unknown" : ex.Message));
        }

        private void TryAuditEvent(string eventType, TcpV3AuditLevel level, string message)
        {
            try
            {
                if (TradeCopierV3Services.Instance.IsInitialized && TradeCopierV3Services.Instance.ViewModel != null)
                {
                    TradeCopierV3Services.Instance.ViewModel.AddEvent(new TcpV3AuditEvent
                    {
                        TimestampUtc = DateTime.UtcNow,
                        LocalTime = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        EventType = eventType,
                        Severity = level,
                        BatchId = string.Empty,
                        Account = string.Empty,
                        Instrument = string.Empty,
                        Message = message
                    });
                }
            }
            catch { }
        }
    }

    public class TradeCopierV3StartupErrorWindow : Window
    {
        public TradeCopierV3StartupErrorWindow(string title, Exception ex)
        {
            Title = "TradeCopier Pro Enterprise - Startup Error";
            Width = 900;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            DockPanel root = new DockPanel { Margin = new Thickness(12) };
            TextBlock header = new TextBlock
            {
                Text = "TradeCopier UI failed to load",
                Foreground = Brushes.White,
                Background = Brushes.DarkRed,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            TextBox tb = new TextBox
            {
                Text = title + Environment.NewLine + Environment.NewLine + (ex == null ? "Unknown error" : ex.ToString()),
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap
            };
            root.Children.Add(tb);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            Button copy = new Button { Content = "Copy Error", Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
            copy.Click += (s, e) => { try { Clipboard.SetText(tb.Text); } catch { } };
            Button close = new Button { Content = "Close", Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
            close.Click += (s, e) => Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            Content = root;
        }
    }
    #endregion

    #region Models
    [Serializable]
    public class TcpV3Settings
    {
        public string MasterAccountName { get; set; }
        public string MasterInstrumentName { get; set; }
        public TcpV3SlippageMode SlippageMode { get; set; }
        public int GlobalMaxSlippageTicks { get; set; }
        public int GlobalMaxOrderQuantity { get; set; }
        public int GlobalMaxDailyContracts { get; set; }
        public int GlobalMaxTradesPerDay { get; set; }
        public bool GlobalTradingEnabled { get; set; }
        public bool GlobalKillSwitch { get; set; }
        public bool RequirePreflightBeforeEnable { get; set; }
        public bool BlockTradingWhenConfigDirty { get; set; }
        public bool DryRunMode { get; set; }
        public bool SimpleWorkflowMode { get; set; }
        public bool StrictSlippageRequiresQuote { get; set; }
        public bool BlockIfQuoteUnavailable { get; set; }
        public bool UseLastAsFallback { get; set; }
        public int MaxQuoteAgeMs { get; set; }
        public int DailyResetTime { get; set; }
        public bool RequireAllFollowersFlatBeforeEnable { get; set; }
        public bool RequireMasterFlatBeforeEnable { get; set; }
        public bool KillSwitchCancelWorkingOrders { get; set; }
        public bool KillSwitchFlattenFollowers { get; set; }
        public bool KillSwitchIncludeDisabledFollowers { get; set; }
        public bool KillSwitchAllowResetOnlyIfAllFlat { get; set; }
        public TcpV3PartialFillMode PartialFillMode { get; set; }
        public int MasterFlatReconcileDelayMs { get; set; }
        public int DuplicateTtlSeconds { get; set; }
        public bool EnableCsvAudit { get; set; }
        public string AuditFileName { get; set; }
        public string ContactDisplayName { get; set; }
        public string ContactEmail { get; set; }
        public string ContactTelegram { get; set; }
        public string ContactWebsite { get; set; }
        public string ContactPhone { get; set; }
        public string ContactCompanyName { get; set; }
        public string ContactSalesText { get; set; }
        public List<TcpV3FollowerConfig> Followers { get; set; }

        public TcpV3Settings()
        {
            MasterAccountName = string.Empty;
            MasterInstrumentName = string.Empty;
            SlippageMode = TcpV3SlippageMode.WarnOnly;
            GlobalMaxSlippageTicks = 4;
            GlobalMaxOrderQuantity = 100;
            GlobalMaxDailyContracts = 1000;
            GlobalMaxTradesPerDay = 0;
            GlobalTradingEnabled = true;
            GlobalKillSwitch = false;
            RequirePreflightBeforeEnable = true;
            BlockTradingWhenConfigDirty = true;
            DryRunMode = true;
            SimpleWorkflowMode = true;
            StrictSlippageRequiresQuote = true;
            BlockIfQuoteUnavailable = true;
            UseLastAsFallback = true;
            MaxQuoteAgeMs = 1000;
            DailyResetTime = 170000;
            RequireAllFollowersFlatBeforeEnable = true;
            RequireMasterFlatBeforeEnable = false;
            KillSwitchCancelWorkingOrders = true;
            KillSwitchFlattenFollowers = false;
            KillSwitchIncludeDisabledFollowers = false;
            KillSwitchAllowResetOnlyIfAllFlat = true;
            PartialFillMode = TcpV3PartialFillMode.CopyPartialFillsImmediately;
            MasterFlatReconcileDelayMs = 1500;
            DuplicateTtlSeconds = 900;
            EnableCsvAudit = true;
            AuditFileName = "TradeCopierProEnterpriseV3_Audit.csv";
            ContactDisplayName = "Juhász Előd Farkas";
            ContactEmail = string.Empty;
            ContactTelegram = string.Empty;
            ContactWebsite = "https://orderflow-hub.com/";
            ContactPhone = string.Empty;
            ContactCompanyName = "TradeCopier Pro Enterprise";
            ContactSalesText = "Need installation, customization or private NinjaTrader automation support? Contact me and describe your use case.";
            Followers = new List<TcpV3FollowerConfig>();
        }
    }

    [Serializable]
    public class TcpV3FollowerConfig
    {
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public string AccountName { get; set; }
        public string InstrumentName { get; set; }
        public double QuantityMultiplier { get; set; }
        public double ContractConversionFactor { get; set; }
        public TcpV3QuantityRoundingMode RoundingMode { get; set; }
        public int MinQuantity { get; set; }
        public int MaxOrderQuantity { get; set; }
        public int MaxPositionSize { get; set; }
        public int MaxDailyContracts { get; set; }
        public int MaxTradesPerDay { get; set; }
        public int DisableAfterErrorCount { get; set; }
        public int DisableAfterRejectCount { get; set; }
        public int TradingStartTime { get; set; }
        public int TradingEndTime { get; set; }
        public string AllowedInstruments { get; set; }
        public int SlippageTicksLimit { get; set; }
        public bool CopyMarketOrders { get; set; }
        public bool CopyLimitOrders { get; set; }
        public bool CopyStopOrders { get; set; }
        public bool FlattenOnMasterFlat { get; set; }
        public bool RequireFlatBeforeEnable { get; set; }
        public bool BlockNewTradeIfFollowerNotFlat { get; set; }
        public bool BlockOppositeDirectionIfOutOfSync { get; set; }
        public bool AutoFlattenIfOutOfSync { get; set; }

        [XmlIgnore] public Account AccountRef { get; set; }
        [XmlIgnore] public NinjaTrader.Cbi.Instrument InstrumentRef { get; set; }

        public TcpV3FollowerConfig()
        {
            Id = Guid.NewGuid().ToString("N");
            Enabled = true;
            AccountName = string.Empty;
            InstrumentName = string.Empty;
            QuantityMultiplier = 1.0;
            ContractConversionFactor = 1.0;
            RoundingMode = TcpV3QuantityRoundingMode.MinimumOneIfMasterNonZero;
            MinQuantity = 1;
            MaxOrderQuantity = 100;
            MaxPositionSize = 100;
            MaxDailyContracts = 500;
            MaxTradesPerDay = 0;
            DisableAfterErrorCount = 3;
            DisableAfterRejectCount = 3;
            TradingStartTime = 0;
            TradingEndTime = 235959;
            AllowedInstruments = string.Empty;
            SlippageTicksLimit = 4;
            CopyMarketOrders = true;
            CopyLimitOrders = false;
            CopyStopOrders = false;
            FlattenOnMasterFlat = true;
            RequireFlatBeforeEnable = true;
            BlockNewTradeIfFollowerNotFlat = false;
            BlockOppositeDirectionIfOutOfSync = true;
            AutoFlattenIfOutOfSync = false;
        }
    }

    public class TcpV3FollowerRuntime
    {
        public MarketPosition ActualPosition = MarketPosition.Flat;
        public int ActualQuantity;
        public MarketPosition ExpectedPosition = MarketPosition.Flat;
        public int ExpectedQuantity;
        public TcpV3SyncStatus SyncStatus = TcpV3SyncStatus.Unknown;
        public TcpV3RuntimeState RuntimeState = TcpV3RuntimeState.Unknown;
        public int ErrorCount;
        public int RejectCount;
        public int DailyContracts;
        public int DailyTrades;
        public DateTime LastResetDate = DateTime.MinValue;
        public string LastError = string.Empty;
        public string LastOrderState = string.Empty;
        public int LastSubmittedQuantity;
        public double LastFillPrice;
        public double LastSlippageTicks;
        public double CurrentLatencyMs;
        public double AverageLatencyMs;
        public bool DisabledByRisk;
        public bool DisabledByError;
    }

    public class TcpV3CopyBatch
    {
        public string BatchId;
        public string MasterAccount;
        public string MasterInstrument;
        public string MasterOrderId;
        public string MasterExecutionId;
        public OrderAction MasterAction;
        public OrderEntry MasterEntry;
        public int MasterQuantity;
        public double MasterPrice;
        public DateTime MasterExecutionTime;
        public DateTime ReceiveUtc;
    }

    public class TcpV3FollowerOrderTrack
    {
        public string BatchId;
        public string MasterExecutionId;
        public string FollowerAccount;
        public string FollowerInstrument;
        public string OrderId;
        public OrderAction Action;
        public OrderType OrderType;
        public int Quantity;
        public double LimitPrice;
        public DateTime SubmitUtc;
        public OrderState State;
        public string ErrorMessage;
    }

    public class TcpV3AuditEvent
    {
        public DateTime TimestampUtc { get; set; }
        public string LocalTime { get; set; }
        public string EventType { get; set; }
        public TcpV3AuditLevel Severity { get; set; }
        public string BatchId { get; set; }
        public string Account { get; set; }
        public string Instrument { get; set; }
        public string Message { get; set; }
        public string CsvLine()
        {
            return string.Join(",", new string[] { TimestampUtc.ToString("o"), Csv(LocalTime), Csv(EventType), Severity.ToString(), Csv(BatchId), Csv(Account), Csv(Instrument), Csv(Message) });
        }
        private static string Csv(string s)
        {
            if (s == null) return string.Empty;
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    public class TcpV3PreflightResult
    {
        public bool Success;
        public int WarningCount;
        public int ErrorCount;
        public List<string> Lines = new List<string>();
        public override string ToString() { return string.Join(Environment.NewLine, Lines); }
    }
    #endregion

    #region ViewModels
    public class TcpV3FollowerRowViewModel : INotifyPropertyChanged
    {
        public TcpV3FollowerConfig Config { get; private set; }
        public TcpV3FollowerRuntime Runtime { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;
        public TcpV3FollowerRowViewModel(TcpV3FollowerConfig c, TcpV3FollowerRuntime r) { Config = c; Runtime = r; }
        public bool Enabled { get { return Config.Enabled; } set { Config.Enabled = value; OnChanged("Enabled"); } }
        public string AccountName { get { return Config.AccountName; } set { Config.AccountName = value; OnChanged("AccountName"); } }
        public string InstrumentName { get { return Config.InstrumentName; } set { Config.InstrumentName = value; OnChanged("InstrumentName"); } }
        public double Multiplier { get { return Config.QuantityMultiplier; } set { Config.QuantityMultiplier = value; OnChanged("Multiplier"); } }
        public double Conversion { get { return Config.ContractConversionFactor; } set { Config.ContractConversionFactor = value; OnChanged("Conversion"); } }
        public TcpV3QuantityRoundingMode RoundingMode { get { return Config.RoundingMode; } set { Config.RoundingMode = value; OnChanged("RoundingMode"); } }
        public string ExpectedPosition { get { return Runtime.ExpectedPosition + " " + Runtime.ExpectedQuantity; } }
        public string ActualPosition { get { return Runtime.ActualPosition + " " + Runtime.ActualQuantity; } }
        public TcpV3SyncStatus SyncStatus { get { return Runtime.SyncStatus; } }
        public int LastSubmittedQuantity { get { return Runtime.LastSubmittedQuantity; } }
        public string LastOrderState { get { return Runtime.LastOrderState; } }
        public double LastFillPrice { get { return Runtime.LastFillPrice; } }
        public double SlippageTicks { get { return Runtime.LastSlippageTicks; } }
        public double CurrentLatencyMs { get { return Runtime.CurrentLatencyMs; } }
        public double AverageLatencyMs { get { return Runtime.AverageLatencyMs; } }
        public int DailyContracts { get { return Runtime.DailyContracts; } }
        public int DailyTrades { get { return Runtime.DailyTrades; } }
        public int ErrorCount { get { return Runtime.ErrorCount; } }
        public string LastError { get { return Runtime.LastError; } }
        public TcpV3RuntimeState RuntimeState { get { return Runtime.RuntimeState; } }
        public void RefreshAll()
        {
            OnChanged("ExpectedPosition"); OnChanged("ActualPosition"); OnChanged("SyncStatus"); OnChanged("LastSubmittedQuantity");
            OnChanged("LastOrderState"); OnChanged("LastFillPrice"); OnChanged("SlippageTicks"); OnChanged("CurrentLatencyMs");
            OnChanged("AverageLatencyMs"); OnChanged("DailyContracts"); OnChanged("DailyTrades"); OnChanged("ErrorCount"); OnChanged("LastError"); OnChanged("RuntimeState");
        }
        private void OnChanged(string name) { PropertyChangedEventHandler h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }

    public class TradeCopierV3ViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<TcpV3FollowerRowViewModel> Followers { get; private set; }
        public ObservableCollection<TcpV3AuditEvent> Events { get; private set; }
        public ObservableCollection<string> AccountNames { get; private set; }
        private Dispatcher uiDispatcher;
        private readonly object followersSync=new object(), eventsSync=new object(), accountNamesSync=new object();
        public TcpV3Settings Settings { get; private set; }
        private TcpV3RunState runState;
        private TcpV3OperationMode operationMode;
        private string masterPosition, connectionState, lastError, lastAction, preflightReport, lastPreflightHash, currentConfigHash, statusMessage;
        private bool isConfigDirty, lastPreflightSuccess, isEngineInitialized;
        private double avgLatency, worstLatency;
        private string masterRouteText, followerRouteSummaryText, routeTooltipText, copyDirectionText;
        public TcpV3RunState RunState { get { return runState; } set { SetRunStateSafe(value); } }
        public string RunStateText { get { return runState.ToString(); } }
        public TcpV3OperationMode OperationMode { get { return operationMode; } }
        public string OperationModeText { get { return operationMode==TcpV3OperationMode.Live ? "LIVE MODE" : "TEST RUN"; } }
        public bool IsLiveMode { get { return operationMode==TcpV3OperationMode.Live; } }
        public bool IsDryRun { get { return operationMode!=TcpV3OperationMode.Live; } }
        public string MasterPosition { get { return masterPosition; } set { SetMasterPositionSafe(value); } }
        public string ConnectionState { get { return connectionState; } set { SetConnectionStateSafe(value); } }
        public int EnabledFollowersCount { get { return Followers.Count(x=>x.Config.Enabled); } }
        public int HealthyFollowersCount { get { return Followers.Count(x=>x.Runtime.RuntimeState==TcpV3RuntimeState.Healthy); } }
        public int OutOfSyncCount { get { return Followers.Count(x=>x.Runtime.SyncStatus==TcpV3SyncStatus.OutOfSync); } }
        public double AverageLatency { get { return avgLatency; } set { SetAverageLatencySafe(value); } }
        public double WorstLatency { get { return worstLatency; } set { SetWorstLatencySafe(value); } }
        public string LastError { get { return lastError; } set { SetLastErrorSafe(value); } }
        public string LastAction { get { return lastAction; } set { SetLastActionSafe(value); } }
        public string PreflightReport { get { return preflightReport; } set { SetPreflightReportSafe(value); } }
        public bool IsConfigDirty { get { return isConfigDirty; } set { SetIsConfigDirtySafe(value); } }
        public bool LastPreflightSuccess { get { return lastPreflightSuccess; } set { SetLastPreflightSuccessSafe(value); } }
        public string LastPreflightHash { get { return lastPreflightHash; } set { SetLastPreflightHashSafe(value); } }
        public string CurrentConfigHash { get { return currentConfigHash; } set { SetCurrentConfigHashSafe(value); } }
        public string StatusMessage { get { return statusMessage; } set { SetStatusMessageSafe(value); } }
        public bool IsEngineInitialized { get { return isEngineInitialized; } set { SetIsEngineInitializedSafe(value); } }
        public string MasterRouteText { get { return masterRouteText; } }
        public string FollowerRouteSummaryText { get { return followerRouteSummaryText; } }
        public string RouteTooltipText { get { return routeTooltipText; } }
        public string CopyDirectionText { get { return copyDirectionText; } }
        public TradeCopierV3ViewModel(){ Followers=new ObservableCollection<TcpV3FollowerRowViewModel>(); Events=new ObservableCollection<TcpV3AuditEvent>(); AccountNames=new ObservableCollection<string>(); Settings=new TcpV3Settings(); runState=TcpV3RunState.Off; operationMode=Settings.DryRunMode?TcpV3OperationMode.TestRun:TcpV3OperationMode.Live; masterPosition="Unknown"; connectionState="Unknown"; lastError=lastAction=preflightReport=lastPreflightHash=currentConfigHash=string.Empty; statusMessage="Config requires preflight"; isConfigDirty=true; masterRouteText="Master: not selected"; followerRouteSummaryText="Followers: none"; copyDirectionText="Route not ready"; routeTooltipText=string.Empty; try{ BindingOperations.EnableCollectionSynchronization(Followers,followersSync); BindingOperations.EnableCollectionSynchronization(Events,eventsSync); BindingOperations.EnableCollectionSynchronization(AccountNames,accountNamesSync);}catch{} RefreshRouteSummarySafe(); }
        public void AttachDispatcher(Dispatcher dispatcher){ uiDispatcher=dispatcher ?? (Application.Current!=null?Application.Current.Dispatcher:null); }
        private void RunOnUi(Action a){ Dispatcher d=uiDispatcher ?? (Application.Current!=null?Application.Current.Dispatcher:null); if(d==null||d.CheckAccess()) a(); else d.BeginInvoke(new Action(()=>{try{a();}catch(Exception ex){TcpV3Diagnostics.Error("RunOnUi action failed",ex);}})); }
        private void RunOnUiSync(Action a){ Dispatcher d=uiDispatcher ?? (Application.Current!=null?Application.Current.Dispatcher:null); if(d==null||d.CheckAccess()) a(); else d.Invoke(a); }
        private void Raise(params string[] names){ var h=PropertyChanged; if(h==null)return; foreach(string n in names) h(this,new PropertyChangedEventArgs(n)); }
        public void SetSettingsSafe(TcpV3Settings value){ RunOnUiSync(()=>{ Settings=value??new TcpV3Settings(); operationMode=Settings.DryRunMode?TcpV3OperationMode.TestRun:TcpV3OperationMode.Live; Raise("Settings","IsDryRun","OperationMode","OperationModeText","IsLiveMode"); RefreshRouteSummaryCore(); }); }
        public void SetOperationModeSafe(TcpV3OperationMode mode){ RunOnUiSync(()=>{ operationMode=mode; if(Settings!=null) Settings.DryRunMode=mode!=TcpV3OperationMode.Live; Raise("OperationMode","OperationModeText","IsLiveMode","IsDryRun","Settings"); }); }
        public void SetRunStateSafe(TcpV3RunState v){ RunOnUi(()=>{runState=v;Raise("RunState","RunStateText");}); }
        public void SetLastActionSafe(string v){ RunOnUi(()=>{lastAction=v??string.Empty;Raise("LastAction");}); }
        public void SetStatusMessageSafe(string v){ RunOnUi(()=>{statusMessage=v??string.Empty;Raise("StatusMessage");}); }
        public void SetLastErrorSafe(string v){ RunOnUi(()=>{lastError=v??string.Empty;Raise("LastError");}); }
        public void SetMasterPositionSafe(string v){ RunOnUi(()=>{masterPosition=v??string.Empty;Raise("MasterPosition");}); }
        public void SetConnectionStateSafe(string v){ RunOnUi(()=>{connectionState=v??string.Empty;Raise("ConnectionState");}); }
        public void SetPreflightReportSafe(string v){ RunOnUi(()=>{preflightReport=v??string.Empty;Raise("PreflightReport");}); }
        public void SetAverageLatencySafe(double v){ RunOnUi(()=>{avgLatency=v;Raise("AverageLatency");}); }
        public void SetWorstLatencySafe(double v){ RunOnUi(()=>{worstLatency=v;Raise("WorstLatency");}); }
        public void SetIsConfigDirtySafe(bool v){ RunOnUi(()=>{isConfigDirty=v;Raise("IsConfigDirty");}); }
        public void SetLastPreflightSuccessSafe(bool v){ RunOnUiSync(()=>{lastPreflightSuccess=v;Raise("LastPreflightSuccess");}); }
        public void SetCurrentConfigHashSafe(string v){ RunOnUi(()=>{currentConfigHash=v??string.Empty;Raise("CurrentConfigHash");}); }
        public void SetLastPreflightHashSafe(string v){ RunOnUiSync(()=>{lastPreflightHash=v??string.Empty;Raise("LastPreflightHash");}); }
        public void SetIsEngineInitializedSafe(bool v){ RunOnUi(()=>{isEngineInitialized=v;Raise("IsEngineInitialized");}); }
        public void NotifyDryRunChangedSafe(){ RunOnUi(()=>Raise("IsDryRun","OperationMode","OperationModeText","IsLiveMode")); }
        public void RefreshAccounts(){ List<string> names; lock(Account.All) names=Account.All.Select(a=>a.Name).OrderBy(x=>x).ToList(); RunOnUi(()=>{AccountNames.Clear(); foreach(string n in names) AccountNames.Add(n);}); }
        public void RefreshSummary(){ RunOnUi(()=>Raise("EnabledFollowersCount","HealthyFollowersCount","OutOfSyncCount")); }
        public void AddEvent(TcpV3AuditEvent ev){ RunOnUi(()=>{Events.Insert(0,ev); while(Events.Count>1000) Events.RemoveAt(Events.Count-1);}); }
        public void ClearEvents(){ RunOnUi(()=>Events.Clear()); }
        public void RebuildFollowerRows(Dictionary<string,TcpV3FollowerRuntime> runtimeById){ var configs=Settings!=null&&Settings.Followers!=null?Settings.Followers.ToList():new List<TcpV3FollowerConfig>(); var rt=runtimeById!=null?new Dictionary<string,TcpV3FollowerRuntime>(runtimeById):new Dictionary<string,TcpV3FollowerRuntime>(); RunOnUi(()=>{Followers.Clear(); foreach(var c in configs){TcpV3FollowerRuntime r; if(!rt.TryGetValue(c.Id,out r)) r=new TcpV3FollowerRuntime(); Followers.Add(new TcpV3FollowerRowViewModel(c,r));} Raise("EnabledFollowersCount","HealthyFollowersCount","OutOfSyncCount"); RefreshRouteSummaryCore();}); }
        public void RefreshRouteSummarySafe(){ RunOnUi(()=>RefreshRouteSummaryCore()); }
        private void RefreshRouteSummaryCore(){ string ma=Settings!=null?Settings.MasterAccountName:string.Empty; string mi=Settings!=null?Settings.MasterInstrumentName:string.Empty; masterRouteText=(string.IsNullOrWhiteSpace(ma)&&string.IsNullOrWhiteSpace(mi))?"Master: not selected":"Master: "+(string.IsNullOrWhiteSpace(ma)?"?":ma)+" | "+(string.IsNullOrWhiteSpace(mi)?"?":mi); var en=Settings!=null&&Settings.Followers!=null?Settings.Followers.Where(f=>f.Enabled).ToList():new List<TcpV3FollowerConfig>(); int cnt=en.Count; int ic=en.Select(f=>f.InstrumentName??"").Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(); followerRouteSummaryText=cnt==0?"Followers: none":"Followers: "+cnt+" enabled / "+ic+" instruments"; copyDirectionText=(!string.IsNullOrWhiteSpace(ma)&&!string.IsNullOrWhiteSpace(mi)&&cnt>0)?ma+" "+mi+" → "+cnt+" followers":"Route not ready"; StringBuilder t=new StringBuilder(); int max=Math.Min(8,en.Count); for(int i=0;i<max;i++) t.AppendLine((en[i].AccountName??"?")+" → "+(en[i].InstrumentName??"?")+" x"+en[i].QuantityMultiplier.ToString(CultureInfo.InvariantCulture)); if(en.Count>max)t.AppendLine("... +"+(en.Count-max)+" more"); routeTooltipText=t.ToString(); Raise("MasterRouteText","FollowerRouteSummaryText","RouteTooltipText","CopyDirectionText"); }
    }
    #endregion

    #region Services
    public sealed class TradeCopierV3Services
    {
        private static readonly Lazy<TradeCopierV3Services> lazy = new Lazy<TradeCopierV3Services>(() => new TradeCopierV3Services());
        public static TradeCopierV3Services Instance { get { return lazy.Value; } }
        public TradeCopierV3ViewModel ViewModel { get; private set; }
        public TradeCopierV3Engine Engine { get; private set; }
        public bool IsInitialized { get; private set; }
        public Exception InitializationError { get; private set; }
        private readonly object initLock = new object();

        private TradeCopierV3Services()
        {
            ViewModel = new TradeCopierV3ViewModel();
        }

        public bool Initialize()
        {
            lock (initLock)
            {
                if (IsInitialized) return true;
                try
                {
                    TcpV3Diagnostics.Info("Service initialize start");
                    if (ViewModel == null) ViewModel = new TradeCopierV3ViewModel();
                    Engine = new TradeCopierV3Engine(ViewModel);
                    IsInitialized = true;
                    InitializationError = null;
                    TcpV3Diagnostics.Info("Service initialize finished");
                    return true;
                }
                catch (Exception ex)
                {
                    InitializationError = ex;
                    IsInitialized = false;
                    Engine = null;
                    TcpV3Diagnostics.Error("Service initialize failed", ex);
                    try
{ 
    if (ViewModel != null) 
        ViewModel.SetLastErrorSafe("Engine failed to initialize: " + ex.Message); 
} 
catch { }
                    return false;
                }
            }
        }

        public bool RetryInitialize()
        {
            lock (initLock)
            {
                try { if (Engine != null) Engine.Shutdown(); } catch { }
                IsInitialized = false;
                InitializationError = null;
                Engine = null;
            }
            return Initialize();
        }

        public void SafeShutdown()
        {
            try { if (Engine != null) Engine.Shutdown(); }
            catch (Exception ex) { TcpV3Diagnostics.Error("Engine shutdown failed", ex); }
        }
    }
    #endregion

    #region State machine
    public class TcpV3StateMachine
    {
        private readonly object sync = new object();
        private TcpV3RunState state = TcpV3RunState.Off;
        public TcpV3RunState State { get { lock (sync) return state; } }
        public string LastTransitionReason { get; private set; }
        public DateTime LastTransitionTime { get; private set; }
        public bool IsTradingAllowed { get { return State == TcpV3RunState.On; } }
        public bool IsConfigEditable
        {
            get
            {
                TcpV3RunState s = State;
                return s == TcpV3RunState.Off || s == TcpV3RunState.Ready || s == TcpV3RunState.Error;
            }
        }
        public bool IsEmergencyOnly
        {
            get
            {
                TcpV3RunState s = State;
                return s == TcpV3RunState.KillSwitch || s == TcpV3RunState.Error || s == TcpV3RunState.Disconnected;
            }
        }

        public bool CanTransition(TcpV3RunState from, TcpV3RunState to, bool explicitReset)
        {
            if (from == to) return true;
            if (to == TcpV3RunState.KillSwitch) return true;
            if (from == TcpV3RunState.KillSwitch) return explicitReset && to == TcpV3RunState.Off;
            if (from == TcpV3RunState.Error) return explicitReset && to == TcpV3RunState.Off;
            if (from == TcpV3RunState.Disconnected) return to == TcpV3RunState.Off || to == TcpV3RunState.Arming;
            if (from == TcpV3RunState.Off) return to == TcpV3RunState.Arming || to == TcpV3RunState.Error || to == TcpV3RunState.Disconnected;
            if (from == TcpV3RunState.Arming) return to == TcpV3RunState.Ready || to == TcpV3RunState.Error || to == TcpV3RunState.Off;
            if (from == TcpV3RunState.Ready) return to == TcpV3RunState.On || to == TcpV3RunState.Off || to == TcpV3RunState.Error;
            if (from == TcpV3RunState.On) return to == TcpV3RunState.Paused || to == TcpV3RunState.Error || to == TcpV3RunState.Off || to == TcpV3RunState.Disconnected;
            if (from == TcpV3RunState.Paused) return to == TcpV3RunState.On || to == TcpV3RunState.Off || to == TcpV3RunState.Error;
            return false;
        }

        public bool TryTransition(TcpV3RunState to, string reason, bool explicitReset, out string error)
        {
            lock (sync)
            {
                if (!CanTransition(state, to, explicitReset))
                {
                    error = "Invalid state transition " + state + " -> " + to + ". Reason: " + reason;
                    return false;
                }
                state = to;
                LastTransitionReason = reason;
                LastTransitionTime = DateTime.Now;
                error = string.Empty;
                return true;
            }
        }
    }
    #endregion

    #region Engine and modules
    public class TradeCopierV3Engine
    {
        private readonly object stateLock = new object();
        private readonly TradeCopierV3ViewModel vm;
        private readonly AccountSubscriptionManager subscriptions;
        private readonly QuantityCalculatorV3 quantityCalculator;
        private readonly RiskManagerV3 riskManager;
        private readonly SlippageEngineV3 slippageEngine;
        private readonly OrderRouterV3 orderRouter;
        private readonly AuditLoggerV3 audit;
        private readonly PositionSynchronizerV3 synchronizer;
        private readonly LatencyMonitorV3 latency;
        private readonly PreflightValidatorV3 preflight;
        private readonly SettingsRepositoryV3 settingsRepo;
        private readonly ConcurrentDictionary<string, DateTime> processedExecutions = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, TcpV3FollowerRuntime> runtimeByFollowerId = new ConcurrentDictionary<string, TcpV3FollowerRuntime>();
        private readonly ConcurrentDictionary<string, TcpV3FollowerOrderTrack> orderTracks = new ConcurrentDictionary<string, TcpV3FollowerOrderTrack>();
        private readonly System.Timers.Timer maintenanceTimer = new System.Timers.Timer(500);
        private readonly TcpV3StateMachine stateMachine = new TcpV3StateMachine();
        private int maintenanceBusy;
        private bool isShutdown;
        private bool lastPreflightSuccess;
        private string lastPreflightHash = string.Empty;
        private Account masterAccount;
        private NinjaTrader.Cbi.Instrument masterInstrument;
        private MarketPosition masterPosition = MarketPosition.Flat;
        private int masterQuantity;
        private bool subscribed;

        public TradeCopierV3Engine(TradeCopierV3ViewModel viewModel)
        {
            TcpV3Diagnostics.Info("Engine constructor start");
            vm = viewModel;
            quantityCalculator = new QuantityCalculatorV3();
            latency = new LatencyMonitorV3(vm);
            audit = new AuditLoggerV3(vm);
            riskManager = new RiskManagerV3();
            slippageEngine = new SlippageEngineV3();
            orderRouter = new OrderRouterV3(orderTracks, audit);
            synchronizer = new PositionSynchronizerV3(audit);
            preflight = new PreflightValidatorV3();
            settingsRepo = new SettingsRepositoryV3();
            subscriptions = new AccountSubscriptionManager(OnOrderUpdate, OnExecutionUpdate, OnPositionUpdate, audit);
            maintenanceTimer.Elapsed += OnMaintenance;
            maintenanceTimer.Start();
            LoadSettings();
            TcpV3Diagnostics.Info("Engine constructor finished");
        }

        public void LoadSettings()
        {
            vm.SetSettingsSafe(settingsRepo.Load());
            vm.RefreshAccounts();
            ResolveAccountsAndInstruments();
            EnsureRuntimeRows();
            vm.RebuildFollowerRows(runtimeByFollowerId.ToDictionary(k => k.Key, v => v.Value));
            audit.Configure(vm.Settings);
            audit.Start();
            vm.RefreshRouteSummarySafe();
            MarkConfigDirty("Settings loaded");
            vm.SetIsEngineInitializedSafe(true);
            SetState(TcpV3RunState.Off, "Settings loaded. Engine safe OFF.", true);
        }

        public void SaveSettings()
        {
            settingsRepo.Save(vm.Settings);
            Audit("SETTINGS", TcpV3AuditLevel.Info, string.Empty, string.Empty, "Settings saved");
            vm.SetLastActionSafe("Settings saved");
            MarkConfigDirty("Settings saved - preflight required");
        }

        public TcpV3PreflightResult RunPreflight()
        {
            ResolveAccountsAndInstruments();
            EnsureRuntimeRows();
            TcpV3PreflightResult result = preflight.Validate(vm.Settings, masterAccount, masterInstrument, runtimeByFollowerId, masterPosition);
            vm.SetPreflightReportSafe(result.ToString());
            string hash = ComputeConfigHash();
            vm.SetCurrentConfigHashSafe(hash);
            if (result.Success)
            {
                lastPreflightSuccess = true;
                lastPreflightHash = hash;
                vm.SetLastPreflightSuccessSafe(true);
                vm.SetLastPreflightHashSafe(hash);
                vm.SetIsConfigDirtySafe(false);
                RebuildSubscriptions();
            }
            else
            {
                lastPreflightSuccess = false;
                vm.SetLastPreflightSuccessSafe(false);
            }
            vm.RefreshRouteSummarySafe();
            Audit("PREFLIGHT", result.Success ? TcpV3AuditLevel.Info : TcpV3AuditLevel.Error, string.Empty, string.Empty, result.ToString());
            return result;
        }

        public void Arm()
        {
            lock (stateLock)
            {
                if (vm.RunState == TcpV3RunState.On) { Audit("ARM_BLOCK", TcpV3AuditLevel.Warning, string.Empty, string.Empty, "Cannot arm while copier is already ON. Disable first."); return; }
                if (vm.RunState == TcpV3RunState.KillSwitch) return;
                SetState(TcpV3RunState.Arming, "Arming requested", false);
                TcpV3PreflightResult r = RunPreflight();
                if (!r.Success) { SetState(TcpV3RunState.Error, "Preflight failed", false); return; }
                SetState(TcpV3RunState.Ready, "Preflight OK. Ready.", false);
            }
        }

        public void EnableCopy()
        {
            lock (stateLock)
            {
                if (vm.RunState != TcpV3RunState.Ready && vm.RunState != TcpV3RunState.Paused) return;
                SetState(TcpV3RunState.On, "Copy enabled", false);
            }
        }

        public void Pause() { lock (stateLock) if (vm.RunState == TcpV3RunState.On) SetState(TcpV3RunState.Paused, "Paused", false); }
        public void Disable() { lock (stateLock) SetState(TcpV3RunState.Off, "Copy disabled", true); }

        public void KillSwitch()
        {
            lock (stateLock)
            {
                SetState(TcpV3RunState.KillSwitch, "KILL SWITCH activated", false);
                if (vm.Settings.KillSwitchCancelWorkingOrders || vm.Settings.KillSwitchFlattenFollowers)
                    FlattenFollowers(vm.Settings.KillSwitchIncludeDisabledFollowers, vm.Settings.KillSwitchFlattenFollowers, vm.Settings.KillSwitchCancelWorkingOrders);
            }
        }

        public void ResetKillSwitch()
        {
            lock (stateLock)
            {
                if (vm.RunState != TcpV3RunState.KillSwitch) return;
                if (vm.Settings.KillSwitchAllowResetOnlyIfAllFlat && runtimeByFollowerId.Values.Any(r => r.ActualPosition != MarketPosition.Flat))
                {
                    Audit("KILL_RESET", TcpV3AuditLevel.Warning, string.Empty, string.Empty, "Reset blocked: not all followers flat");
                    return;
                }
                SetState(TcpV3RunState.Off, "Kill switch reset. State OFF. Preflight required.", true);
            }
        }

        public void FlattenAllFollowers() { FlattenFollowers(true, true, true); }
        public void FlattenOutOfSyncOnly()
        {
            foreach (TcpV3FollowerConfig f in vm.Settings.Followers)
            {
                TcpV3FollowerRuntime r;
                if (!runtimeByFollowerId.TryGetValue(f.Id, out r) || r.SyncStatus != TcpV3SyncStatus.OutOfSync) continue;
                FlattenFollower(f, true, true);
            }
        }

        public void AddFollower()
        {
            TcpV3FollowerConfig f = new TcpV3FollowerConfig { AccountName = "Sim101", InstrumentName = vm.Settings.MasterInstrumentName };
            vm.Settings.Followers.Add(f);
            runtimeByFollowerId[f.Id] = new TcpV3FollowerRuntime();
            vm.RebuildFollowerRows(runtimeByFollowerId.ToDictionary(k => k.Key, v => v.Value));
            vm.RefreshSummary();
            vm.RefreshRouteSummarySafe();
            MarkConfigDirty("Follower added");
        }

        public void RemoveSelected(TcpV3FollowerRowViewModel row)
        {
            if (row == null) return;
            vm.Settings.Followers.Remove(row.Config);
            TcpV3FollowerRuntime dummy;
            runtimeByFollowerId.TryRemove(row.Config.Id, out dummy);
            vm.RebuildFollowerRows(runtimeByFollowerId.ToDictionary(k => k.Key, v => v.Value));
            vm.RefreshRouteSummarySafe();
            MarkConfigDirty("Follower removed");
        }

        public void SetOperationMode(TcpV3OperationMode mode)
        {
            lock (stateLock)
            {
                if (vm.RunState == TcpV3RunState.KillSwitch) { Audit("MODE_CHANGE_BLOCK", TcpV3AuditLevel.Warning, string.Empty, string.Empty, "Cannot change operation mode while KillSwitch is active"); return; }
                if (vm.RunState == TcpV3RunState.On) { Audit("MODE_CHANGE_BLOCK", TcpV3AuditLevel.Warning, string.Empty, string.Empty, "Cannot change operation mode while copier is ON. Disable first."); return; }
                bool live = mode == TcpV3OperationMode.Live;
                if (vm.Settings != null) vm.Settings.DryRunMode = !live;
                vm.SetOperationModeSafe(mode); vm.NotifyDryRunChangedSafe(); MarkConfigDirty("Operation mode changed - preflight required");
                if (vm.RunState != TcpV3RunState.Off) SetState(TcpV3RunState.Off, "Operation mode changed. Preflight required.", true);
                Audit("MODE_CHANGE", live ? TcpV3AuditLevel.Warning : TcpV3AuditLevel.Info, string.Empty, string.Empty, "Operation mode changed to " + mode);
            }
        }
        public bool StartSafeCopy(bool enableAfterReady, out string message)
        {
            message=string.Empty;
            lock(stateLock)
            {
                Audit("START_SAFE_COPY_REQUEST", TcpV3AuditLevel.Info, string.Empty, string.Empty, "Main enable requested. Mode="+(vm.IsLiveMode?"LIVE":"TEST_RUN")+" State="+vm.RunState);
                if(vm.RunState==TcpV3RunState.On){message="Copy is already active.";return false;}
                if(vm.RunState==TcpV3RunState.KillSwitch){message="Kill Switch is active.";return false;}
                if(vm.RunState==TcpV3RunState.Paused){EnableCopy();Audit("START_SAFE_COPY_ENABLED",TcpV3AuditLevel.Info,string.Empty,string.Empty,"Copy resumed");return vm.RunState==TcpV3RunState.On;}
                SetState(TcpV3RunState.Arming,"Main enable - running preflight",false);
                TcpV3PreflightResult result=RunPreflight();
                if(!result.Success){Audit("START_SAFE_COPY_PREFLIGHT_FAILED",TcpV3AuditLevel.Warning,string.Empty,string.Empty,result.ToString());SetState(TcpV3RunState.Error,"Main enable preflight failed",false);message="Preflight failed. Check Preflight panel.";return false;}
                Audit("START_SAFE_COPY_PREFLIGHT_SUCCESS",TcpV3AuditLevel.Info,string.Empty,string.Empty,"Preflight success");
                SetState(TcpV3RunState.Ready,"Main enable armed and ready",false);
                Audit("START_SAFE_COPY_ARMED",TcpV3AuditLevel.Info,string.Empty,string.Empty,"Ready");
                if(enableAfterReady){EnableCopy(); if(vm.RunState==TcpV3RunState.On){Audit("START_SAFE_COPY_ENABLED",TcpV3AuditLevel.Warning,string.Empty,string.Empty,"Copy enabled from main toggle. Mode="+(vm.IsLiveMode?"LIVE":"TEST_RUN"));return true;}}
                message="Preflight passed. Copier is READY."; return true;
            }
        }

        public void RefreshAccounts()
        {
            vm.RefreshAccounts();
            vm.SetLastActionSafe("Accounts refreshed");
        }

        public void Shutdown()
        {
            lock (stateLock)
            {
                if (isShutdown) return;
                isShutdown = true;
            }
            try { SetState(TcpV3RunState.Off, "Shutdown", true); } catch { }
            try { subscriptions.UnsubscribeAll(); subscribed = false; } catch { }
            try { maintenanceTimer.Stop(); maintenanceTimer.Elapsed -= OnMaintenance; maintenanceTimer.Dispose(); } catch { }
            try { audit.Stop(); } catch { }
        }

        private void SetState(TcpV3RunState state, string reason, bool explicitReset)
        {
            string error;
            if (!stateMachine.TryTransition(state, reason, explicitReset, out error))
            {
                vm.SetLastErrorSafe(error);
                Audit("STATE_BLOCK", TcpV3AuditLevel.Warning, string.Empty, string.Empty, error);
                return;
            }
            vm.SetRunStateSafe(stateMachine.State);
            vm.SetLastActionSafe(reason);
            vm.SetStatusMessageSafe(reason);
            Audit("STATE", TcpV3AuditLevel.Info, string.Empty, string.Empty, state + " - " + reason);
        }

        private bool CanSubmitOrders(out string reason)
        {
            reason = string.Empty;
            if (isShutdown) { reason = "Engine shutdown"; return false; }
            if (vm == null || vm.Settings == null) { reason = "Settings unavailable"; return false; }
            if (!vm.Settings.GlobalTradingEnabled) { reason = "GlobalTradingEnabled=false"; return false; }
            if (vm.Settings.GlobalKillSwitch || vm.RunState == TcpV3RunState.KillSwitch) { reason = "Global/Kill switch active"; return false; }
            if (vm.RunState != TcpV3RunState.On) { reason = "RunState is not ON"; return false; }
            if (masterAccount == null) { reason = "Master account invalid"; return false; }
            if (masterInstrument == null) { reason = "Master instrument invalid"; return false; }
            if (!subscribed) { reason = "Account subscriptions inactive"; return false; }
            if (vm.Settings.RequirePreflightBeforeEnable && !lastPreflightSuccess) { reason = vm.IsConfigDirty ? "Configuration or operation mode changed after preflight. Run Preflight again." : "Preflight has not passed."; return false; }
            string currentHash = ComputeConfigHash();
            vm.SetCurrentConfigHashSafe(currentHash);
            if (vm.Settings.BlockTradingWhenConfigDirty && currentHash != lastPreflightHash) { reason = "Config changed after preflight"; return false; }
            return true;
        }

        private void MarkConfigDirty(string reason)
        {
            lastPreflightSuccess = false;
            vm.SetLastPreflightSuccessSafe(false);
            vm.SetIsConfigDirtySafe(true);
            vm.SetCurrentConfigHashSafe(ComputeConfigHash());
            vm.SetStatusMessageSafe(reason);
        }

        private string ComputeConfigHash()
        {
            if (vm == null || vm.Settings == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            TcpV3Settings s = vm.Settings;
            sb.Append(s.MasterAccountName).Append('|').Append(s.MasterInstrumentName).Append('|').Append(s.SlippageMode).Append('|').Append(s.GlobalMaxSlippageTicks).Append('|').Append(s.GlobalMaxOrderQuantity).Append('|').Append(s.GlobalMaxDailyContracts).Append('|').Append(s.GlobalTradingEnabled).Append('|').Append(s.GlobalKillSwitch).Append('|').Append(s.DryRunMode);
            foreach (TcpV3FollowerConfig f in s.Followers.OrderBy(x => x.Id))
                sb.Append("#").Append(f.Id).Append('|').Append(f.Enabled).Append('|').Append(f.AccountName).Append('|').Append(f.InstrumentName).Append('|').Append(f.QuantityMultiplier.ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.ContractConversionFactor.ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.RoundingMode).Append('|').Append(f.MaxOrderQuantity).Append('|').Append(f.MaxPositionSize).Append('|').Append(f.MaxDailyContracts).Append('|').Append(f.SlippageTicksLimit);
            return sb.ToString().GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        private void ResolveAccountsAndInstruments()
        {
            lock (Account.All)
                masterAccount = Account.All.FirstOrDefault(a => a.Name == vm.Settings.MasterAccountName);
            masterInstrument = !string.IsNullOrWhiteSpace(vm.Settings.MasterInstrumentName) ? NinjaTrader.Cbi.Instrument.GetInstrumentFuzzy(vm.Settings.MasterInstrumentName) : null;
            foreach (TcpV3FollowerConfig f in vm.Settings.Followers)
            {
                lock (Account.All)
                    f.AccountRef = Account.All.FirstOrDefault(a => a.Name == f.AccountName);
                f.InstrumentRef = !string.IsNullOrWhiteSpace(f.InstrumentName) ? NinjaTrader.Cbi.Instrument.GetInstrumentFuzzy(f.InstrumentName) : null;
            }
        }

        private void EnsureRuntimeRows()
        {
            foreach (TcpV3FollowerConfig f in vm.Settings.Followers)
                runtimeByFollowerId.GetOrAdd(f.Id, id => new TcpV3FollowerRuntime());
        }

        private void SubscribeIfNeeded()
        {
            if (subscribed) return;
            subscriptions.Subscribe(masterAccount, vm.Settings.Followers);
            subscribed = true;
        }

        public void RebuildSubscriptions()
        {
            subscriptions.UnsubscribeAll();
            subscribed = false;
            ResolveAccountsAndInstruments();
            SubscribeIfNeeded();
            Audit("SUBSCRIPTIONS", TcpV3AuditLevel.Info, string.Empty, string.Empty, "Subscriptions rebuilt");
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (vm.RunState != TcpV3RunState.On) return;
                if (e == null || e.Execution == null || e.Execution.Order == null) return;
                Order mo = e.Execution.Order;
                if (mo.Account != masterAccount || mo.Instrument != masterInstrument) return;
                if (mo.Name != null && mo.Name.StartsWith("TCPV3", StringComparison.OrdinalIgnoreCase)) return;
                if (vm.Settings.PartialFillMode == TcpV3PartialFillMode.WaitForFullFill && mo.OrderState != OrderState.Filled) return;
                string submitGateReason;
                if (!CanSubmitOrders(out submitGateReason))
                {
                    Audit("SUBMIT_GATE_BLOCK", TcpV3AuditLevel.Warning, string.Empty, string.Empty, submitGateReason);
                    return;
                }

                int execQty = e.Execution.Quantity;
                if (execQty <= 0) return;
                string key = BuildDuplicateKey(mo, e.Execution, execQty);
                if (!processedExecutions.TryAdd(key, DateTime.UtcNow))
                {
                    Audit("DUPLICATE", TcpV3AuditLevel.Debug, string.Empty, string.Empty, key);
                    return;
                }

                DateTime receive = DateTime.UtcNow;
                TcpV3CopyBatch b = new TcpV3CopyBatch
                {
                    BatchId = receive.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    MasterAccount = mo.Account.Name,
                    MasterInstrument = mo.Instrument.FullName,
                    MasterOrderId = mo.OrderId,
                    MasterExecutionId = SafeExecutionId(e.Execution),
                    MasterAction = mo.OrderAction,
                    MasterEntry = mo.OrderEntry,
                    MasterQuantity = execQty,
                    MasterPrice = e.Execution.Price,
                    MasterExecutionTime = e.Execution.Time,
                    ReceiveUtc = receive
                };

                foreach (TcpV3FollowerConfig f in vm.Settings.Followers.ToList())
                    RouteExecution(b, f, mo);
            }
            catch (Exception ex)
            {
                vm.SetLastErrorSafe(ex.Message);
                Audit("EXECUTION_EXCEPTION", TcpV3AuditLevel.Error, string.Empty, string.Empty, ex.Message);
                SetState(TcpV3RunState.Error, "Unhandled execution callback exception", false);
            }
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e == null || e.Order == null) return;
                Order o = e.Order;
                if (o.Name != null && o.Name.StartsWith("TCPV3", StringComparison.OrdinalIgnoreCase))
                {
                    TcpV3FollowerOrderTrack tr;
                    if (!string.IsNullOrWhiteSpace(o.OrderId) && orderTracks.TryGetValue(o.OrderId, out tr))
                    {
                        tr.State = e.OrderState;
                        UpdateRuntimeOrderState(tr, e.OrderState.ToString());
                    }
                    if (e.OrderState == OrderState.Rejected)
                        HandleFollowerRejection(o);
                }
            }
            catch (Exception ex) { Audit("ORDER_EXCEPTION", TcpV3AuditLevel.Error, string.Empty, string.Empty, ex.Message); }
        }

        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            try
            {
                if (e == null || e.Position == null) return;
                if (e.Position.Account == masterAccount && e.Position.Instrument == masterInstrument)
                {
                    masterPosition = e.MarketPosition;
                    masterQuantity = e.Quantity;
                    vm.SetMasterPositionSafe(masterPosition + " " + masterQuantity);
                    if (masterPosition == MarketPosition.Flat)
                        synchronizer.OnMasterFlat(DateTime.UtcNow);
                    return;
                }

                foreach (TcpV3FollowerConfig f in vm.Settings.Followers)
                {
                    if (f.AccountRef == e.Position.Account && f.InstrumentRef == e.Position.Instrument)
                    {
                        TcpV3FollowerRuntime r = runtimeByFollowerId.GetOrAdd(f.Id, id => new TcpV3FollowerRuntime());
                        r.ActualPosition = e.MarketPosition;
                        r.ActualQuantity = e.Quantity;
                        synchronizer.UpdateSync(f, r);
                        RefreshFollowerRows();
                        break;
                    }
                }
            }
            catch (Exception ex) { Audit("POSITION_EXCEPTION", TcpV3AuditLevel.Error, string.Empty, string.Empty, ex.Message); }
        }

        private void RouteExecution(TcpV3CopyBatch b, TcpV3FollowerConfig f, Order masterOrder)
        {
            TcpV3FollowerRuntime r = runtimeByFollowerId.GetOrAdd(f.Id, id => new TcpV3FollowerRuntime());
            if (!f.Enabled || r.DisabledByError || r.DisabledByRisk) return;
            if (f.AccountRef == null || f.InstrumentRef == null) { RegisterFollowerError(f, r, "Account or instrument unresolved"); return; }
            if (f.AccountRef == masterAccount && f.InstrumentRef == masterInstrument) { RegisterFollowerError(f, r, "Follower route equals master route"); return; }
            if (!ShouldCopyOrderType(f, masterOrder)) return;

            int qty = quantityCalculator.Calculate(b.MasterQuantity, f.QuantityMultiplier, f.ContractConversionFactor, f.RoundingMode, f.MinQuantity);
            if (qty <= 0) { Audit("QTY_SKIP", TcpV3AuditLevel.Info, b.BatchId, f.AccountName, "Quantity below one; skipped"); return; }

            RiskDecisionV3 risk = riskManager.Check(vm.Settings, f, r, b, qty);
            if (risk.Result == TcpV3RiskResult.Blocked || risk.Result == TcpV3RiskResult.Disabled)
            {
                r.DisabledByRisk = risk.Result == TcpV3RiskResult.Disabled;
                r.RuntimeState = TcpV3RuntimeState.RiskBlocked;
                Audit("RISK_BLOCK", TcpV3AuditLevel.Warning, b.BatchId, f.AccountName, risk.Reason);
                RefreshFollowerRows();
                return;
            }

            SlippageDecisionV3 slip = slippageEngine.Check(vm.Settings, f, b, 0);
            r.LastSlippageTicks = slip.EstimatedTicks;
            if (!slip.Allow)
            {
                Audit("SLIPPAGE_BLOCK", TcpV3AuditLevel.Warning, b.BatchId, f.AccountName, slip.Message);
                RefreshFollowerRows();
                return;
            }

            string gateReason;
            if (!CanSubmitOrders(out gateReason))
            {
                Audit("SUBMIT_GATE_BLOCK", TcpV3AuditLevel.Warning, b.BatchId, f.AccountName, gateReason);
                return;
            }

            DateTime start = DateTime.UtcNow;
            OrderType orderType = slip.ConvertToLimit ? OrderType.Limit : OrderType.Market;
            if (vm.Settings.DryRunMode)
            {
                r.LastSubmittedQuantity = qty;
                r.LastOrderState = "DRY_RUN";
                r.RuntimeState = TcpV3RuntimeState.Warning;
                Audit("DRY_RUN_SUBMIT", TcpV3AuditLevel.Warning, b.BatchId, f.AccountName, "TEST RUN ONLY - would submit " + b.MasterAction + " " + qty + " " + orderType + " " + f.InstrumentName + ". No real order was sent.");
                RefreshFollowerRows();
                return;
            }
            Audit("LIVE_SUBMIT_ATTEMPT", TcpV3AuditLevel.Warning, b.BatchId, f.AccountName, "LIVE MODE - submitting " + b.MasterAction + " " + qty + " " + orderType + " " + f.InstrumentName);
            TcpV3FollowerOrderTrack tr = orderRouter.Submit(b, f, b.MasterAction, b.MasterEntry, qty, orderType, slip.LimitPrice);
            double ms = (DateTime.UtcNow - start).TotalMilliseconds;
            latency.Add(f.Id, ms);
            r.CurrentLatencyMs = ms;
            r.AverageLatencyMs = latency.GetAverage(f.Id);
            r.LastSubmittedQuantity = qty;
            r.LastOrderState = tr != null ? "Submitted" : "SubmitFailed";
            r.RuntimeState = tr != null ? TcpV3RuntimeState.Healthy : TcpV3RuntimeState.Error;
            if (tr != null)
            {
                ResetDaily(r);
                r.DailyContracts += qty;
                r.DailyTrades++;
            }
            synchronizer.ApplyExpectedDelta(f, r, b.MasterAction, qty);
            RefreshFollowerRows();
        }

        private bool ShouldCopyOrderType(TcpV3FollowerConfig f, Order o)
        {
            if (o == null) return false;
            if (o.IsMarket) return f.CopyMarketOrders;
            if (o.IsLimit) return f.CopyLimitOrders;
            if (o.IsStopMarket || o.IsStopLimit) return f.CopyStopOrders;
            return true;
        }

        private void HandleFollowerRejection(Order o)
        {
            TcpV3FollowerConfig f = vm.Settings.Followers.FirstOrDefault(x => x.AccountRef == o.Account && x.InstrumentRef == o.Instrument);
            if (f == null) return;
            TcpV3FollowerRuntime r = runtimeByFollowerId.GetOrAdd(f.Id, id => new TcpV3FollowerRuntime());
            r.RejectCount++;
            RegisterFollowerError(f, r, "Order rejected: " + o.OrderId);
        }

        private void UpdateRuntimeOrderState(TcpV3FollowerOrderTrack tr, string state)
        {
            TcpV3FollowerConfig f = vm.Settings.Followers.FirstOrDefault(x => x.AccountName == tr.FollowerAccount && x.InstrumentName == tr.FollowerInstrument);
            if (f == null) return;
            TcpV3FollowerRuntime r = runtimeByFollowerId.GetOrAdd(f.Id, id => new TcpV3FollowerRuntime());
            r.LastOrderState = state;
            RefreshFollowerRows();
        }

        private void RegisterFollowerError(TcpV3FollowerConfig f, TcpV3FollowerRuntime r, string msg)
        {
            r.ErrorCount++;
            r.LastError = msg;
            r.RuntimeState = TcpV3RuntimeState.Error;
            if (f.DisableAfterErrorCount > 0 && r.ErrorCount >= f.DisableAfterErrorCount)
            {
                r.DisabledByError = true;
                r.RuntimeState = TcpV3RuntimeState.Disabled;
            }
            Audit("FOLLOWER_ERROR", TcpV3AuditLevel.Error, string.Empty, f.AccountName, msg);
            RefreshFollowerRows();
        }

        private void FlattenFollowers(bool includeDisabled, bool flatten, bool cancel)
        {
            foreach (TcpV3FollowerConfig f in vm.Settings.Followers)
            {
                if (!includeDisabled && !f.Enabled) continue;
                FlattenFollower(f, flatten, cancel);
            }
        }

        private void FlattenFollower(TcpV3FollowerConfig f, bool flatten, bool cancel)
        {
            if (f.AccountRef == null || f.InstrumentRef == null) return;
            try
            {
                if (cancel) f.AccountRef.CancelAllOrders(f.InstrumentRef);
                if (flatten) f.AccountRef.Flatten(new[] { f.InstrumentRef });
                Audit("FLATTEN", TcpV3AuditLevel.Warning, string.Empty, f.AccountName, "Cancel=" + cancel + " Flatten=" + flatten);
            }
            catch (Exception ex) { Audit("FLATTEN_EXCEPTION", TcpV3AuditLevel.Error, string.Empty, f.AccountName, ex.Message); }
        }

        private void OnMaintenance(object sender, ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref maintenanceBusy, 1) == 1) return;
            try
            {
                CleanupDuplicateKeys();
                audit.Flush(false);
                if (vm.RunState == TcpV3RunState.On)
                    synchronizer.CheckMasterFlatTimeout(vm.Settings, vm.Settings.Followers, runtimeByFollowerId, FlattenFollower);
                latency.UpdateSummary();
            }
            finally { Interlocked.Exchange(ref maintenanceBusy, 0); }
        }

        private void CleanupDuplicateKeys()
        {
            DateTime cut = DateTime.UtcNow.AddSeconds(-Math.Max(60, vm.Settings.DuplicateTtlSeconds));
            foreach (KeyValuePair<string, DateTime> kv in processedExecutions.ToArray())
            {
                if (kv.Value < cut)
                {
                    DateTime old;
                    processedExecutions.TryRemove(kv.Key, out old);
                }
            }
        }

        private void RefreshFollowerRows()
        {
            vm.RebuildFollowerRows(runtimeByFollowerId.ToDictionary(k => k.Key, v => v.Value));
        }

        private void ResetDaily(TcpV3FollowerRuntime r)
        {
            DateTime now = DateTime.Now;
            int reset = vm != null && vm.Settings != null ? vm.Settings.DailyResetTime : 0;
            int hh = Math.Max(0, Math.Min(23, reset / 10000));
            int mm = Math.Max(0, Math.Min(59, (reset / 100) % 100));
            int ss = Math.Max(0, Math.Min(59, reset % 100));
            DateTime boundary = now.Date.AddHours(hh).AddMinutes(mm).AddSeconds(ss);
            if (now < boundary) boundary = boundary.AddDays(-1);
            if (r.LastResetDate >= boundary) return;
            r.LastResetDate = now;
            r.DailyContracts = 0;
            r.DailyTrades = 0;
        }

        private void Audit(string type, TcpV3AuditLevel level, string batchId, string account, string msg)
        {
            audit.Enqueue(new TcpV3AuditEvent { TimestampUtc = DateTime.UtcNow, LocalTime = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), EventType = type, Severity = level, BatchId = batchId, Account = account, Instrument = string.Empty, Message = msg });
        }

        private string SafeExecutionId(Execution ex)
        {
            if (ex == null) return Guid.NewGuid().ToString("N");
            if (!string.IsNullOrWhiteSpace(ex.ExecutionId)) return ex.ExecutionId;
            return (ex.Order == null ? "NOORDER" : ex.Order.OrderId) + "-" + ex.Time.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        }

        private string BuildDuplicateKey(Order o, Execution ex, int qty)
        {
            return (o.Account == null ? "" : o.Account.Name) + "|" + (o.Instrument == null ? "" : o.Instrument.FullName) + "|" + o.OrderId + "|" + SafeExecutionId(ex) + "|" + ex.Time.ToString("O", CultureInfo.InvariantCulture) + "|" + o.OrderAction + "|" + qty + "|" + ex.Price.ToString(CultureInfo.InvariantCulture);
        }
    }

    public class AccountSubscriptionManager
    {
        private readonly object sync = new object();
        private readonly HashSet<Account> subscribed = new HashSet<Account>();
        private readonly EventHandler<OrderEventArgs> orderHandler;
        private readonly EventHandler<ExecutionEventArgs> execHandler;
        private readonly EventHandler<PositionEventArgs> posHandler;
        private readonly AuditLoggerV3 audit;
        public AccountSubscriptionManager(EventHandler<OrderEventArgs> oh, EventHandler<ExecutionEventArgs> eh, EventHandler<PositionEventArgs> ph, AuditLoggerV3 logger) { orderHandler = oh; execHandler = eh; posHandler = ph; audit = logger; }
        public void Subscribe(Account master, IEnumerable<TcpV3FollowerConfig> followers)
        {
            lock (sync)
            {
                SubscribeOne(master);
                foreach (TcpV3FollowerConfig f in followers) SubscribeOne(f.AccountRef);
            }
        }
        private void SubscribeOne(Account a)
        {
            if (a == null || subscribed.Contains(a)) return;
            a.OrderUpdate += orderHandler;
            a.ExecutionUpdate += execHandler;
            a.PositionUpdate += posHandler;
            subscribed.Add(a);
        }
        public void UnsubscribeAll()
        {
            lock (sync)
            {
                foreach (Account a in subscribed.ToArray())
                {
                    try { a.OrderUpdate -= orderHandler; a.ExecutionUpdate -= execHandler; a.PositionUpdate -= posHandler; } catch { }
                }
                subscribed.Clear();
            }
        }
    }

    public class QuantityCalculatorV3
    {
        public int Calculate(int masterQty, double multiplier, double conversion, TcpV3QuantityRoundingMode mode, int minQty)
        {
            if (masterQty <= 0 || multiplier <= 0 || conversion <= 0) return 0;
            double raw = masterQty * multiplier * conversion;
            int q;
            switch (mode)
            {
                case TcpV3QuantityRoundingMode.Floor: q = (int)Math.Floor(raw); break;
                case TcpV3QuantityRoundingMode.Ceiling: q = (int)Math.Ceiling(raw); break;
                case TcpV3QuantityRoundingMode.RoundNearest: q = (int)Math.Round(raw, MidpointRounding.AwayFromZero); break;
                case TcpV3QuantityRoundingMode.SkipIfBelowOne: q = raw < 1.0 ? 0 : (int)Math.Floor(raw); break;
                default: q = Math.Max(1, (int)Math.Round(raw, MidpointRounding.AwayFromZero)); break;
            }
            if (q > 0 && minQty > 0) q = Math.Max(q, minQty);
            return Math.Max(0, q);
        }
    }

    public class RiskDecisionV3 { public TcpV3RiskResult Result; public string Reason; }
    public class RiskManagerV3
    {
        public RiskDecisionV3 Check(TcpV3Settings s, TcpV3FollowerConfig f, TcpV3FollowerRuntime r, TcpV3CopyBatch b, int qty)
        {
            if (qty <= 0) return Block("InvalidQuantity");
            if (s.GlobalMaxOrderQuantity > 0 && qty > s.GlobalMaxOrderQuantity) return Block("GlobalMaxOrderQuantity exceeded");
            if (f.MaxOrderQuantity > 0 && qty > f.MaxOrderQuantity) return Block("Follower MaxOrderQuantity exceeded");
            if (s.GlobalMaxDailyContracts > 0 && r.DailyContracts + qty > s.GlobalMaxDailyContracts) return Block("GlobalMaxDailyContracts exceeded");
            if (f.MaxDailyContracts > 0 && r.DailyContracts + qty > f.MaxDailyContracts) return Block("Follower MaxDailyContracts exceeded");
            if (s.GlobalMaxTradesPerDay > 0 && r.DailyTrades + 1 > s.GlobalMaxTradesPerDay) return Block("GlobalMaxTradesPerDay exceeded");
            if (f.MaxTradesPerDay > 0 && r.DailyTrades + 1 > f.MaxTradesPerDay) return Block("Follower MaxTradesPerDay exceeded");
            if (f.MaxPositionSize > 0 && ProjectedAbs(r.ActualPosition, r.ActualQuantity, b.MasterAction, qty) > f.MaxPositionSize) return Block("MaxPositionSize exceeded");
            if (f.BlockNewTradeIfFollowerNotFlat && r.ActualPosition != MarketPosition.Flat) return Block("Follower not flat");
            if (f.BlockOppositeDirectionIfOutOfSync && r.SyncStatus == TcpV3SyncStatus.OutOfSync) return Block("Follower out-of-sync");
            return new RiskDecisionV3 { Result = TcpV3RiskResult.Allowed, Reason = "Allowed" };
        }
        private RiskDecisionV3 Block(string reason) { return new RiskDecisionV3 { Result = TcpV3RiskResult.Blocked, Reason = reason }; }
        private int ProjectedAbs(MarketPosition pos, int cur, OrderAction action, int qty) { int s = pos == MarketPosition.Long ? cur : pos == MarketPosition.Short ? -cur : 0; if (action == OrderAction.Buy || action == OrderAction.BuyToCover) s += qty; else s -= qty; return Math.Abs(s); }
    }

    public class SlippageDecisionV3 { public bool Allow; public bool ConvertToLimit; public double LimitPrice; public double EstimatedTicks; public string Message; }
    public class SlippageEngineV3
    {
        public SlippageDecisionV3 Check(TcpV3Settings s, TcpV3FollowerConfig f, TcpV3CopyBatch b, double followerReferencePrice)
        {
            SlippageDecisionV3 d = new SlippageDecisionV3 { Allow = true, ConvertToLimit = false, Message = "OK" };
            if (s.SlippageMode == TcpV3SlippageMode.Off || b.MasterPrice <= 0 || f.InstrumentRef == null) return d;
            if (followerReferencePrice <= 0)
            {
                d.Message = "No reliable quote available";
                if ((s.StrictSlippageRequiresQuote || s.BlockIfQuoteUnavailable) && s.SlippageMode == TcpV3SlippageMode.BlockOrder)
                    d.Allow = false;
                return d;
            }
            double tick = f.InstrumentRef.MasterInstrument != null ? f.InstrumentRef.MasterInstrument.TickSize : 0.25;
            if (tick <= 0) tick = 0.25;
            d.EstimatedTicks = (b.MasterAction == OrderAction.Buy || b.MasterAction == OrderAction.BuyToCover) ? (followerReferencePrice - b.MasterPrice) / tick : (b.MasterPrice - followerReferencePrice) / tick;
            int allowed = f.SlippageTicksLimit > 0 ? f.SlippageTicksLimit : s.GlobalMaxSlippageTicks;
            if (allowed <= 0 || d.EstimatedTicks <= allowed) return d;
            if (s.SlippageMode == TcpV3SlippageMode.WarnOnly) return d;
            if (s.SlippageMode == TcpV3SlippageMode.BlockOrder) { d.Allow = false; d.Message = "Slippage " + d.EstimatedTicks.ToString("0.##") + " > " + allowed; return d; }
            d.ConvertToLimit = true;
            d.LimitPrice = (b.MasterAction == OrderAction.Buy || b.MasterAction == OrderAction.BuyToCover) ? followerReferencePrice + allowed * tick : followerReferencePrice - allowed * tick;
            d.LimitPrice = f.InstrumentRef.MasterInstrument.RoundToTickSize(d.LimitPrice);
            return d;
        }
    }

    public class OrderRouterV3
    {
        private readonly ConcurrentDictionary<string, TcpV3FollowerOrderTrack> tracks;
        private readonly AuditLoggerV3 audit;
        public OrderRouterV3(ConcurrentDictionary<string, TcpV3FollowerOrderTrack> t, AuditLoggerV3 a) { tracks = t; audit = a; }
        public TcpV3FollowerOrderTrack Submit(TcpV3CopyBatch b, TcpV3FollowerConfig f, OrderAction action, OrderEntry entry, int qty, OrderType type, double limitPrice)
        {
            try
            {
                Order order = f.AccountRef.CreateOrder(f.InstrumentRef, action, type, entry, TimeInForce.Day, qty, limitPrice, 0, string.Empty, "TCPV3-" + b.BatchId, Core.Globals.MaxDate, null);
                TcpV3FollowerOrderTrack tr = new TcpV3FollowerOrderTrack { BatchId = b.BatchId, MasterExecutionId = b.MasterExecutionId, FollowerAccount = f.AccountName, FollowerInstrument = f.InstrumentName, OrderId = order.OrderId, Action = action, OrderType = type, Quantity = qty, LimitPrice = limitPrice, SubmitUtc = DateTime.UtcNow, State = OrderState.Unknown };
                if (!string.IsNullOrWhiteSpace(order.OrderId)) tracks[order.OrderId] = tr;
                f.AccountRef.Submit(new[] { order });
                audit.Enqueue(new TcpV3AuditEvent { TimestampUtc = DateTime.UtcNow, LocalTime = DateTime.Now.ToString("HH:mm:ss.fff"), EventType = "SUBMIT", Severity = TcpV3AuditLevel.Info, BatchId = b.BatchId, Account = f.AccountName, Instrument = f.InstrumentName, Message = action + " " + qty + " " + type });
                return tr;
            }
            catch (Exception ex)
            {
                audit.Enqueue(new TcpV3AuditEvent { TimestampUtc = DateTime.UtcNow, LocalTime = DateTime.Now.ToString("HH:mm:ss.fff"), EventType = "SUBMIT_EXCEPTION", Severity = TcpV3AuditLevel.Error, BatchId = b.BatchId, Account = f.AccountName, Instrument = f.InstrumentName, Message = ex.Message });
                return null;
            }
        }
    }

    public class PositionSynchronizerV3
    {
        private readonly AuditLoggerV3 audit;
        private DateTime masterFlatUtc = DateTime.MinValue;
        public PositionSynchronizerV3(AuditLoggerV3 a) { audit = a; }
        public void OnMasterFlat(DateTime utc) { masterFlatUtc = utc; }
        public void ApplyExpectedDelta(TcpV3FollowerConfig f, TcpV3FollowerRuntime r, OrderAction action, int qty)
        {
            int signed = r.ExpectedPosition == MarketPosition.Long ? r.ExpectedQuantity : r.ExpectedPosition == MarketPosition.Short ? -r.ExpectedQuantity : 0;
            if (action == OrderAction.Buy || action == OrderAction.BuyToCover) signed += qty; else signed -= qty;
            r.ExpectedQuantity = Math.Abs(signed);
            r.ExpectedPosition = signed > 0 ? MarketPosition.Long : signed < 0 ? MarketPosition.Short : MarketPosition.Flat;
            UpdateSync(f, r);
        }
        public void UpdateSync(TcpV3FollowerConfig f, TcpV3FollowerRuntime r)
        {
            if (r.ActualPosition == r.ExpectedPosition && r.ActualQuantity == r.ExpectedQuantity) { r.SyncStatus = TcpV3SyncStatus.InSync; if (r.RuntimeState != TcpV3RuntimeState.Disabled && r.RuntimeState != TcpV3RuntimeState.RiskBlocked) r.RuntimeState = TcpV3RuntimeState.Healthy; }
            else { r.SyncStatus = TcpV3SyncStatus.OutOfSync; r.RuntimeState = TcpV3RuntimeState.OutOfSync; }
        }
        public void CheckMasterFlatTimeout(TcpV3Settings s, IEnumerable<TcpV3FollowerConfig> followers, ConcurrentDictionary<string, TcpV3FollowerRuntime> runtime, Action<TcpV3FollowerConfig, bool, bool> flatten)
        {
            if (masterFlatUtc == DateTime.MinValue || (DateTime.UtcNow - masterFlatUtc).TotalMilliseconds < s.MasterFlatReconcileDelayMs) return;
            foreach (TcpV3FollowerConfig f in followers)
            {
                TcpV3FollowerRuntime r;
                if (!runtime.TryGetValue(f.Id, out r)) continue;
                if (f.FlattenOnMasterFlat && r.ActualPosition != MarketPosition.Flat)
                    flatten(f, true, true);
            }
            masterFlatUtc = DateTime.MinValue;
        }
    }

    public class LatencyMonitorV3
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> samples = new ConcurrentDictionary<string, ConcurrentQueue<double>>();
        private readonly TradeCopierV3ViewModel vm;
        public LatencyMonitorV3(TradeCopierV3ViewModel v) { vm = v; }
        public void Add(string followerId, double ms)
        {
            ConcurrentQueue<double> q = samples.GetOrAdd(followerId, id => new ConcurrentQueue<double>());
            q.Enqueue(ms);
            while (q.Count > 500) { double old; q.TryDequeue(out old); }
        }
        public double GetAverage(string followerId)
        {
            ConcurrentQueue<double> q; if (!samples.TryGetValue(followerId, out q) || q.Count == 0) return 0;
            return q.ToArray().Average();
        }
        public void UpdateSummary()
        {
            double[] all = samples.Values.SelectMany(x => x.ToArray()).ToArray();
            if (all.Length == 0) return;
            vm.SetAverageLatencySafe(all.Average());
            vm.SetWorstLatencySafe(all.Max());
        }
    }

    public class PreflightValidatorV3
    {
        public TcpV3PreflightResult Validate(TcpV3Settings s, Account master, NinjaTrader.Cbi.Instrument masterInstrument, ConcurrentDictionary<string, TcpV3FollowerRuntime> runtimes, MarketPosition masterPosition)
        {
            TcpV3PreflightResult r = new TcpV3PreflightResult();
            if (master == null) Error(r, "Master account not found");
            if (masterInstrument == null) Error(r, "Master instrument invalid");
            if (s.Followers == null || s.Followers.Count(x => x.Enabled) == 0) Error(r, "No enabled follower");
            HashSet<string> routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TcpV3FollowerConfig f in s.Followers)
            {
                if (!f.Enabled) continue;
                if (f.AccountRef == null) Error(r, "Follower account not found: " + f.AccountName);
                if (f.InstrumentRef == null) Error(r, "Follower instrument invalid: " + f.InstrumentName);
                if (master != null && f.AccountRef == master && f.InstrumentRef == masterInstrument) Error(r, "Follower equals master route: " + f.AccountName + " " + f.InstrumentName);
                string route = (f.AccountName ?? "") + "|" + (f.InstrumentName ?? "");
                if (!routes.Add(route)) Error(r, "Duplicate follower route: " + route);
                if (f.QuantityMultiplier <= 0 || f.ContractConversionFactor <= 0) Error(r, "Invalid multiplier/conversion: " + route);
                TcpV3FollowerRuntime rt;
                if (s.RequireAllFollowersFlatBeforeEnable && runtimes.TryGetValue(f.Id, out rt) && rt.ActualPosition != MarketPosition.Flat) Error(r, "Follower not flat: " + route);
                if (runtimes.TryGetValue(f.Id, out rt) && (rt.DisabledByError || rt.DisabledByRisk)) Error(r, "Follower enabled but disabled by runtime error/risk: " + route);
            }
            if (s.RequireMasterFlatBeforeEnable && masterPosition != MarketPosition.Flat) Error(r, "Master not flat");
            r.Success = r.ErrorCount == 0;
            r.Lines.Insert(0, r.Success ? "SUCCESS" : "FAILURE");
            return r;
        }
        private void Error(TcpV3PreflightResult r, string s) { r.ErrorCount++; r.Lines.Add("ERROR: " + s); }
    }

    public class AuditLoggerV3
    {
        private readonly TradeCopierV3ViewModel vm;
        private readonly ConcurrentQueue<TcpV3AuditEvent> queue = new ConcurrentQueue<TcpV3AuditEvent>();
        private StreamWriter writer;
        private TcpV3Settings settings;
        public AuditLoggerV3(TradeCopierV3ViewModel v) { vm = v; }
        public void Configure(TcpV3Settings s) { settings = s; }
        public void Start()
        {
            if (settings == null || !settings.EnableCsvAudit) return;
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "TradeCopierProEnterpriseV3");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, string.IsNullOrWhiteSpace(settings.AuditFileName) ? "TradeCopierProEnterpriseV3_Audit.csv" : settings.AuditFileName);
                bool exists = File.Exists(file);
                writer = new StreamWriter(file, true, Encoding.UTF8);
                if (!exists) writer.WriteLine("TimestampUtc,LocalTime,EventType,Severity,BatchId,Account,Instrument,Message");
            }
            catch { writer = null; }
        }
        public void Stop() { Flush(true); if (writer != null) { writer.Dispose(); writer = null; } }
        public void Enqueue(TcpV3AuditEvent ev)
        {
            queue.Enqueue(ev);
            vm.AddEvent(ev);
            try
            {
                if (ev.Severity <= TcpV3AuditLevel.Warning)
                    NinjaTrader.Code.Output.Process("TCPV3 " + ev.EventType + " " + ev.Message, PrintTo.OutputTab1);
            }
            catch { }
        }
        public void Flush(bool force)
        {
            if (writer == null) return;
            int n = 0; TcpV3AuditEvent ev;
            while (queue.TryDequeue(out ev))
            {
                writer.WriteLine(ev.CsvLine());
                n++;
                if (!force && n >= 250) break;
            }
            if (force || n > 0) writer.Flush();
        }
    }

    public class SettingsRepositoryV3
    {
        private string Folder { get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "TradeCopierProEnterpriseV3"); } }
        private string FilePath { get { return Path.Combine(Folder, "TradeCopierProEnterpriseV3_Settings.xml"); } }
        public TcpV3Settings Load()
        {
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                if (!File.Exists(FilePath)) return CreateDefault();
                XmlSerializer xs = new XmlSerializer(typeof(TcpV3Settings));
                using (FileStream fs = File.OpenRead(FilePath)) return (TcpV3Settings)xs.Deserialize(fs);
            }
            catch { try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt_" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { } return CreateDefault(); }
        }
        public void Save(TcpV3Settings s)
        {
            if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
            XmlSerializer xs = new XmlSerializer(typeof(TcpV3Settings));
            string tmp = FilePath + ".tmp";
            string bak = FilePath + ".bak";
            using (FileStream fs = File.Create(tmp)) xs.Serialize(fs, s);
            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, bak, true); } catch { }
                File.Delete(FilePath);
            }
            File.Move(tmp, FilePath);
        }
        private TcpV3Settings CreateDefault()
        {
            TcpV3Settings s = new TcpV3Settings();
            s.Followers.Add(new TcpV3FollowerConfig { AccountName = "Sim101", InstrumentName = "MES 09-26", QuantityMultiplier = 1, ContractConversionFactor = 1 });
            return s;
        }
    }
    #endregion

    #region UI Theme
    public static class TcpV3Theme
    {
        public static readonly Brush BackgroundMain=new SolidColorBrush(Color.FromRgb(0xF4,0xF6,0xF8));
        public static readonly Brush HeaderBackground=new SolidColorBrush(Color.FromRgb(0x11,0x18,0x27));
        public static readonly Brush SidebarBackground=new SolidColorBrush(Color.FromRgb(0x1F,0x29,0x37));
        public static readonly Brush SidebarSelected=new SolidColorBrush(Color.FromRgb(0x37,0x41,0x51));
        public static readonly Brush TextPrimary=new SolidColorBrush(Color.FromRgb(0x11,0x18,0x27));
        public static readonly Brush TextSecondary=new SolidColorBrush(Color.FromRgb(0x6B,0x72,0x80));
        public static readonly Brush Border=new SolidColorBrush(Color.FromRgb(0xE5,0xE7,0xEB));
        public static readonly Brush Info=new SolidColorBrush(Color.FromRgb(0x25,0x63,0xEB));
        public static readonly Brush Warning=new SolidColorBrush(Color.FromRgb(0xF5,0x9E,0x0B));
        public static readonly Brush Neutral=new SolidColorBrush(Color.FromRgb(0x6B,0x72,0x80));
        public static readonly Brush Danger=new SolidColorBrush(Color.FromRgb(0xDC,0x26,0x26));
        public static readonly Brush Critical=new SolidColorBrush(Color.FromRgb(0x7F,0x1D,0x1D));
    }
    #endregion

    #region WPF Control Center
    public class TradeCopierV3ControlCenter : NTWindow
    {
        private TradeCopierV3ViewModel vm; private TradeCopierV3Engine engine; private DataGrid followerGrid; private ContentControl mainContent; private readonly Dictionary<string,Button> navButtons=new Dictionary<string,Button>(); private Dictionary<string,Func<UIElement>> pageBuilders; private DispatcherTimer liveBlinkTimer; private Border modeBadgeBorder; private bool blinkState;
        public TradeCopierV3ControlCenter(){ Caption="TradeCopier Pro Enterprise - Control Center"; Title="TradeCopier Pro Enterprise - Control Center"; Width=1480; Height=900; MinWidth=1050; MinHeight=680; Background=TcpV3Theme.BackgroundMain; Loaded+=(s,e)=>StartLiveBlinkTimer(); Closed+=(s,e)=>StopLiveBlinkTimer(); try{var services=TradeCopierV3Services.Instance; bool ok=services.Initialize(); vm=services.ViewModel; engine=services.Engine; if(vm!=null)vm.AttachDispatcher(this.Dispatcher); DataContext=vm; Content=(!ok||engine==null)?BuildFallbackContent(services.InitializationError,true):BuildLayout();}catch(Exception ex){TcpV3Diagnostics.Error("Control Center constructor failed",ex); Content=BuildFallbackContent(ex,true);} }
        private UIElement BuildFallbackContent(Exception ex,bool serviceFailure){DockPanel r=new DockPanel{Margin=new Thickness(12)}; r.Children.Add(new TextBlock{Text=serviceFailure?"TradeCopier engine failed to initialize":"TradeCopier UI failed to load",Foreground=Brushes.White,Background=Brushes.DarkRed,FontSize=20,FontWeight=FontWeights.Bold,Padding=new Thickness(10)}); r.Children.Add(new TextBox{Text=ex==null?"Unknown error":ex.ToString(),IsReadOnly=true,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}); return r;}
        private UIElement BuildLayout(){DockPanel root=new DockPanel{Background=TcpV3Theme.BackgroundMain}; UIElement h=BuildHeader(); DockPanel.SetDock(h,Dock.Top); root.Children.Add(h); UIElement st=BuildStatusBar(); DockPanel.SetDock(st,Dock.Bottom); root.Children.Add(st); pageBuilders=new Dictionary<string,Func<UIElement>>{{"dashboard",()=>BuildDashboardPanel()},{"quickstart",()=>BuildQuickStartPanel()},{"master",()=>BuildMasterPanel()},{"followers",()=>BuildFollowersPanel()},{"risk",()=>BuildRiskPanel()},{"slippage",()=>BuildSlippagePanel()},{"preflight",()=>BuildPreflightPanel()},{"logs",()=>BuildEventLog()},{"contact",()=>BuildContactPanel()},{"settings",()=>BuildSettingsPanel()}}; Grid center=new Grid(); center.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(230)}); center.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)}); UIElement side=BuildSidebar(); Grid.SetColumn(side,0); center.Children.Add(side); mainContent=new ContentControl{Margin=new Thickness(12)}; Border cb=new Border{Background=Brushes.White,BorderBrush=TcpV3Theme.Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Child=mainContent}; Grid.SetColumn(cb,1); center.Children.Add(cb); root.Children.Add(center); NavigateTo("dashboard"); return root;}
        private UIElement BuildSidebar(){Border b=new Border{Background=TcpV3Theme.SidebarBackground,Padding=new Thickness(10)}; StackPanel p=new StackPanel(); p.Children.Add(new TextBlock{Text="Navigation",Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=15,Margin=new Thickness(8,8,8,14)}); string[,] items={{"Dashboard","dashboard"},{"Quick Start","quickstart"},{"Master Setup","master"},{"Followers","followers"},{"Risk & Safety","risk"},{"Slippage","slippage"},{"Preflight Check","preflight"},{"Event Log","logs"},{"Contact & Support","contact"},{"Settings","settings"}}; for(int i=0;i<items.GetLength(0);i++)p.Children.Add(BuildNavButton(items[i,0],items[i,1])); b.Child=p; return b;}
        private Button BuildNavButton(string title,string key){Button b=new Button{Content=title,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,2,0,2),Background=TcpV3Theme.SidebarBackground,Foreground=new SolidColorBrush(Color.FromRgb(0xD1,0xD5,0xDB)),BorderBrush=TcpV3Theme.SidebarBackground,FontWeight=FontWeights.SemiBold}; b.Click+=(s,e)=>NavigateTo(key); navButtons[key]=b; return b;}
        private void NavigateTo(string key){if(mainContent==null||pageBuilders==null||!pageBuilders.ContainsKey(key))return; foreach(var kv in navButtons){bool sel=kv.Key==key; kv.Value.Background=sel?TcpV3Theme.SidebarSelected:TcpV3Theme.SidebarBackground; kv.Value.Foreground=sel?Brushes.White:new SolidColorBrush(Color.FromRgb(0xD1,0xD5,0xDB));} mainContent.Content=pageBuilders[key]();}
        private UIElement BuildHeader(){Grid g=new Grid{Height=98,Background=TcpV3Theme.HeaderBackground}; g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(390)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto}); StackPanel l=new StackPanel{Margin=new Thickness(16,10,16,8)}; l.Children.Add(new TextBlock{Text="TradeCopier Pro Enterprise",Foreground=Brushes.White,FontSize=22,FontWeight=FontWeights.Bold}); l.Children.Add(new TextBlock{Text="Execution-based multi-account copier",Foreground=new SolidColorBrush(Color.FromRgb(0xD1,0xD5,0xDB)),FontSize=12}); Grid.SetColumn(l,0); g.Children.Add(l); StackPanel m=new StackPanel{Margin=new Thickness(8,12,8,8)}; TextBlock mr=new TextBlock{Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,FontSize=13}; mr.SetBinding(TextBlock.TextProperty,new Binding("MasterRouteText")); m.Children.Add(mr); TextBlock r=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(0xD1,0xD5,0xDB)),FontSize=12}; r.SetBinding(TextBlock.TextProperty,new Binding("CopyDirectionText")); r.SetBinding(TextBlock.ToolTipProperty,new Binding("RouteTooltipText")); m.Children.Add(r); TextBlock fs=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(0xD1,0xD5,0xDB)),FontSize=12}; fs.SetBinding(TextBlock.TextProperty,new Binding("FollowerRouteSummaryText")); fs.SetBinding(TextBlock.ToolTipProperty,new Binding("RouteTooltipText")); m.Children.Add(fs); Grid.SetColumn(m,1); g.Children.Add(m); StackPanel right=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,16,0)}; modeBadgeBorder=ModeBadge(); right.Children.Add(modeBadgeBorder); right.Children.Add(HeaderText("RunStateText","State: {0}")); right.Children.Add(HeaderText("LastPreflightSuccess","Preflight: {0}")); right.Children.Add(HeaderText("IsConfigDirty","Dirty: {0}")); Grid.SetColumn(right,2); g.Children.Add(right); return g;}
        private TextBlock HeaderText(string bind,string fmt){TextBlock t=new TextBlock{Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center}; t.SetBinding(TextBlock.TextProperty,new Binding(bind){StringFormat=fmt}); return t;}
        private Border ModeBadge(){bool live=vm!=null&&vm.IsLiveMode; return new Border{Background=live?TcpV3Theme.Critical:TcpV3Theme.Warning,CornerRadius=new CornerRadius(12),Padding=new Thickness(12,5,12,5),Child=new TextBlock{Text=live?"LIVE MODE":"TEST RUN",Foreground=Brushes.White,FontWeight=FontWeights.Bold}};}
        private void StartLiveBlinkTimer(){if(liveBlinkTimer!=null)return; liveBlinkTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)}; liveBlinkTimer.Tick+=(s,e)=>UpdateModeBadgeBlink(); liveBlinkTimer.Start();}
        private void StopLiveBlinkTimer(){if(liveBlinkTimer==null)return; liveBlinkTimer.Stop(); liveBlinkTimer=null;}
        private void UpdateModeBadgeBlink(){if(modeBadgeBorder==null||vm==null)return; if(vm.IsLiveMode&&vm.RunState==TcpV3RunState.On){blinkState=!blinkState; modeBadgeBorder.Background=blinkState?TcpV3Theme.Critical:TcpV3Theme.Danger;} else modeBadgeBorder.Background=vm.IsLiveMode?TcpV3Theme.Critical:TcpV3Theme.Warning; TextBlock tb=modeBadgeBorder.Child as TextBlock; if(tb!=null)tb.Text=vm.IsLiveMode?"LIVE MODE":"TEST RUN";}
        private UIElement BuildDashboardPanel(){StackPanel p=new StackPanel{Margin=new Thickness(16)}; p.Children.Add(new TextBlock{Text="Dashboard",FontSize=22,FontWeight=FontWeights.Bold,Foreground=TcpV3Theme.TextPrimary}); p.Children.Add(ModeBanner()); p.Children.Add(new TextBlock{Text=GetNextStepMessage(),Foreground=TcpV3Theme.Info,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)}); Button main=Button(GetMainToggleText(),GetMainToggleBrush(),(s,e)=>MainToggleClick()); main.FontSize=22; main.Padding=new Thickness(22,14,22,14); main.MinWidth=270; main.IsEnabled=IsMainToggleEnabled(); p.Children.Add(main); p.Children.Add(new TextBlock{Text=GetMainHelpText(),Foreground=TcpV3Theme.TextSecondary,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)}); StackPanel sec=new StackPanel{Orientation=Orientation.Horizontal}; sec.Children.Add(Button("SWITCH TEST",TcpV3Theme.Warning,(s,e)=>SwitchMode(TcpV3OperationMode.TestRun))); sec.Children.Add(Button("SWITCH LIVE",TcpV3Theme.Critical,(s,e)=>SwitchMode(TcpV3OperationMode.Live))); sec.Children.Add(Button("SAVE CONFIG",TcpV3Theme.Neutral,(s,e)=>engine.SaveSettings())); p.Children.Add(sec); GroupBox adv=new GroupBox{Header="Advanced Controls",Margin=new Thickness(0,10,0,0)}; StackPanel a=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(8)}; a.Children.Add(Button("RUN PREFLIGHT",TcpV3Theme.Info,(s,e)=>RunPreflightClick())); a.Children.Add(Button("ARM",TcpV3Theme.Warning,(s,e)=>engine.Arm())); a.Children.Add(Button("ENABLE COPY",Brushes.ForestGreen,(s,e)=>ConfirmEnableCopy())); a.Children.Add(Button("PAUSE",TcpV3Theme.Warning,(s,e)=>engine.Pause())); a.Children.Add(Button("KILL SWITCH",TcpV3Theme.Critical,(s,e)=>engine.KillSwitch())); adv.Content=a; p.Children.Add(adv); return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};}
        private string GetMainToggleText(){if(vm==null)return"ENABLE COPY"; if(vm.RunState==TcpV3RunState.On)return"DISABLE COPY"; if(vm.RunState==TcpV3RunState.KillSwitch)return"KILL SWITCH ACTIVE"; if(vm.RunState==TcpV3RunState.Arming)return"ARMING..."; if(vm.RunState==TcpV3RunState.Error)return"RETRY ENABLE"; return"ENABLE COPY";}
        private Brush GetMainToggleBrush(){if(vm==null)return Brushes.ForestGreen; if(vm.RunState==TcpV3RunState.On)return TcpV3Theme.Danger; if(vm.RunState==TcpV3RunState.KillSwitch)return TcpV3Theme.Critical; if(vm.RunState==TcpV3RunState.Arming)return TcpV3Theme.Warning; return Brushes.ForestGreen;}
        private bool IsMainToggleEnabled(){return vm!=null&&vm.RunState!=TcpV3RunState.KillSwitch&&vm.RunState!=TcpV3RunState.Arming;}
        private void MainToggleClick(){if(vm==null||engine==null)return; if(vm.RunState==TcpV3RunState.On){if(MessageBox.Show("Disable copy routing now?","Disable Copy",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)engine.Disable(); Content=BuildLayout(); return;} if(vm.RunState==TcpV3RunState.Ready){ConfirmEnableCopy(); Content=BuildLayout(); return;} StartSafeCopyClick(); Content=BuildLayout();}
        private string GetMainHelpText(){if(vm==null)return""; string mode=vm.IsLiveMode?"LIVE mode can submit real follower orders.":"TEST RUN mode simulates submissions only."; if(vm.RunState==TcpV3RunState.On)return"Copying is active. Press DISABLE COPY to stop new copied orders. "+mode; return"Copying is currently disabled. Press ENABLE COPY to run safety checks and start routing. "+mode;}
        private void StartSafeCopyClick(){if(vm==null||engine==null)return; if(vm.IsLiveMode&&MessageBox.Show("You are starting LIVE copy routing. Real orders may be submitted. Continue?","LIVE confirmation",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return; bool enableNow=!vm.IsLiveMode||MessageBox.Show("Preflight will run now. If it passes, enable LIVE copy immediately?","Enable LIVE after preflight?",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes; string msg; bool ok=engine.StartSafeCopy(enableNow,out msg); if(!ok&&!string.IsNullOrWhiteSpace(msg))MessageBox.Show(msg,"Enable Copy",MessageBoxButton.OK,MessageBoxImage.Warning);}
        private void ConfirmEnableCopy(){string msg=vm!=null&&vm.IsLiveMode?"Enable LIVE copy routing? Real orders may be submitted.":"Enable Copy in TEST RUN mode? No real orders will be submitted."; if(MessageBox.Show(msg,"Confirm Enable",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)engine.EnableCopy();}
        private void SwitchMode(TcpV3OperationMode mode){if(vm!=null&&vm.RunState==TcpV3RunState.On){MessageBox.Show("Disable copy before changing operation mode.","Blocked",MessageBoxButton.OK,MessageBoxImage.Warning);return;} if(mode==TcpV3OperationMode.Live&&MessageBox.Show("You are switching to LIVE MODE. Real orders may be submitted. Continue?","LIVE MODE",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return; engine.SetOperationMode(mode); Content=BuildLayout();}
        private void RunPreflightClick(){if(vm.RunState==TcpV3RunState.On||vm.RunState==TcpV3RunState.KillSwitch){MessageBox.Show("Disable copy before running Preflight.","Blocked",MessageBoxButton.OK,MessageBoxImage.Warning);return;} engine.RunPreflight();}
        private UIElement ModeBanner(){bool live=vm!=null&&vm.IsLiveMode; return new Border{Background=live?new SolidColorBrush(Color.FromRgb(0xFE,0xF2,0xF2)):new SolidColorBrush(Color.FromRgb(0xFF,0xFB,0xEB)),BorderBrush=live?TcpV3Theme.Critical:TcpV3Theme.Warning,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,8,0,8),Child=new TextBlock{Text=live?"LIVE MODE — real orders may be submitted to enabled follower accounts.":"TEST RUN MODE — follower orders are simulated only. No real orders will be submitted.",Foreground=live?TcpV3Theme.Critical:TcpV3Theme.Warning,FontWeight=FontWeights.Bold,TextWrapping=TextWrapping.Wrap}};}
        private string GetNextStepMessage(){if(vm==null||vm.Settings==null)return"Initialize the Control Center."; if(vm.IsLiveMode&&vm.IsConfigDirty)return"Live mode selected. Run safety checks before enabling copy."; if(vm.RunState==TcpV3RunState.On)return vm.IsLiveMode?"Live copy routing is active. Monitor follower positions.":"Test Run is active. Check DRY_RUN_SUBMIT logs."; if(string.IsNullOrWhiteSpace(vm.Settings.MasterAccountName))return"Select a master account."; if(string.IsNullOrWhiteSpace(vm.Settings.MasterInstrumentName))return"Enter the master instrument."; if(vm.Settings.Followers==null||vm.Settings.Followers.Count==0)return"Add at least one follower account."; if(vm.IsConfigDirty)return"Configuration changed. ENABLE COPY will run Preflight first."; return"Ready for ENABLE COPY workflow.";}
        private UIElement BuildQuickStartPanel(){return new TextBlock{Margin=new Thickness(16),Text="Quick Start\n\n1. Select Master\n2. Add Followers\n3. Choose TEST RUN or LIVE\n4. Press ENABLE COPY\n5. Use DISABLE COPY to stop routing",TextWrapping=TextWrapping.Wrap,FontSize=16};}
        private UIElement BuildMasterPanel(){Grid g=new Grid{Margin=new Thickness(16)}; g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(180)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(320)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(160)}); for(int i=0;i<4;i++)g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); AddLabel(g,"Master Account",0,0); ComboBox acct=new ComboBox{ItemsSource=vm.AccountNames,IsEditable=true,Width=280,Margin=new Thickness(4)}; acct.SetBinding(ComboBox.TextProperty,new Binding("Settings.MasterAccountName"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); Grid.SetRow(acct,0);Grid.SetColumn(acct,1);g.Children.Add(acct); Button refresh=Button("Refresh",TcpV3Theme.Neutral,(s,e)=>engine.RefreshAccounts()); Grid.SetRow(refresh,0);Grid.SetColumn(refresh,2);g.Children.Add(refresh); AddLabel(g,"Master Instrument",1,0); TextBox inst=new TextBox{Width=280,Margin=new Thickness(4)}; inst.SetBinding(TextBox.TextProperty,new Binding("Settings.MasterInstrumentName"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); Grid.SetRow(inst,1);Grid.SetColumn(inst,1);g.Children.Add(inst); AddLabel(g,"Master Position",2,0); TextBlock pos=new TextBlock{Margin=new Thickness(4),FontSize=16,FontWeight=FontWeights.Bold}; pos.SetBinding(TextBlock.TextProperty,new Binding("MasterPosition")); Grid.SetRow(pos,2);Grid.SetColumn(pos,1);g.Children.Add(pos); return g;}
        private UIElement BuildFollowersPanel(){DockPanel p=new DockPanel{Margin=new Thickness(16)}; StackPanel top=new StackPanel(); top.Children.Add(new TextBlock{Text="Followers",FontSize=22,FontWeight=FontWeights.Bold}); StackPanel tb=new StackPanel{Orientation=Orientation.Horizontal}; tb.Children.Add(Button("ADD FOLLOWER",TcpV3Theme.Info,(s,e)=>engine.AddFollower())); tb.Children.Add(Button("REMOVE SELECTED",TcpV3Theme.Neutral,(s,e)=>engine.RemoveSelected(followerGrid!=null?followerGrid.SelectedItem as TcpV3FollowerRowViewModel:null))); top.Children.Add(tb); DockPanel.SetDock(top,Dock.Top); p.Children.Add(top); p.Children.Add(BuildFollowerGrid()); return p;}
        private UIElement BuildFollowerGrid(){followerGrid=new DataGrid{AutoGenerateColumns=false,CanUserAddRows=false,IsReadOnly=false,ItemsSource=vm.Followers,Margin=new Thickness(0),RowHeight=32}; followerGrid.Columns.Add(new DataGridCheckBoxColumn{Header="Enabled",Binding=new Binding("Enabled")}); followerGrid.Columns.Add(new DataGridComboBoxColumn{Header="Account",ItemsSource=vm.AccountNames,SelectedItemBinding=new Binding("AccountName"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}}); followerGrid.Columns.Add(TextCol("Instrument","InstrumentName",false)); followerGrid.Columns.Add(TextCol("Multiplier","Multiplier",false)); followerGrid.Columns.Add(TextCol("Expected","ExpectedPosition",true)); followerGrid.Columns.Add(TextCol("Actual","ActualPosition",true)); followerGrid.Columns.Add(TextCol("Sync","SyncStatus",true)); followerGrid.Columns.Add(TextCol("Order","LastOrderState",true)); followerGrid.Columns.Add(TextCol("Error","LastError",true)); return followerGrid;}
        private UIElement BuildRiskPanel(){StackPanel p=new StackPanel{Margin=new Thickness(16)}; p.Children.Add(new TextBlock{Text="Risk & Safety",FontSize=22,FontWeight=FontWeights.Bold}); p.Children.Add(ModeBanner()); StackPanel mb=new StackPanel{Orientation=Orientation.Horizontal}; mb.Children.Add(Button("SWITCH TO TEST RUN",TcpV3Theme.Warning,(s,e)=>SwitchMode(TcpV3OperationMode.TestRun))); mb.Children.Add(Button("SWITCH TO LIVE",TcpV3Theme.Critical,(s,e)=>SwitchMode(TcpV3OperationMode.Live))); p.Children.Add(mb); p.Children.Add(BuildCheckBox("Global Trading Enabled","Settings.GlobalTradingEnabled")); p.Children.Add(BuildCheckBox("Global Kill Switch","Settings.GlobalKillSwitch")); p.Children.Add(LabelBox("Global Max Order Quantity","Settings.GlobalMaxOrderQuantity")); return p;}
        private UIElement BuildSlippagePanel(){StackPanel p=new StackPanel{Margin=new Thickness(16)}; p.Children.Add(new TextBlock{Text="Slippage",FontSize=22,FontWeight=FontWeights.Bold}); p.Children.Add(LabelBox("Global Max Slippage Ticks","Settings.GlobalMaxSlippageTicks")); p.Children.Add(BuildCheckBox("Strict Slippage Requires Quote","Settings.StrictSlippageRequiresQuote")); p.Children.Add(BuildCheckBox("Block If Quote Unavailable","Settings.BlockIfQuoteUnavailable")); return p;}
        private UIElement BuildPreflightPanel(){DockPanel p=new DockPanel{Margin=new Thickness(16)}; Button run=Button("RUN PREFLIGHT",TcpV3Theme.Info,(s,e)=>RunPreflightClick()); DockPanel.SetDock(run,Dock.Top); p.Children.Add(run); TextBox report=new TextBox{IsReadOnly=true,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto}; report.SetBinding(TextBox.TextProperty,new Binding("PreflightReport")); p.Children.Add(report); return p;}
        private UIElement BuildEventLog(){DockPanel root=new DockPanel{Margin=new Thickness(16)}; Button clear=Button("CLEAR LOG",TcpV3Theme.Neutral,(s,e)=>vm.ClearEvents()); DockPanel.SetDock(clear,Dock.Top); root.Children.Add(clear); DataGrid dg=new DataGrid{AutoGenerateColumns=false,ItemsSource=vm.Events,IsReadOnly=true}; dg.Columns.Add(TextEvent("Time","LocalTime")); dg.Columns.Add(TextEvent("Level","Severity")); dg.Columns.Add(TextEvent("Type","EventType")); dg.Columns.Add(TextEvent("Account","Account")); dg.Columns.Add(TextEvent("Instrument","Instrument")); dg.Columns.Add(TextEvent("Message","Message")); root.Children.Add(dg); return root;}
        private UIElement BuildContactPanel()
        {
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = "Contact & Support", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = TcpV3Theme.TextPrimary });
            root.Children.Add(new TextBlock { Text = "Clean Telegram alerts, safer copy-trading workflows and custom NinjaTrader automation support.", FontSize = 14, Foreground = TcpV3Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });

            Border hero = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF8,0xFA,0xFC)), BorderBrush = TcpV3Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0,0,0,12) };
            StackPanel hp = new StackPanel();
            hp.Children.Add(new TextBlock { Text = "TradeCopier Pro Enterprise Telegram Bot", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = TcpV3Theme.TextPrimary });
            hp.Children.Add(new TextBlock { Text = "Professional NinjaTrader automation & Telegram trade monitoring.", Foreground = TcpV3Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,8) });
            TextBlock sales = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TcpV3Theme.TextPrimary };
            sales.SetBinding(TextBlock.TextProperty, new Binding("Settings.ContactSalesText"));
            hp.Children.Add(sales);
            hero.Child = hp;
            root.Children.Add(hero);

            root.Children.Add(ContactHelpCard());
            root.Children.Add(ContactDetailsCard());
            root.Children.Add(ContactActionButtons());
            root.Children.Add(EditContactInfoCard());

            Border disclaimer = new Border { Background = new SolidColorBrush(Color.FromRgb(0xFF,0xFB,0xEB)), BorderBrush = TcpV3Theme.Warning, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0,12,0,0) };
            disclaimer.Child = new TextBlock { Text = "Telegram and contact features are outbound / informational only. They do not execute trades, modify orders, or provide financial advice.", Foreground = TcpV3Theme.Warning, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            root.Children.Add(disclaimer);
            scroll.Content = root;
            return scroll;
        }

        private UIElement ContactHelpCard()
        {
            GroupBox g = new GroupBox { Header = "How I can help", Margin = new Thickness(0,0,0,12) };
            TextBlock t = new TextBlock { Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap, Foreground = TcpV3Theme.TextPrimary, Text = "I can help with:\n- installation and setup,\n- Telegram bot configuration,\n- custom alert formatting,\n- trade copier customization,\n- risk control logic,\n- account routing logic,\n- NinjaTrader troubleshooting,\n- private feature development.\n\nSend a short message with your setup, instrument, account type and what you want to automate." };
            g.Content = t;
            return g;
        }

        private UIElement ContactDetailsCard()
        {
            GroupBox g = new GroupBox { Header = "Contact", Margin = new Thickness(0,0,0,12) };
            Grid grid = new Grid { Margin = new Thickness(10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddContactRow(grid, 0, "Name", "Settings.ContactDisplayName");
            AddContactRow(grid, 1, "Company / Brand", "Settings.ContactCompanyName");
            AddContactRow(grid, 2, "Email", "Settings.ContactEmail");
            AddContactRow(grid, 3, "Telegram", "Settings.ContactTelegram");
            AddContactRow(grid, 4, "Website", "Settings.ContactWebsite");
            AddContactRow(grid, 5, "Phone", "Settings.ContactPhone");
            g.Content = grid;
            return g;
        }

        private void AddContactRow(Grid grid, int row, string label, string binding)
        {
            TextBlock l = new TextBlock { Text = label + ":", FontWeight = FontWeights.Bold, Margin = new Thickness(0,4,8,4), Foreground = TcpV3Theme.TextPrimary };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0); grid.Children.Add(l);
            TextBlock v = new TextBlock { Margin = new Thickness(0,4,0,4), Foreground = TcpV3Theme.TextSecondary, TextWrapping = TextWrapping.Wrap };
            v.SetBinding(TextBlock.TextProperty, new Binding(binding));
            Grid.SetRow(v, row); Grid.SetColumn(v, 1); grid.Children.Add(v);
        }

        private UIElement ContactActionButtons()
        {
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
            sp.Children.Add(Button("Copy Contact Info", TcpV3Theme.Info, (s,e)=>CopyToClipboardSafe(BuildContactInfoText(), "Contact info copied to clipboard.")));
            sp.Children.Add(Button("Copy Support Request Template", TcpV3Theme.Neutral, (s,e)=>CopyToClipboardSafe(BuildSupportRequestTemplate(), "Support request template copied to clipboard.")));
            sp.Children.Add(Button("Open Website", TcpV3Theme.Warning, (s,e)=>OpenUrlSafe(vm != null && vm.Settings != null ? vm.Settings.ContactWebsite : string.Empty)));
            sp.Children.Add(Button("Copy Telegram", TcpV3Theme.Neutral, (s,e)=>CopyToClipboardSafe(vm != null && vm.Settings != null ? vm.Settings.ContactTelegram : string.Empty, "Telegram contact copied to clipboard.")));
            return sp;
        }

        private UIElement EditContactInfoCard()
        {
            GroupBox g = new GroupBox { Header = "Edit Contact Info", Margin = new Thickness(0,0,0,12) };
            StackPanel p = new StackPanel { Margin = new Thickness(10) };
            p.Children.Add(LabelBox("Contact Display Name", "Settings.ContactDisplayName"));
            p.Children.Add(LabelBox("Contact Company / Brand", "Settings.ContactCompanyName"));
            p.Children.Add(LabelBox("Contact Email", "Settings.ContactEmail"));
            p.Children.Add(LabelBox("Contact Telegram", "Settings.ContactTelegram"));
            p.Children.Add(LabelBox("Contact Website", "Settings.ContactWebsite"));
            p.Children.Add(LabelBox("Contact Phone", "Settings.ContactPhone"));
            p.Children.Add(LabelBox("Contact Sales Text", "Settings.ContactSalesText"));
            p.Children.Add(Button("Save Contact Info", TcpV3Theme.Info, (s,e)=>engine.SaveSettings()));
            g.Content = p;
            return g;
        }

        private string BuildContactInfoText()
        {
            TcpV3Settings st = vm != null ? vm.Settings : null;
            if (st == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Name: " + st.ContactDisplayName);
            sb.AppendLine("Company: " + st.ContactCompanyName);
            sb.AppendLine("Email: " + st.ContactEmail);
            sb.AppendLine("Telegram: " + st.ContactTelegram);
            sb.AppendLine("Website: " + st.ContactWebsite);
            sb.AppendLine("Phone: " + st.ContactPhone);
            sb.AppendLine("GitHub: https://github.com/elodfarkas");
            sb.AppendLine("LinkedIn: https://www.linkedin.com/in/elod/");
            return sb.ToString();
        }

        private string BuildSupportRequestTemplate()
        {
            return "Hello," + Environment.NewLine + Environment.NewLine
                + "I would like help with TradeCopier Pro Enterprise Telegram Bot." + Environment.NewLine + Environment.NewLine
                + "My NinjaTrader version:" + Environment.NewLine
                + "Account type: Playback / Sim / Live" + Environment.NewLine
                + "Instrument:" + Environment.NewLine
                + "Telegram configured: Yes / No" + Environment.NewLine
                + "What I need:" + Environment.NewLine
                + "Problem description:" + Environment.NewLine
                + "Screenshots/logs attached: Yes / No" + Environment.NewLine + Environment.NewLine
                + "Thank you.";
        }

        private void CopyToClipboardSafe(string text, string successMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) { MessageBox.Show("Nothing to copy.", "Contact", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                Clipboard.SetText(text);
                MessageBox.Show(successMessage, "Contact", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { MessageBox.Show("Could not copy to clipboard.", "Contact", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void OpenUrlSafe(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) { MessageBox.Show("Website URL is not configured.", "Contact", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                string u = url.Trim();
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    u = "https://" + u;
                Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
            }
            catch { MessageBox.Show("Could not open website.", "Contact", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private UIElement BuildSettingsPanel(){StackPanel p=new StackPanel{Margin=new Thickness(16)}; p.Children.Add(new TextBlock{Text="Settings",FontSize=22,FontWeight=FontWeights.Bold}); p.Children.Add(LabelBox("Audit File Name","Settings.AuditFileName")); p.Children.Add(BuildCheckBox("Enable CSV Audit","Settings.EnableCsvAudit")); p.Children.Add(Button("SAVE CONFIG",TcpV3Theme.Info,(s,e)=>engine.SaveSettings())); p.Children.Add(Button("LOAD CONFIG",TcpV3Theme.Neutral,(s,e)=>engine.LoadSettings())); p.Children.Add(BuildAboutPanel()); return p;}
        private UIElement BuildAboutPanel(){Border b=new Border{Background=Brushes.White,BorderBrush=TcpV3Theme.Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,12,0,0)}; StackPanel p=new StackPanel(); p.Children.Add(new TextBlock{Text="About",FontSize=18,FontWeight=FontWeights.Bold}); p.Children.Add(new TextBlock{Text="TradeCopier Pro Enterprise",Margin=new Thickness(0,6,0,0)}); p.Children.Add(new TextBlock{Text="Created by Juhász Előd Farkas"}); p.Children.Add(new TextBlock{Text="GitHub: https://github.com/elodfarkas"}); p.Children.Add(new TextBlock{Text="LinkedIn: https://www.linkedin.com/in/elod/"}); p.Children.Add(new TextBlock{Text="Web: https://orderflow-hub.com/"}); b.Child=p; return b;}
        private DataGridTextColumn TextCol(string h,string b,bool ro){return new DataGridTextColumn{Header=h,Binding=new Binding(b),IsReadOnly=ro};} private DataGridTextColumn TextEvent(string h,string b){return new DataGridTextColumn{Header=h,Binding=new Binding(b)};} private void AddLabel(Grid g,string text,int row,int col){TextBlock tb=new TextBlock{Text=text,Margin=new Thickness(4),FontWeight=FontWeights.Bold}; Grid.SetRow(tb,row);Grid.SetColumn(tb,col);g.Children.Add(tb);} private Button Button(string text,Brush bg,RoutedEventHandler click){Button b=new Button{Content=text,Margin=new Thickness(4),Padding=new Thickness(12,6,12,6),Background=bg,Foreground=Brushes.White,FontWeight=FontWeights.Bold}; b.Click+=click; return b;} private UIElement LabelBox(string label,string path){StackPanel s=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,4)}; s.Children.Add(new TextBlock{Text=label,Width=220}); TextBox tb=new TextBox{Width=160}; tb.SetBinding(TextBox.TextProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); s.Children.Add(tb); return s;} private UIElement BuildCheckBox(string text,string path){System.Windows.Controls.CheckBox cb=new System.Windows.Controls.CheckBox{Content=text,Margin=new Thickness(0,4,0,4)}; cb.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,new Binding(path){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); return cb;}
        private UIElement BuildStatusBar(){System.Windows.Controls.Primitives.StatusBar sb=new System.Windows.Controls.Primitives.StatusBar(); TextBlock state=new TextBlock(); state.SetBinding(TextBlock.TextProperty,new Binding("RunStateText"){StringFormat="State: {0}"}); sb.Items.Add(state); TextBlock mode=new TextBlock{Foreground=TcpV3Theme.Warning,FontWeight=FontWeights.Bold}; mode.SetBinding(TextBlock.TextProperty,new Binding("OperationModeText"){StringFormat="Mode: {0}"}); sb.Items.Add(mode); TextBlock dirty=new TextBlock(); dirty.SetBinding(TextBlock.TextProperty,new Binding("IsConfigDirty"){StringFormat="Dirty: {0}"}); sb.Items.Add(dirty); TextBlock pf=new TextBlock(); pf.SetBinding(TextBlock.TextProperty,new Binding("LastPreflightSuccess"){StringFormat="Preflight: {0}"}); sb.Items.Add(pf); TextBlock action=new TextBlock(); action.SetBinding(TextBlock.TextProperty,new Binding("LastAction"){StringFormat="Last: {0}"}); sb.Items.Add(action); sb.Items.Add(new TextBlock{Text="by Juhász Előd Farkas",Foreground=TcpV3Theme.TextSecondary}); return sb;}
    }
    #endregion
}