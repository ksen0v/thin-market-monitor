using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool loading;
    private long order;
    private DateTimeOffset nextRefresh;

    public MainWindow()
    {
        InitializeComponent();
        api = new(http);
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new ChartCell(api, PickMarket, Save, () => ++order) { Choice = settings.Cells[i] };
        order = cells.Max(cell => cell.Order);

        RebuildGrid();
        Loaded += async (_, _) =>
        {
            timer.Tick += Tick;
            timer.Start();
            await ReloadCatalogs();
        };
        Closed += (_, _) => Dispose();
    }

    private void ChangeGrid(object sender, RoutedEventArgs e)
    {
        settings.Grid = ReferenceEquals(sender, GridThree) ? 3 : 2;
        RebuildGrid();
        Save();
    }

    private void RebuildGrid()
    {
        Charts.Children.Clear();
        Charts.Rows = Charts.Columns = settings.Grid;
        GridTwo.IsEnabled = settings.Grid != 2;
        GridThree.IsEnabled = settings.Grid != 3;
        int visible = settings.Grid * settings.Grid;
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Activate(i < visible);
            if (i < visible) Charts.Children.Add(cells[i]);
        }
    }

    private async void RefreshCatalogs(object sender, RoutedEventArgs e) => await ReloadCatalogs();

    private async Task ReloadCatalogs()
    {
        if (loading) return;
        loading = true;
        CatalogStatus.Text = "Обновление каталогов…";
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

    private void ApplyFilter(bool force)
    {
        var result = ListingFilter.Apply(catalogs, settings.Rules);
        var errors = catalogs.Where(x => x.Value.Error != null).ToArray();
        CatalogStatus.Text = result.Missing.Count > 0
            ? $"Фильтр ожидает каталоги · {string.Join(", ", result.Missing.Select(v => v.Label()))}"
            : $"{result.Markets.Count:N0} монет после фильтра" +
              (errors.Length > 0 ? $" · недоступны {string.Join(", ", errors.Select(x => x.Key.Label()))}" : "");
        CatalogStatus.ToolTip = errors.Length == 0 ? null : string.Join("\n", errors.Select(x => $"{x.Key.Label()} — {x.Value.Error}"));
        bool same = markets.SequenceEqual(result.Markets);
        markets = result.Markets;
        if (!same || force)
        {
            var previous = streams;
            previous?.Dispose();
            streams = markets.Count > 0 && settings.Thresholds.Any(t => t.Enabled)
                ? new MarketStreams(api, markets, settings.Thresholds, force ? null : previous) : null;
        }

        var lookup = markets.ToDictionary(x => x.Key);
        foreach (var cell in cells)
        {
            if (cell.Choice == null) continue;
            lookup.TryGetValue(cell.Choice.Key, out var market);
            cell.Resolve(market);
        }
        ShowCoverage();
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (!loading && DateTimeOffset.UtcNow >= nextRefresh) await ReloadCatalogs();
        if (stop.IsCancellationRequested) return;
        for (int i = 0; i < 150 && streams != null && streams.TryRead(out var signal); i++)
        {
            var entry = signal!;
            var item = new ListBoxItem
            {
                Content = $"{DateTimeOffset.FromUnixTimeMilliseconds(entry.Time).ToLocalTime():HH:mm:ss}     " +
                          $"{entry.Market.Label}     +{entry.Percent:F2}% за {WindowName(entry.Seconds)}",
                Tag = entry.Market.Key, Padding = new Thickness(3, 1, 3, 1)
            };
            Journal.Items.Insert(0, item);
            if (Journal.Items.Count > 200) Journal.Items.RemoveAt(Journal.Items.Count - 1);
            Place(entry.Market, false);
        }
        ShowCoverage();
    }

    internal static string WindowName(int seconds) => seconds switch { 15 => "15 с", 60 => "1 мин", _ => "15 мин" };

    private void ShowCoverage()
    {
        if (streams == null)
        {
            StreamStatus.Text = settings.Thresholds.Any(t => t.Enabled) ? "Сигналы ожидают рынки" : "Сигналы выключены";
            StreamStatus.ToolTip = null;
            return;
        }
        var status = streams.Status();
        int connected = status.Where(s => s.Connected).Sum(s => s.Count);
        int fresh = streams.FreshCount(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        StreamStatus.Text = $"Потоки подключены · {connected:N0} из {markets.Count:N0}   |   Сделки за последние 15 с · {fresh:N0}";
        StreamStatus.ToolTip = string.Join("\n", status.GroupBy(s => s.Venue).Select(g =>
            $"{g.Key.Label()} · {g.Where(s => s.Connected).Sum(s => s.Count)}/{g.Sum(s => s.Count)}" +
            string.Concat(g.Where(s => !s.Connected).Select(s => s.Detail).Distinct().Select(s => $" · {s}"))));
    }

    private void OpenJournalMarket(object sender, MouseButtonEventArgs e)
    {
        if (Journal.SelectedItem is ListBoxItem { Tag: string key } && markets.FirstOrDefault(m => m.Key == key) is { } market)
            Place(market, true);
    }

    private void Place(Market market, bool pinned)
    {
        var visible = cells.Take(settings.Grid * settings.Grid).ToArray();
        if (pinned && visible.FirstOrDefault(c => c.Choice?.Key == market.Key) is { } existing)
        {
            existing.SetPinned(true);
            Save();
            return;
        }
        int slot = ChartPlacement.Pick(market.Key, visible
            .Select(c => (c.Choice?.Key, c.Choice?.Pinned == true, c.Order)).ToArray());
        if (slot < 0) return;
        visible[slot].Assign(market, pinned);
        Save();
    }

    private Market? PickMarket()
    {
        var chooser = new MarketPicker(markets) { Owner = this };
        return chooser.ShowDialog() == true ? chooser.Selected : null;
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings, catalogs, new Updater(http)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Restarting)
        {
            Close();
            return;
        }
        bool thresholdsChanged = !settings.Thresholds.SequenceEqual(dialog.Thresholds);
        settings.Rules = dialog.Rules;
        settings.Thresholds = dialog.Thresholds;
        Save();
        ApplyFilter(thresholdsChanged);
        if (catalogs.Count == 0 || catalogs.Any(x => x.Value.Error != null && settings.Rules.GetValueOrDefault(x.Key) != ListingRule.Ignore))
            await ReloadCatalogs();
    }

    private void Save()
    {
        settings.Cells = cells.Select(c => c.Choice).ToArray();
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CatalogStatus.Text = "Настройки не сохранены";
            CatalogStatus.ToolTip = ex.Message;
        }
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        timer.Stop();
        streams?.Dispose();
        foreach (var cell in cells) cell.Activate(false);
        http.Dispose();
    }
}
