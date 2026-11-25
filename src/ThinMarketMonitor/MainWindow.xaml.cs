using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ThinMarketMonitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private readonly ExchangeApi api;
    private readonly Settings settings;
    private readonly bool persistSettings;
    private readonly ColibriBridge colibri;
    private readonly SignalNotifications notifications;
    private readonly SignalCooldown signalCooldown;
    private readonly RussianVoicePack neuralVoice;
    private readonly Dictionary<MarketSource, Catalog> catalogs = new();
    private readonly ChartCell[] cells = new ChartCell[9];
    private readonly ChartCell overviewChart;
    private readonly OverviewPanel overviewPanel;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<Market> markets = [];
    private IReadOnlyList<Market> candidates = [];
    private MarketSource[] missing = [];
    private VolumeRule[] filterRules = [];
    private VolumeFilterMonitor? volumeFilter;
    private SpreadFilterMonitor? spreadFilter;
    private HashSet<string> eligible = [];
    private MarketStreams? streams;
    private SettingsPanel? settingsPanel;
    private SignalListingsPanel? signalListings;
    private MarketPicker? picker;
    private IInputElement? previousFocus;
    private ChartCell? fullscreenCell;
    private ChartCell? activeCell;
    private CandlePlot? keyboardRuler;
    private WindowState previousWindowState;
    private WindowStyle previousWindowStyle;
    private ResizeMode previousResizeMode;
    private bool loading;
    private long order;
    private DateTimeOffset nextRefresh;
    private DateTimeOffset nextRetry = DateTimeOffset.MaxValue;
    private int connectionFailures;
    private long networkChanged;
    private bool watchingNetwork;

    public MainWindow() : this(startMonitoring: true) { }

    internal MainWindow(bool startMonitoring, Settings? testSettings = null, ColibriClient? colibriClient = null,
        HttpClient? testHttp = null, SignalNotifications? testNotifications = null, TimeProvider? signalClock = null)
    {
        http = testHttp ?? new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20)
        }) { Timeout = TimeSpan.FromSeconds(16) };
        settings = testSettings ?? Settings.Load();
        persistSettings = testSettings == null;
        InitializeComponent();
        Overlay.SizeChanged += (_, _) => FitSettingsDialog();
        neuralVoice = new(http, stop.Token);
        notifications = testNotifications ?? new(settings, Dispatcher, speak: neuralVoice.Speak);
        signalCooldown = new(signalClock ?? TimeProvider.System);
        api = new(http);
        var chartFeatures = new ChartFeatures(settings, api, source => catalogs.GetValueOrDefault(source), Save);
        colibri = new(settings, colibriClient);
        colibri.StatusChanged += ShowIntegrationStatus;
        var mark = Terminal.Icon("candles");
        System.Windows.Documents.TextElement.SetForeground(mark, Terminal.Brush("Accent"));
        BrandMark.Child = mark;
        SignalListingsButton.Content = Terminal.LabelWithIcon("listings", "Листинги", 5);
        foreach (var (button, icon, accessKey) in new[]
        {
            (SignalsButton, "signals-nav", "С"), (OverviewButton, "overview-nav", "О"), (SettingsButton, "settings-nav", "Н")
        })
        {
            button.Content = Terminal.Icon(icon, 22);
            AccessKeyManager.Register(accessKey, button);
        }
        for (int i = 0; i < cells.Length; i++)
        {
            int slot = i;
            var cell = new ChartCell(api, i + 1, () => PickMarket(slot), Save, () => ++order, () => settings.Ruler, () => settings.FrameButtons, chartFeatures)
                { Choice = settings.Cells[i] };
            cells[i] = cell;
            cell.IndicatorLayoutChanged += () => ResizeChartGrid(this, null);
            cell.PreviewMouseDown += (_, _) => activeCell = cell;
            cell.GotKeyboardFocus += (_, _) => activeCell = cell;
            cell.FrameChanged += _ => Save();
            cell.OpenInColibri += market => colibri.Open(market);
            cell.BlacklistRequested += BlacklistSignalCoin;
        }
        overviewChart = new ChartCell(api, 0, null, Save, () => ++order, () => settings.Ruler, () => settings.FrameButtons, chartFeatures)
        {
            Choice = settings.OverviewChoice,
            PlotHeight = double.NaN
        };
        overviewChart.SetFrame(settings.OverviewFrame);
        overviewChart.FrameChanged += value => { settings.OverviewFrame = value; Save(); };
        overviewChart.PreviewMouseDown += (_, _) => activeCell = overviewChart;
        overviewChart.GotKeyboardFocus += (_, _) => activeCell = overviewChart;
        overviewPanel = new OverviewPanel(api, settings, () =>
        {
            colibri.Configure();
            colibri.CancelPending();
            RefreshOverview();
            Save();
        }, selected =>
        {
            overviewChart.Assign(selected, pinned: false);
            activeCell = overviewChart;
            Save();
        }, savePresentation: Save);
        overviewPanel.UserSelected += market => colibri.Open(market, automatic: true);
        OverviewDrawerHost.Content = overviewPanel;
        OverviewChartHost.Content = overviewChart;
        OverviewDivider.AddHandler(KeyDownEvent, new KeyEventHandler(OverviewDividerKeyDown), handledEventsToo: true);
        order = cells.Max(cell => cell.Order);

        RebuildGrid();
        RefreshOverview();
        UpdateIntegration();
        RefreshDiagnostics();
        if (startMonitoring) Loaded += async (_, _) =>
        {
            if (!watchingNetwork)
            {
                NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
                watchingNetwork = true;
            }
            timer.Tick += Tick;
            timer.Start();
            await ReloadCatalogs();
        };
        PreviewKeyDown += KeyPressed;
        PreviewKeyUp += (_, e) =>
        {
            if (keyboardRuler == null || e.Key != settings.Ruler.Key) return;
            keyboardRuler.EndKeyboardRuler();
            keyboardRuler = null;
            e.Handled = true;
        };
        Deactivated += (_, _) => CancelChartGestures();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) CancelChartGestures();
        };
        Closing += (_, e) => { if (settingsPanel?.Installing == true) e.Cancel = true; };
        Closed += (_, _) => Dispose();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Overlay.Visibility == Visibility.Visible)
        {
            if (e.Key != Key.Escape || settingsPanel?.CapturingShortcut == true) return;
            if (settingsPanel != null) settingsPanel.Close();
            else CloseDialog();
            e.Handled = true;
            return;
        }
        var underMouse = overviewChart.IsVisible && overviewChart.IsMouseOver ? overviewChart
            : cells.FirstOrDefault(c => c.IsMouseOver && c.IsVisible);
        var drawing = fullscreenCell ?? underMouse ?? (activeCell?.IsVisible == true ? activeCell : null);
        if (e.Key == Key.Escape && (drawing?.Plot.CancelDrawing() == true ||
            overviewChart.Plot.CancelDrawing() || cells.Any(cell => cell.Plot.CancelDrawing())))
        { e.Handled = true; return; }
        if (e.Key == Key.Escape && fullscreenCell != null) { ExitFullscreen(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Up or Key.Down && ChartSurface.IsPaneDivider(Keyboard.FocusedElement)) return;
        if (e.Key == Key.Delete && drawing?.Plot.DeleteDrawing() == true) { e.Handled = true; return; }
        if (e.Key == settings.Fullscreen)
        {
            if (!e.IsRepeat)
            {
                if (fullscreenCell != null) ExitFullscreen();
                else
                {
                    var target = underMouse?.HasMarket == true ? underMouse : activeCell?.IsVisible == true ? activeCell :
                        overviewChart.IsVisible ? overviewChart : null;
                    if (target?.HasMarket == true) EnterFullscreen(target);
                }
            }
            e.Handled = true;
        }
        else if (settings.Ruler.Mouse == null && e.Key == settings.Ruler.Key)
        {
            if (!e.IsRepeat && underMouse?.Plot.BeginKeyboardRuler() == true) keyboardRuler = underMouse.Plot;
            if (keyboardRuler != null) e.Handled = true;
        }
        else if (e.Key == settings.FrameUp || e.Key == settings.FrameDown)
        {
            if (overviewPanel.IsKeyboardFocusWithin) return;
            var target = fullscreenCell ?? underMouse ?? (activeCell?.IsVisible == true ? activeCell :
                overviewChart.IsVisible ? overviewChart : null);
            if (target != null)
            {
                if (!e.IsRepeat) target.CycleFrame(e.Key == settings.FrameUp ? 1 : -1);
                e.Handled = true;
            }
        }
    }

    private void CancelChartGestures()
    {
        foreach (var cell in cells) cell.CancelGestures();
        overviewChart.CancelGestures();
        keyboardRuler = null;
    }

    private void EnterFullscreen(ChartCell cell, bool changeWindow = true)
    {
        CancelChartGestures();
        fullscreenCell = cell;
        previousWindowState = WindowState;
        previousWindowStyle = WindowStyle;
        previousResizeMode = ResizeMode;
        if (ReferenceEquals(cell, overviewChart)) OverviewChartHost.Content = null;
        else Charts.Children.Remove(cell);
        cell.PlotHeight = double.NaN;
        cell.Margin = new Thickness(0);
        Workspace.Visibility = Visibility.Collapsed;
        BottomNavigation.Visibility = Visibility.Collapsed;
        FullChart.Content = cell;
        cell.SetFullscreenDrawingToolbar(true);
        FullChart.Visibility = Visibility.Visible;
        if (changeWindow)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    private void ExitFullscreen()
    {
        if (fullscreenCell == null) return;
        CancelChartGestures();
        var previous = fullscreenCell;
        previous.SetFullscreenDrawingToolbar(false);
        previous.Margin = new Thickness(0, 0, 1, 1);
        fullscreenCell = null;
        FullChart.Content = null;
        FullChart.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        BottomNavigation.Visibility = Visibility.Visible;
        WindowStyle = previousWindowStyle;
        ResizeMode = previousResizeMode;
        WindowState = previousWindowState;
        RebuildGrid();
        if (ReferenceEquals(previous, overviewChart)) OverviewChartHost.Content = overviewChart;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (SystemParameters.HighContrast) return;
        SetWindowAttribute(handle, 20, 1, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        SetWindowAttribute(handle, 35, 0x00182220, sizeof(int)); // DWMWA_CAPTION_COLOR
        SetWindowAttribute(handle, 36, 0x00DDF0F5, sizeof(int)); // DWMWA_TEXT_COLOR
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int SetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    private void ChangeGrid(object sender, RoutedEventArgs e)
    {
        int size = ReferenceEquals(sender, GridThree) ? 3 : 2;
        if (settings.Grid != size)
        {
            settings.Grid = size;
            RebuildGrid();
            Save();
        }
        GridTwo.IsChecked = size == 2;
        GridThree.IsChecked = size == 3;
    }

    private void RebuildGrid()
    {
        Charts.Children.Clear();
        Charts.Rows = Charts.Columns = settings.Grid;
        GridTwo.IsChecked = settings.Grid == 2;
        GridThree.IsChecked = settings.Grid == 3;
        int visible = settings.Grid * settings.Grid;
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].PlotHeight = double.NaN;
            cells[i].Activate(i < visible);
            if (i < visible) Charts.Children.Add(cells[i]);
        }
        ResizeChartGrid(this, null);
    }

    private void ResizeChartGrid(object sender, SizeChangedEventArgs? e)
    {
        if (Charts != null && ChartScroll != null)
        {
            int minimum = cells.Take(settings.Grid * settings.Grid).Max(cell => cell?.MinimumGridHeight ?? 116);
            Charts.Height = Math.Max(settings.Grid * minimum, ChartScroll.ActualHeight - ChartScroll.Padding.Top);
        }
    }

    private void ChangeWorkspace(object sender, RoutedEventArgs e)
    {
        if (settingsPanel?.Installing == true) return;
        if (DialogHost.Content != null) CloseDialog();
        bool overview = ReferenceEquals(sender, OverviewButton);
        if (settings.Overview == overview) return;
        colibri.CancelPending();
        settings.Overview = overview;
        if (overview) overviewChart.ResetDrawingToolbar();
        else foreach (var cell in cells) cell.ResetDrawingToolbar();
        activeCell = settings.Overview ? overviewChart : null;
        RefreshOverview();
        Save();
    }

    private void ChangeDrawer(object sender, RoutedEventArgs e)
    {
        settings.OverviewDrawer = !settings.OverviewDrawer;
        RefreshOverview();
        Save();
    }

    private void RefreshOverview()
    {
        GridSpace.Visibility = settings.Overview ? Visibility.Collapsed : Visibility.Visible;
        OverviewSpace.Visibility = settings.Overview ? Visibility.Visible : Visibility.Collapsed;
        GridSwitch.Visibility = SignalListingsButton.Visibility = settings.Overview ? Visibility.Collapsed : Visibility.Visible;
        UpdateNavigation();
        if (settings.Overview) UpdateOverviewQuickSources();
        DrawerSwitch.Content = Terminal.Icon(settings.OverviewDrawer ? "drawer-open" : "drawer-closed");
        DrawerSwitch.Foreground = Terminal.Brush(settings.OverviewDrawer ? "Accent" : "Muted");
        string drawerLabel = settings.OverviewDrawer ? "Скрыть инструменты" : "Показать инструменты";
        DrawerSwitch.ToolTip = drawerLabel;
        System.Windows.Automation.AutomationProperties.SetName(DrawerSwitch, drawerLabel);
        OverviewDrawerHost.Visibility = settings.OverviewDrawer ? Visibility.Visible : Visibility.Collapsed;
        UpdateOverviewDrawerWidth();
        var result = ListingFilter.Apply(catalogs, settings.OverviewRule, settings.OverviewSource);
        int unavailable = result.Missing.Count(source => catalogs.GetValueOrDefault(source)?.Error != null);
        string? pending = result.Missing.Count == 0 ? null : unavailable > 0
            ? $"Недоступно каталогов: {unavailable} · фильтр ожидает данные"
            : $"Загрузка каталогов: {result.Missing.Count}";
        string? details = result.Missing.Count == 0 ? null : string.Join("\n", result.Missing.Select(source =>
            $"{source.Label} · {catalogs.GetValueOrDefault(source)?.Error ?? "загрузка"}"));
        if (overviewChart.Choice?.Key != settings.OverviewChoice?.Key) overviewChart.Choice = settings.OverviewChoice;
        overviewChart.Resolve(result.Markets.FirstOrDefault(m => m.Key == settings.OverviewChoice?.Key && settings.ShowsOverviewMarket(m)));
        overviewChart.Activate(settings.Overview);
        overviewPanel.SetSource(result.Markets, settings.Overview && settings.OverviewDrawer, pending, details,
            catalogUnavailable: unavailable > 0);
        settingsPanel?.UpdateQuoteChoices(OverviewSourceQuotes());
    }

    private void UpdateOverviewQuickSources()
    {
        var venues = settings.QuickOverviewVenues().ToArray();
        var buttons = OverviewQuickSources.Children.OfType<Button>();
        if (!buttons.Select(button => (Venue)button.Tag).SequenceEqual(venues))
        {
            var existing = buttons.ToDictionary(button => (Venue)button.Tag);
            var focused = buttons.FirstOrDefault(button => button.IsKeyboardFocused);
            OverviewQuickSources.Children.Clear();
            foreach (var venue in venues)
                OverviewQuickSources.Children.Add(existing.TryGetValue(venue, out var button) ? button : QuickButton(venue));
            if (focused?.Parent == OverviewQuickSources) focused.Focus();
        }
        foreach (var button in buttons)
        {
            var market = settings.QuickOverviewSource((Venue)button.Tag);
            bool active = market == settings.OverviewSource;
            button.ToolTip = market.Label;
            button.BorderBrush = Terminal.Brush(active ? "Accent" : "Outline");
            button.Background = Terminal.Brush(active ? "AccentSurface" : "Surface");
            button.Foreground = Terminal.Brush(active ? "Accent" : "Text");
            System.Windows.Automation.AutomationProperties.SetName(button, market.Label);
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, active ? "Текущий источник" : "");
        }

        Button QuickButton(Venue venue)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Terminal.VenueLogo(venue, 15));
            content.Children.Add(new TextBlock
            {
                Text = venue.Label(), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            var button = new Button
            {
                Tag = venue, Content = content, MinHeight = 25, Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(0, 0, 4, 3)
            };
            button.Click += (_, _) => overviewPanel.SelectSource(settings.QuickOverviewSource(venue));
            return button;
        }
    }

    private void OverviewBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateOverviewDrawerWidth();
    }

    private void UpdateOverviewDrawerWidth()
    {
        bool visible = settings.OverviewDrawer;
        OverviewDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OverviewDividerColumn.Width = new GridLength(visible ? OverviewDivider.Width : 0);
        var chart = OverviewBody.ColumnDefinitions[0];
        chart.Width = new GridLength(1, GridUnitType.Star);
        const double minimum = 230;
        double contentWidth = (OverviewBody.ActualWidth > 0 ? OverviewBody.ActualWidth : 1090) - OverviewDivider.Width;
        double maximum = Math.Clamp(contentWidth - chart.MinWidth, minimum, 1600);
        OverviewDrawerColumn.MinWidth = visible ? minimum : 0;
        OverviewDrawerColumn.MaxWidth = visible ? maximum : 0;
        double preferred = Math.Clamp(settings.OverviewDrawerWidth, minimum, maximum);
        OverviewDrawerColumn.Width = visible
            ? new GridLength(preferred / Math.Max(chart.MinWidth, contentWidth - preferred), GridUnitType.Star) : new GridLength(0);
    }

    private void OverviewDividerCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled && Math.Abs(e.HorizontalChange) >= 1) SaveOverviewDrawerWidth();
    }

    private void OverviewDividerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled && e.Key is Key.Left or Key.Right) SaveOverviewDrawerWidth();
    }

    private void SaveOverviewDrawerWidth()
    {
        OverviewBody.UpdateLayout();
        settings.OverviewDrawerWidth = Math.Clamp(OverviewDrawerColumn.ActualWidth, 230, 1600);
        Save();
    }

    internal void SmokeTestLayout()
    {
        UpdateLayout();
        int original = settings.Grid;
        bool overview = settings.Overview;
        double originalHeight = Height;
        try
        {
            settings.Overview = false;
            RefreshOverview();
            foreach (int size in new[] { 2, 3 })
            {
                settings.Grid = size;
                RebuildGrid();
                foreach (double height in new[] { MinHeight, originalHeight })
                {
                    Height = height;
                    UpdateLayout();
                    if (Charts.Children.Count != size * size) throw new InvalidOperationException("Chart grid is incomplete.");
                    double expected = Math.Max(size * 116, ChartScroll.ActualHeight - ChartScroll.Padding.Top);
                    if (Math.Abs(Charts.ActualHeight - expected) > 2 || Charts.ActualWidth < Workspace.ActualWidth - 42 ||
                        cells.Take(size * size).Any(cell => cell.Plot.ActualHeight <= 0))
                        throw new InvalidOperationException("The grid or chart did not resize to the available area.");
                }
            }
            OpenSettings(this, new RoutedEventArgs());
            UpdateLayout();
            if (DialogHost.Content is not SettingsPanel)
                throw new InvalidOperationException("The settings overlay did not open.");
            CloseDialog();
            OpenSignalListings(this, new RoutedEventArgs());
            UpdateLayout();
            if (DialogHost.Content is not SignalListingsPanel || !SignalListingsButton.IsVisible)
                throw new InvalidOperationException("The signal listing dialog did not open.");
            CloseDialog();
            PickMarket(0);
            UpdateLayout();
            if (DialogHost.Content is not MarketPicker)
                throw new InvalidOperationException("The market picker did not open.");
            CloseDialog();
            EnterFullscreen(cells[0], changeWindow: false);
            if (!ReferenceEquals(FullChart.Content, cells[0])) throw new InvalidOperationException("Fullscreen did not retain the chart.");
            ExitFullscreen();
            if (!Charts.Children.Contains(cells[0])) throw new InvalidOperationException("Fullscreen did not restore the chart.");
            overviewPanel.SetSource([new Market(Venue.KuCoin, "CHECK-USDT", "CHECK", "USDT")], false);
            overviewPanel.SetSource([], false);
            settings.Overview = true;
            RefreshOverview();
            UpdateLayout();
            if (GridSpace.IsVisible || SignalListingsButton.IsVisible || !OverviewSpace.IsVisible || overviewChart.Plot.ActualHeight <= 0)
                throw new InvalidOperationException("The overview workspace did not open.");
        }
        finally
        {
            Height = originalHeight;
            settings.Grid = original;
            settings.Overview = overview;
            RebuildGrid();
            RefreshOverview();
        }
    }

    private async Task ReloadCatalogs(MarketSource[]? retry = null)
    {
        if (loading) return;
        loading = true;
        ShowCatalogStatus("Обновление каталогов…");
        try
        {
            await Task.WhenAll((retry ?? MarketSource.All).Select(async v =>
            {
                var catalog = await api.LoadCatalog(v, stop.Token);
                if (!stop.IsCancellationRequested)
                {
                    catalogs[v] = catalog;
                    signalListings?.RefreshCatalogStatus();
                    RefreshOverview();
                    if (v.Segment == Segment.Spot || settings.Rule(v) != ListingRule.Ignore) ApplyFilter(false);
                }
            }));
            if (stop.IsCancellationRequested) return;
            ApplyFilter(false);
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        finally
        {
            loading = false;
            var now = DateTimeOffset.UtcNow;
            if (retry == null) nextRefresh = now.AddMinutes(5);
            connectionFailures = CatalogsToRetry().Length > 0 ? Math.Min(connectionFailures + 1, 6) : 0;
            nextRetry = connectionFailures == 0 ? DateTimeOffset.MaxValue :
                now.AddSeconds(Math.Min(300, 15 << (connectionFailures - 1)));
        }
    }

    private MarketSource[] CatalogsToRetry()
    {
        bool defaultSpot = !MarketSource.All.Any(source => settings.Rule(source) == ListingRule.Require);
        return catalogs.Where(entry => entry.Value.ConnectionFailed).Select(entry => entry.Key).Where(source =>
            settings.Rule(source) != ListingRule.Ignore || defaultSpot && source.Segment == Segment.Spot ||
            settings.Overview && (source == settings.OverviewSource || settings.OverviewRule(source) != ListingRule.Ignore)).ToArray();
    }

    private void NetworkAddressChanged(object? sender, EventArgs e) =>
        Interlocked.Exchange(ref networkChanged, Environment.TickCount64);

    private void BlacklistSignalCoin(Market market)
    {
        if (!settings.BlockSignals(market)) return;
        foreach (var cell in cells)
        {
            var instrument = cell.Instrument ?? catalogs.Values.SelectMany(c => c.Markets)
                .FirstOrDefault(m => m.Key == cell.Choice?.Key);
            if (instrument == null || !settings.SignalBlocked(instrument)) continue;
            if (ReferenceEquals(fullscreenCell, cell)) ExitFullscreen();
            cell.Clear();
        }
        ApplyFilter(false);
        Save();
    }

    private void ApplyFilter(bool thresholdsChanged)
    {
        var result = ListingFilter.Apply(catalogs, settings.Rule);
        missing = result.Missing.ToArray();
        var allowed = result.Markets.Where(settings.ShowsSignalMarket).ToArray();
        bool candidatesChanged = !candidates.SequenceEqual(allowed);
        if (candidatesChanged) candidates = allowed;
        var nextRules = settings.VolumeFilters.Where(r => r.Enabled).ToArray();
        if (candidatesChanged || !filterRules.SequenceEqual(nextRules))
        {
            filterRules = nextRules;
            volumeFilter?.Dispose();
            volumeFilter = nextRules.Length > 0 && candidates.Count > 0 ? new(api, candidates, nextRules) : null;
        }
        if (candidatesChanged || (spreadFilter != null) != (settings.SpreadFilter.Enabled && candidates.Count > 0))
        {
            spreadFilter?.Dispose();
            spreadFilter = settings.SpreadFilter.Enabled && candidates.Count > 0 ? new(api, candidates) : null;
        }
        RefreshEligibility();
        if (candidatesChanged || thresholdsChanged)
        {
            var price = settings.PriceModule ? settings.Thresholds.Where(t => t.Enabled).ToArray() : [];
            var volume = settings.VolumeModule ? settings.VolumeGrowthThresholds.Where(t => t.Enabled).ToArray() : [];
            if (streams != null && !thresholdsChanged && candidates.Count > 0)
                streams.UpdateMarkets(candidates);
            else
            {
                var previous = streams;
                previous?.Dispose();
                streams = candidates.Count > 0 && (price.Length > 0 || volume.Length > 0)
                    ? new MarketStreams(api, candidates, price, volume, markets, thresholdsChanged ? null : previous,
                        settings.VolumeReferenceSeconds) : null;
            }
        }
        ShowCoverage();
    }

    private void RefreshEligibility()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var filtered = volumeFilter?.Evaluate(now);
        IReadOnlyList<Market> next = filtered?.Markets ?? candidates;
        var spread = spreadFilter?.Evaluate(next, settings.SpreadFilter, now);
        if (spread is { } range) next = range.Markets;
        var errors = catalogs.Where(x => x.Value.Error != null).ToArray();
        string status = missing.Length > 0
            ? $"Фильтр ожидает каталоги · {string.Join(", ", missing.Select(v => v.Label))}"
            : $"{CountLabel(next.Count)} после фильтра" +
              (settings.VolumePercentMigrationPending ? " · объём теперь в %: проверьте настройки модуля" : "") +
              (settings.SignalBlacklist.Length > 0 ? $" · чёрный список: {settings.SignalBlacklist.Length:N0}" : "") +
              (filtered is { Pending: > 0 } pending ? $" · объём ожидается: {pending.Pending:N0}" : "") +
              (filtered is { Unsupported: > 0 } unsupported ? $" · оборот USD недоступен: {unsupported.Unsupported:N0}" : "") +
              (spread is { Pending: > 0 } waiting ? $" · спред ожидается: {waiting.Pending:N0}" : "") +
              (spread is { Unsupported: > 0 } unavailable ? $" · спред недоступен: {unavailable.Unsupported:N0}" : "") +
              (errors.Length > 0 ? $" · недоступно источников: {errors.Length}" : "");
        string? tooltip = string.Join("\n", errors.Select(x => $"{x.Key.Label} — {x.Value.Error}")
            .Concat(spreadFilter == null ? [] : new[] { spreadFilter.Coverage(now) }));
        if (tooltip.Length == 0) tooltip = null;
        string? notice = missing.Any(source => catalogs.GetValueOrDefault(source)?.Error != null)
            ? "Сигналы · каталог недоступен" : null;
        if (CatalogStatus.Text != status || !Equals(CatalogStatus.ToolTip, tooltip) || MonitorNotice.Text != (notice ?? ""))
            ShowCatalogStatus(status, tooltip, notice);
        if (!markets.SequenceEqual(next))
        {
            markets = next;
            eligible = markets.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
            streams?.SetEligible(markets);
            var lookup = markets.ToDictionary(x => x.Key);
            foreach (var cell in cells)
            {
                if (cell.Choice == null) continue;
                lookup.TryGetValue(cell.Choice.Key, out var market);
                cell.Resolve(market);
            }
            picker?.UpdateMarkets(markets);
        }
    }

    private void ShowCatalogStatus(string status, string? tooltip = null, string? notice = null)
    {
        CatalogStatus.Text = status;
        CatalogStatus.ToolTip = tooltip;
        MonitorNotice.Text = notice ?? "";
        MonitorNotice.Visibility = notice != null && !settings.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        settingsPanel?.UpdateFilter(status);
    }

    private static string CountLabel(int count)
    {
        string noun = count % 100 is >= 11 and <= 14 ? "монет" : (count % 10) switch
        {
            1 => "монета", >= 2 and <= 4 => "монеты", _ => "монет"
        };
        return $"{count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} {noun}";
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (!loading)
        {
            long change = Volatile.Read(ref networkChanged);
            if (change != 0 && Environment.TickCount64 - change >= 1500 &&
                Interlocked.CompareExchange(ref networkChanged, 0, change) == change)
            {
                connectionFailures = 0;
                nextRefresh = DateTimeOffset.MinValue;
            }
            var now = DateTimeOffset.UtcNow;
            if (now >= nextRefresh) await ReloadCatalogs();
            else if (now >= nextRetry)
            {
                var retry = CatalogsToRetry();
                if (retry.Length != 0) await ReloadCatalogs(retry);
                else nextRetry = DateTimeOffset.MaxValue;
            }
        }
        if (stop.IsCancellationRequested) return;
        if (!loading && (volumeFilter != null || spreadFilter != null)) RefreshEligibility();
        for (int i = 0; i < 150 && streams != null && streams.TryRead(out var signal); i++)
            if (eligible.Contains(signal!.Market.Key)) Place(signal);
        ShowCoverage();
    }

    internal static string WindowName(int seconds) => seconds % 3600 == 0 ? $"{seconds / 3600} ч" :
        seconds % 60 == 0 ? $"{seconds / 60} мин" : $"{seconds} с";

    private void ShowCoverage()
    {
        if (!settings.DebugMode) return;
        if (streams == null)
        {
            StreamLed.Fill = Terminal.Brush("Muted");
            bool enabled = settings.PriceModule && settings.Thresholds.Any(t => t.Enabled) ||
                settings.VolumeModule && settings.VolumeGrowthThresholds.Any(t => t.Enabled);
            StreamStatus.Text = enabled ? "Сигналы ожидают рынки" : "Сигналы выключены";
            StreamStatus.ToolTip = null;
            return;
        }
        var status = streams.Status();
        int connected = status.Where(s => s.Connected).Sum(s => s.Count);
        int fresh = streams.FreshCount(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StreamLed.Fill = Terminal.Brush(connected > 0 ? "Rising" : "Muted");
        StreamStatus.Text = $"Потоки подключены · {connected:N0} из {candidates.Count:N0}   |   Сделки за последние 15 с · {fresh:N0}";
        if (settings.VolumeModule && settings.VolumeGrowthThresholds.Any(t => t.Enabled))
        {
            var volume = streams.VolumeCoverage(markets, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            StreamStatus.Text += $"   |   Объём % · {volume.Ready:N0}/{markets.Count:N0} готово · история: {volume.Waiting:N0} · база 0: {volume.Zero:N0}" +
                (volume.Unsupported > 0 ? $" · недоступно: {volume.Unsupported:N0}" : "");
        }
        StreamStatus.ToolTip = string.Join("\n", status.GroupBy(s => s.Source).Select(g =>
            $"{g.Key.Label} · {g.Where(s => s.Connected).Sum(s => s.Count)}/{g.Sum(s => s.Count)}" +
            string.Concat(g.Where(s => !s.Connected).Select(s => s.Detail).Distinct().Select(s => $" · {s}")))) +
            (settings.VolumeModule ? $"\nБез потока сделок или подтверждённого USD-оборота: {markets.Count(m => !m.Streaming || !UsdQuotes.Supported(m))}. " +
                $"База: {WindowName(settings.VolumeReferenceSeconds)} перед текущим окном. Готовность по кратчайшему включённому окну. " +
                "Нужна непрерывная история базы и окна; при нулевой базе процент не определён. " +
                "Расчёт раз в секунду с задержкой 6 с. USD-стейблкоины ≈ $1." : "");
    }

    private void Place(Signal signal)
    {
        var market = signal.Market;
        if (!settings.ShowsSignalMarket(market) || !signalCooldown.TryAccept(market, settings.SignalCooldownMinutes)) return;
        notifications.Publish(signal);
        var visible = cells.Take(settings.Grid * settings.Grid).ToArray();
        if (visible.FirstOrDefault(c => c.Choice?.Key == market.Key) is { } existing)
        {
            existing.ShowSignal(signal);
            return;
        }
        int slot = ChartPlacement.Pick(market.Key, visible
            .Select(c => (c.Choice?.Key, c.Choice?.Pinned == true || c.IsInteracting || ReferenceEquals(c, fullscreenCell), c.Order)).ToArray());
        if (slot < 0) return;
        visible[slot].Assign(market, pinned: false, signal);
        Save();
    }

    private void PickMarket(int index)
    {
        picker = new MarketPicker(markets, CloseDialog, selected =>
        {
            if (!markets.Any(m => m.Key == selected.Key)) return;
            cells[index].Assign(selected, pinned: true);
            Save();
            CloseDialog();
        });
        ShowDialog(picker);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (settingsPanel != null) { settingsPanel.Close(); return; }
        if (DialogHost.Content != null) CloseDialog();
        settingsPanel = new SettingsPanel(settings, new Updater(http), colibri, SettingsChanged, CloseDialog, Close, settings.Overview,
            () => { notifications.SettingsChanged(); Save(); }, () => { RefreshOverview(); Save(); }, notifications.PreviewSound,
            neuralVoice, notifications.PreviewVoice, Save, () => { RefreshDiagnostics(); Save(); });
        settingsPanel.UpdateQuoteChoices(OverviewSourceQuotes());
        settingsPanel.UpdateFilter(CatalogStatus.Text);
        ShowDialog(settingsPanel);
        FitSettingsDialog();
    }

    private IEnumerable<string> OverviewSourceQuotes() =>
        catalogs.GetValueOrDefault(settings.OverviewSource)?.Markets.Select(m => m.Quote) ?? [];

    private void FitSettingsDialog()
    {
        if (settingsPanel == null || Overlay.ActualWidth == 0 || Overlay.ActualHeight == 0) return;
        settingsPanel.MaxWidth = Math.Max(0, Overlay.ActualWidth - DialogHost.Margin.Left - DialogHost.Margin.Right);
        settingsPanel.MaxHeight = Math.Max(0, Overlay.ActualHeight - DialogHost.Margin.Top - DialogHost.Margin.Bottom);
    }

    private void RefreshDiagnostics()
    {
        MonitorDiagnostics.Visibility = settings.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        MonitorNotice.Visibility = !settings.DebugMode && MonitorNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var cell in cells) cell.SetDebugMode(settings.DebugMode);
        overviewChart.SetDebugMode(settings.DebugMode);
        overviewPanel.UpdateDiagnostics();
        ShowIntegrationStatus();
        ShowCoverage();
    }

    private void OpenSignalListings(object sender, RoutedEventArgs e)
    {
        signalListings = new SignalListingsPanel(settings, catalogs, () => SettingsChanged(false), CloseDialog);
        ShowDialog(signalListings);
    }

    private async void SettingsChanged(bool thresholdsChanged)
    {
        UpdateIntegration();
        ApplyFilter(thresholdsChanged);
        RefreshOverview();
        foreach (var cell in cells) cell.UpdateFrames();
        overviewChart.UpdateFrames();
        overviewPanel.UpdateColumns();
        Save();
        if (!thresholdsChanged && (catalogs.Count == 0 || catalogs.Any(c => c.Value.Error != null &&
                (settings.Rule(c.Key) != ListingRule.Ignore || settings.OverviewRule(c.Key) != ListingRule.Ignore ||
                 c.Key == settings.OverviewSource)))) await ReloadCatalogs();
    }

    private void ShowDialog(FrameworkElement content)
    {
        previousFocus = Keyboard.FocusedElement;
        CancelChartGestures();
        Workspace.IsEnabled = false;
        Overlay.Visibility = Visibility.Visible;
        DialogHost.Content = content;
        UpdateNavigation();
    }

    private void CloseDialog()
    {
        settingsPanel?.Dispose();
        settingsPanel = null;
        signalListings = null;
        picker = null;
        DialogHost.Content = null;
        Overlay.Visibility = Visibility.Collapsed;
        Workspace.IsEnabled = true;
        UpdateNavigation();
        if (previousFocus != null) Keyboard.Focus(previousFocus);
        previousFocus = null;
    }

    private void UpdateNavigation()
    {
        var selected = settingsPanel != null ? SettingsButton : settings.Overview ? OverviewButton : SignalsButton;
        foreach (var button in new[] { SignalsButton, OverviewButton, SettingsButton })
        {
            bool active = ReferenceEquals(button, selected);
            button.Background = active ? Terminal.Brush("AccentSurface") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = Terminal.Brush(active ? "Accent" : "Text");
            button.BorderBrush = active ? Terminal.Brush("Accent") : System.Windows.Media.Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, active ? "Активный раздел" : "");
        }
    }

    private void Save()
    {
        settings.Cells = cells.Select(c => c.Choice).ToArray();
        settings.OverviewChoice = overviewChart.Choice;
        if (!persistSettings) return;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowCatalogStatus("Настройки не сохранены", ex.Message, "Настройки не сохранены");
        }
    }

    private void UpdateIntegration()
    {
        colibri.Configure();
        foreach (var cell in cells) cell.SetColibriEnabled(settings.ColibriEnabled);
        overviewPanel.UpdateIntegration();
        ShowIntegrationStatus();
    }

    private void ShowIntegrationStatus()
    {
        IntegrationStatusHost.Visibility = settings.ColibriEnabled && colibri.Status.Length > 0 &&
            (settings.DebugMode || colibri.HasError) ? Visibility.Visible : Visibility.Collapsed;
        IntegrationStatus.Text = "Colibri · " + colibri.Status;
        IntegrationStatus.Foreground = Terminal.Brush(colibri.HasError ? "Falling" : "Muted");
        IntegrationStatus.ToolTip = settings.DebugMode ? colibri.Status : null;
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        AccessKeyManager.Unregister("С", SignalsButton);
        AccessKeyManager.Unregister("О", OverviewButton);
        AccessKeyManager.Unregister("Н", SettingsButton);
        if (watchingNetwork) NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        colibri.Dispose();
        notifications.Dispose();
        neuralVoice.Dispose();
        timer.Stop();
        settingsPanel?.Dispose();
        streams?.Dispose();
        volumeFilter?.Dispose();
        spreadFilter?.Dispose();
        overviewPanel.Dispose();
        overviewChart.Activate(false);
        foreach (var cell in cells) cell.Activate(false);
        http.Dispose();
    }
}
