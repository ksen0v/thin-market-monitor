using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ThinMarketMonitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(16) };
    private readonly CancellationTokenSource stop = new();
    private readonly ExchangeApi api;
    private readonly Settings settings = Settings.Load();
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
    private HashSet<string> eligible = [];
    private MarketStreams? streams;
    private SettingsPanel? settingsPanel;
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

    public MainWindow() : this(startMonitoring: true) { }

    internal MainWindow(bool startMonitoring)
    {
        InitializeComponent();
        api = new(http);
        var mark = Terminal.Icon("candles");
        System.Windows.Documents.TextElement.SetForeground(mark, Terminal.Brush("Accent"));
        BrandMark.Child = mark;
        SettingsButton.Content = Terminal.LabelWithIcon("settings", "Настройки", 5);
        for (int i = 0; i < cells.Length; i++)
        {
            int slot = i;
            var cell = new ChartCell(api, i + 1, () => PickMarket(slot), Save, () => ++order, () => settings.Ruler, () => settings.FrameButtons)
                { Choice = settings.Cells[i] };
            cells[i] = cell;
            cell.PreviewMouseDown += (_, _) => activeCell = cell;
            cell.GotKeyboardFocus += (_, _) => activeCell = cell;
            cell.FrameChanged += _ => Save();
        }
        overviewChart = new ChartCell(api, 0, () =>
        {
            settings.OverviewDrawer = true;
            RefreshOverview();
            Save();
        }, Save, () => ++order, () => settings.Ruler, () => settings.FrameButtons)
        {
            Choice = settings.OverviewChoice,
            PlotHeight = double.NaN
        };
        overviewChart.SetFrame(settings.OverviewFrame);
        overviewChart.FrameChanged += value => { settings.OverviewFrame = value; Save(); };
        overviewChart.PreviewMouseDown += (_, _) => activeCell = overviewChart;
        overviewChart.GotKeyboardFocus += (_, _) => activeCell = overviewChart;
        overviewPanel = new OverviewPanel(api, settings, () => { RefreshOverview(); Save(); }, selected =>
        {
            overviewChart.Assign(selected, pinned: false);
            activeCell = overviewChart;
            Save();
        }, () => OpenSettings(this, new RoutedEventArgs(), overview: true));
        OverviewDrawerHost.Content = overviewPanel;
        OverviewChartHost.Content = overviewChart;
        order = cells.Max(cell => cell.Order);

        RebuildGrid();
        RefreshOverview();
        if (startMonitoring) Loaded += async (_, _) =>
        {
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
        if (e.Key == Key.Escape && fullscreenCell != null) { ExitFullscreen(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.Modifiers != ModifierKeys.None) return;
        var underMouse = overviewChart.IsVisible && overviewChart.IsMouseOver ? overviewChart
            : cells.FirstOrDefault(c => c.IsMouseOver && c.IsVisible);
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
        foreach (var cell in cells) cell.Plot.CancelGesture();
        overviewChart.Plot.CancelGesture();
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
        FullChart.Content = cell;
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
        previous.Margin = new Thickness(0, 0, 1, 1);
        fullscreenCell = null;
        FullChart.Content = null;
        FullChart.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
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
            Charts.Height = Math.Max(settings.Grid * 116, ChartScroll.ActualHeight - ChartScroll.Padding.Top);
    }

    private void ChangeWorkspace(object sender, RoutedEventArgs e)
    {
        settings.Overview = !settings.Overview;
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
        GridTwo.Visibility = GridThree.Visibility = settings.Overview ? Visibility.Collapsed : Visibility.Visible;
        DrawerSwitch.Visibility = settings.Overview ? Visibility.Visible : Visibility.Collapsed;
        SpaceSwitch.Content = settings.Overview ? "Сигналы" : "Обзор";
        DrawerSwitch.Content = settings.OverviewDrawer ? "Скрыть инструменты" : "Инструменты";
        OverviewDrawerHost.Visibility = settings.OverviewDrawer ? Visibility.Visible : Visibility.Collapsed;
        var catalog = catalogs.GetValueOrDefault(settings.OverviewSource);
        if (overviewChart.Choice?.Key != settings.OverviewChoice?.Key) overviewChart.Choice = settings.OverviewChoice;
        overviewChart.Resolve(catalog?.Markets.FirstOrDefault(m => m.Key == settings.OverviewChoice?.Key));
        overviewChart.Activate(settings.Overview);
        overviewPanel.SetSource(catalog, settings.Overview && settings.OverviewDrawer);
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
            PickMarket(0);
            UpdateLayout();
            if (DialogHost.Content is not MarketPicker)
                throw new InvalidOperationException("The market picker did not open.");
            CloseDialog();
            EnterFullscreen(cells[0], changeWindow: false);
            if (!ReferenceEquals(FullChart.Content, cells[0])) throw new InvalidOperationException("Fullscreen did not retain the chart.");
            ExitFullscreen();
            if (!Charts.Children.Contains(cells[0])) throw new InvalidOperationException("Fullscreen did not restore the chart.");
            overviewPanel.SetSource(new Catalog([new Market(Venue.KuCoin, "CHECK-USDT", "CHECK", "USDT")]), false);
            overviewPanel.SetSource(new Catalog([]), false);
            settings.Overview = true;
            RefreshOverview();
            UpdateLayout();
            if (GridSpace.IsVisible || !OverviewSpace.IsVisible || overviewChart.Plot.ActualHeight <= 0)
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

    private async Task ReloadCatalogs()
    {
        if (loading) return;
        loading = true;
        ShowCatalogStatus("Обновление каталогов…");
        try
        {
            await Task.WhenAll(MarketSource.All.Select(async v =>
            {
                var catalog = await api.LoadCatalog(v, stop.Token);
                if (!stop.IsCancellationRequested)
                {
                    catalogs[v] = catalog;
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
            nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
        }
    }

    private void ApplyFilter(bool thresholdsChanged)
    {
        var result = ListingFilter.Apply(catalogs, settings.Rule);
        missing = result.Missing.ToArray();
        bool candidatesChanged = !candidates.SequenceEqual(result.Markets);
        if (candidatesChanged) candidates = result.Markets;
        var nextRules = settings.VolumeFilters.Where(r => r.Enabled).ToArray();
        if (candidatesChanged || !filterRules.SequenceEqual(nextRules))
        {
            filterRules = nextRules;
            volumeFilter?.Dispose();
            volumeFilter = nextRules.Length > 0 && candidates.Count > 0 ? new(api, candidates, nextRules) : null;
        }
        RefreshEligibility();
        if (candidatesChanged || thresholdsChanged)
        {
            var previous = streams;
            previous?.Dispose();
            var price = settings.PriceModule ? settings.Thresholds.Where(t => t.Enabled).ToArray() : [];
            var volume = settings.VolumeModule ? settings.VolumeThresholds.Where(t => t.Enabled).ToArray() : [];
            streams = candidates.Count > 0 && (price.Length > 0 || volume.Length > 0)
                ? new MarketStreams(api, candidates, price, volume, markets, thresholdsChanged ? null : previous) : null;
        }
        ShowCoverage();
    }

    private void RefreshEligibility()
    {
        var filtered = volumeFilter?.Evaluate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        IReadOnlyList<Market> next = filtered?.Markets ?? candidates;
        var errors = catalogs.Where(x => x.Value.Error != null).ToArray();
        string status = missing.Length > 0
            ? $"Фильтр ожидает каталоги · {string.Join(", ", missing.Select(v => v.Label))}"
            : $"{CountLabel(next.Count)} после фильтра" +
              (filtered is { Pending: > 0 } pending ? $" · объём ожидается: {pending.Pending:N0}" : "") +
              (filtered is { Unsupported: > 0 } unsupported ? $" · без котировки USD: {unsupported.Unsupported:N0}" : "") +
              (errors.Length > 0 ? $" · недоступно источников: {errors.Length}" : "");
        string? tooltip = errors.Length == 0 ? null : string.Join("\n", errors.Select(x => $"{x.Key.Label} — {x.Value.Error}"));
        if (CatalogStatus.Text != status || !Equals(CatalogStatus.ToolTip, tooltip)) ShowCatalogStatus(status, tooltip);
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

    private void ShowCatalogStatus(string status, string? tooltip = null)
    {
        CatalogStatus.Text = status;
        CatalogStatus.ToolTip = tooltip;
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
        if (!loading && DateTimeOffset.UtcNow >= nextRefresh) await ReloadCatalogs();
        if (stop.IsCancellationRequested) return;
        if (!loading && volumeFilter != null) RefreshEligibility();
        for (int i = 0; i < 150 && streams != null && streams.TryRead(out var signal); i++)
            if (eligible.Contains(signal!.Market.Key)) Place(signal);
        ShowCoverage();
    }

    internal static string WindowName(int seconds) => seconds % 3600 == 0 ? $"{seconds / 3600} ч" :
        seconds % 60 == 0 ? $"{seconds / 60} мин" : $"{seconds} с";

    private void ShowCoverage()
    {
        if (streams == null)
        {
            StreamLed.Fill = Terminal.Brush("Muted");
            bool enabled = settings.PriceModule && settings.Thresholds.Any(t => t.Enabled) ||
                settings.VolumeModule && settings.VolumeThresholds.Any(t => t.Enabled);
            StreamStatus.Text = enabled ? "Сигналы ожидают рынки" : "Сигналы выключены";
            StreamStatus.ToolTip = null;
            return;
        }
        var status = streams.Status();
        int connected = status.Where(s => s.Connected).Sum(s => s.Count);
        int fresh = streams.FreshCount(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StreamLed.Fill = Terminal.Brush(connected > 0 ? "Rising" : "Muted");
        StreamStatus.Text = $"Потоки подключены · {connected:N0} из {candidates.Count:N0}   |   Сделки за последние 15 с · {fresh:N0}";
        if (settings.VolumeModule && settings.VolumeThresholds.Any(t => t.Enabled))
            StreamStatus.Text += $"   |   Объём USD · {streams.VolumeReadyCount(markets, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()):N0}" +
                $"/{markets.Count(UsdQuotes.Supported):N0} готово";
        StreamStatus.ToolTip = string.Join("\n", status.GroupBy(s => s.Source).Select(g =>
            $"{g.Key.Label} · {g.Where(s => s.Connected).Sum(s => s.Count)}/{g.Sum(s => s.Count)}" +
            string.Concat(g.Where(s => !s.Connected).Select(s => s.Detail).Distinct().Select(s => $" · {s}")))) +
            (settings.VolumeModule ? $"\nБез подтверждённого USD-оборота: {markets.Count(m => !UsdQuotes.Supported(m))}. " +
                "Готовность по кратчайшему включённому окну. USD-стейблкоины ≈ $1." : "");
    }

    private void Place(Signal signal)
    {
        var market = signal.Market;
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

    private void OpenSettings(object sender, RoutedEventArgs e) => OpenSettings(sender, e, overview: false);

    private void OpenSettings(object sender, RoutedEventArgs e, bool overview)
    {
        settingsPanel = new SettingsPanel(settings, catalogs, new Updater(http), SettingsChanged, CloseDialog, Close, overview);
        settingsPanel.UpdateFilter(CatalogStatus.Text);
        ShowDialog(settingsPanel);
    }

    private async void SettingsChanged(bool thresholdsChanged)
    {
        ApplyFilter(thresholdsChanged);
        foreach (var cell in cells) cell.UpdateFrames();
        overviewChart.UpdateFrames();
        overviewPanel.UpdateColumns();
        Save();
        if (!thresholdsChanged && (catalogs.Count == 0 || catalogs.Any(c => c.Value.Error != null &&
                settings.Rule(c.Key) != ListingRule.Ignore))) await ReloadCatalogs();
    }

    private void ShowDialog(FrameworkElement content)
    {
        previousFocus = Keyboard.FocusedElement;
        CancelChartGestures();
        Workspace.IsEnabled = false;
        Overlay.Visibility = Visibility.Visible;
        DialogHost.Content = content;
    }

    private void CloseDialog()
    {
        settingsPanel?.Dispose();
        settingsPanel = null;
        picker = null;
        DialogHost.Content = null;
        Overlay.Visibility = Visibility.Collapsed;
        Workspace.IsEnabled = true;
        if (previousFocus != null) Keyboard.Focus(previousFocus);
        previousFocus = null;
    }

    private void Save()
    {
        settings.Cells = cells.Select(c => c.Choice).ToArray();
        settings.OverviewChoice = overviewChart.Choice;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowCatalogStatus("Настройки не сохранены", ex.Message);
        }
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        timer.Stop();
        settingsPanel?.Dispose();
        streams?.Dispose();
        volumeFilter?.Dispose();
        overviewPanel.Dispose();
        overviewChart.Activate(false);
        foreach (var cell in cells) cell.Activate(false);
        http.Dispose();
    }
}
