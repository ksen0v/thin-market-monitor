using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
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
    private readonly Dictionary<Venue, Catalog> catalogs = new();
    private readonly ChartCell[] cells = new ChartCell[9];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<Market> markets = [];
    private MarketStreams? streams;
    private SettingsPanel? settingsPanel;
    private MarketPicker? picker;
    private IInputElement? previousFocus;
    private bool loading;
    private long order;
    private DateTimeOffset nextRefresh;

    public MainWindow()
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
            cells[i] = new ChartCell(api, i + 1, () => PickMarket(slot), Save, () => ++order) { Choice = settings.Cells[i] };
        }
        order = cells.Max(cell => cell.Order);

        RebuildGrid();
        Loaded += async (_, _) =>
        {
            timer.Tick += Tick;
            timer.Start();
            await ReloadCatalogs();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Overlay.Visibility != Visibility.Visible) return;
            if (settingsPanel != null) settingsPanel.Close();
            else CloseDialog();
            e.Handled = true;
        };
        Closing += (_, e) => { if (settingsPanel?.Installing == true) e.Cancel = true; };
        Closed += (_, _) => Dispose();
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
            cells[i].PlotHeight = settings.Grid == 3 ? 140 : 235;
            cells[i].Activate(i < visible);
            if (i < visible) Charts.Children.Add(cells[i]);
        }
    }

    internal void SmokeTestLayout()
    {
        UpdateLayout();
        int original = settings.Grid;
        try
        {
            foreach (int size in new[] { 2, 3 })
            {
                settings.Grid = size;
                RebuildGrid();
                UpdateLayout();
                if (Charts.Children.Count != size * size) throw new InvalidOperationException("Chart grid is incomplete.");
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
        }
        finally
        {
            settings.Grid = original;
            RebuildGrid();
        }
    }

    private async Task ReloadCatalogs()
    {
        if (loading) return;
        loading = true;
        ShowCatalogStatus("Обновление каталогов…");
        try
        {
            var all = await Task.WhenAll(Enum.GetValues<Venue>().Select(async v => (v, await api.LoadCatalog(v, stop.Token))));
            foreach (var (venue, catalog) in all) catalogs[venue] = catalog;
            ApplyFilter(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            loading = false;
            nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
        }
    }

    private void ApplyFilter(bool thresholdsChanged)
    {
        var result = ListingFilter.Apply(catalogs, settings.Rules);
        var errors = catalogs.Where(x => x.Value.Error != null).ToArray();
        var status = result.Missing.Count > 0
            ? $"Фильтр ожидает каталоги · {string.Join(", ", result.Missing.Select(v => v.Label()))}"
            : $"{CountLabel(result.Markets.Count)} после фильтра" +
              (errors.Length > 0 ? $" · недоступны {string.Join(", ", errors.Select(x => x.Key.Label()))}" : "");
        ShowCatalogStatus(status, errors.Length == 0 ? null : string.Join("\n",
            errors.Select(x => $"{x.Key.Label()} — {x.Value.Error}")));

        bool same = markets.SequenceEqual(result.Markets);
        if (!same) markets = result.Markets;
        if (!same || thresholdsChanged)
        {
            var previous = streams;
            previous?.Dispose();
            streams = markets.Count > 0 && settings.Thresholds.Any(t => t.Enabled)
                ? new MarketStreams(api, markets, settings.Thresholds.ToArray(), thresholdsChanged ? null : previous) : null;
        }

        if (!same)
        {
            var lookup = markets.ToDictionary(x => x.Key);
            foreach (var cell in cells)
            {
                if (cell.Choice == null) continue;
                lookup.TryGetValue(cell.Choice.Key, out var market);
                cell.Resolve(market);
            }
            picker?.UpdateMarkets(markets);
        }
        ShowCoverage();
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
        for (int i = 0; i < 150 && streams != null && streams.TryRead(out var signal); i++)
            Place(signal!.Market);
        ShowCoverage();
    }

    internal static string WindowName(int seconds) => seconds switch { 15 => "15 с", 60 => "1 мин", _ => "15 мин" };

    private void ShowCoverage()
    {
        if (streams == null)
        {
            StreamLed.Fill = Terminal.Brush("Muted");
            StreamStatus.Text = settings.Thresholds.Any(t => t.Enabled) ? "Сигналы ожидают рынки" : "Сигналы выключены";
            StreamStatus.ToolTip = null;
            return;
        }
        var status = streams.Status();
        int connected = status.Where(s => s.Connected).Sum(s => s.Count);
        int fresh = streams.FreshCount(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StreamLed.Fill = Terminal.Brush(connected > 0 ? "Rising" : "Muted");
        StreamStatus.Text = $"Потоки подключены · {connected:N0} из {markets.Count:N0}   |   Сделки за последние 15 с · {fresh:N0}";
        StreamStatus.ToolTip = string.Join("\n", status.GroupBy(s => s.Venue).Select(g =>
            $"{g.Key.Label()} · {g.Where(s => s.Connected).Sum(s => s.Count)}/{g.Sum(s => s.Count)}" +
            string.Concat(g.Where(s => !s.Connected).Select(s => s.Detail).Distinct().Select(s => $" · {s}"))));
    }

    private void Place(Market market)
    {
        var visible = cells.Take(settings.Grid * settings.Grid).ToArray();
        int slot = ChartPlacement.Pick(market.Key, visible
            .Select(c => (c.Choice?.Key, c.Choice?.Pinned == true, c.Order)).ToArray());
        if (slot < 0) return;
        visible[slot].Assign(market, pinned: false);
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
        settingsPanel = new SettingsPanel(settings, catalogs, new Updater(http), SettingsChanged, CloseDialog, Close);
        settingsPanel.UpdateFilter(CatalogStatus.Text);
        ShowDialog(settingsPanel);
    }

    private async void SettingsChanged(bool thresholdsChanged)
    {
        ApplyFilter(thresholdsChanged);
        Save();
        if (!thresholdsChanged) await ReloadCatalogs();
    }

    private void ShowDialog(FrameworkElement content)
    {
        previousFocus = Keyboard.FocusedElement;
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
        foreach (var cell in cells) cell.Activate(false);
        http.Dispose();
    }
}
