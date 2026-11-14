using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ThinMarketMonitor;

internal static class OverviewMetrics
{
    public static string Label(OverviewColumn c) =>
        (c.Kind switch { MetricKind.Turnover => "Оборот $", MetricKind.Change => "Изм. %", _ => "ATR %" }) +
        $" · {MainWindow.WindowName(c.Minutes * 60)}";

    public static double? Calculate(Market m, Candle[] bars, int frame, long end, OverviewColumn metric)
    {
        long interval = frame * 1000L;
        int count = metric.Minutes * 60 / frame;
        long first = end - count * interval;
        int previous = Array.FindIndex(bars, c => c.Time == first - interval);
        var current = bars.Where(c => c.Time >= first && c.Time < end).ToArray();
        if (current.Length != count || current.Where((c, index) => c.Time != first + index * interval).Any()) return null;
        if (metric.Kind == MetricKind.Turnover)
            return UsdQuotes.Supported(m) && current.All(c => c.QuoteVolume.HasValue) ? current.Sum(c => c.QuoteVolume!.Value) : null;
        if (previous < 0) return null;
        double prior = bars[previous].Close, last = current[^1].Close;
        if (metric.Kind == MetricKind.Change) return (last / prior - 1) * 100;
        double total = 0;
        foreach (var candle in current)
        {
            total += Math.Max(candle.High - candle.Low, Math.Max(Math.Abs(candle.High - prior), Math.Abs(candle.Low - prior)));
            prior = candle.Close;
        }
        return total / count / last * 100;
    }
}

internal sealed class OverviewPanel : DockPanel, IDisposable
{
    private readonly ExchangeApi api;
    private readonly Settings settings;
    private readonly Action changed;
    private readonly Action<Market> select;
    private readonly ComboBox source = new() { MinWidth = 165, Margin = new Thickness(0, 0, 4, 5) };
    private readonly DataGrid table = new()
    {
        IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, GridLinesVisibility = DataGridGridLinesVisibility.None,
        SelectionMode = DataGridSelectionMode.Single
    };
    private readonly TextBlock status = Terminal.Copy("");
    private Row[] rows = [];
    private ListCollectionView view;
    private CancellationTokenSource? worker;
    private Market[] markets = [];
    private bool loadingSelection;
    private bool shown;
    private bool loadedCatalog;
    private OverviewColumn[] columns = [];

    public OverviewPanel(ExchangeApi api, Settings settings, Action changed, Action<Market> select, Action edit)
    {
        this.api = api;
        this.settings = settings;
        this.changed = changed;
        this.select = select;
        Width = 390;
        Background = Terminal.Brush("Surface");
        Margin = new Thickness(1, 0, 0, 0);
        var header = new DockPanel { Margin = new Thickness(7, 7, 7, 0) };
        var options = new Button { Content = "Колонки", Margin = new Thickness(0, 0, 0, 5) };
        options.Click += (_, _) => edit();
        SetDock(options, Dock.Right);
        header.Children.Add(options);
        source.ItemsSource = MarketSource.All;
        source.SelectedItem = settings.OverviewSource;
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is not MarketSource next || next == settings.OverviewSource) return;
            settings.OverviewSource = next;
            settings.OverviewChoice = null;
            loadedCatalog = false;
            changed();
        };
        header.Children.Add(source);
        SetDock(header, Dock.Top);
        Children.Add(header);
        status.Margin = new Thickness(8, 4, 8, 5);
        SetDock(status, Dock.Bottom);
        Children.Add(status);
        view = new ListCollectionView(rows);
        table.Background = Terminal.Brush("Plot");
        table.Foreground = Terminal.Brush("Text");
        table.RowBackground = Terminal.Brush("Plot");
        table.AlternatingRowBackground = Terminal.Brush("Surface");
        table.BorderBrush = Terminal.Brush("Outline");
        table.Resources[typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)] =
            MakeStyle(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), "Control");
        table.Resources[typeof(DataGridCell)] = MakeStyle(typeof(DataGridCell), "Plot");
        table.ItemsSource = view;
        table.SelectionChanged += (_, _) =>
        {
            if (!loadingSelection && table.SelectedItem is Row selected) select(selected.Market);
        };
        table.Sorting += (_, e) =>
        {
            e.Handled = true;
            int key = table.Columns.IndexOf(e.Column) - 1;
            settings.OverviewDescending = settings.OverviewSort == key ? !settings.OverviewDescending : key >= 0;
            settings.OverviewSort = key;
            Sort();
            changed();
        };
        Children.Add(table);
        UpdateColumns();
    }

    private static Style MakeStyle(Type type, string bg)
    {
        var style = new Style(type);
        style.Setters.Add(new Setter(Control.BackgroundProperty, Terminal.Brush(bg)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Terminal.Brush("Text")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Terminal.Brush("Outline")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
        return style;
    }

    public void UpdateColumns()
    {
        if (columns.SequenceEqual(settings.OverviewColumns) && table.Columns.Count > 0) return;
        columns = settings.OverviewColumns.ToArray();
        table.Columns.Clear();
        table.Columns.Add(new DataGridTextColumn { Header = "Тикер", Binding = new Binding("Ticker"), MinWidth = 95 });
        for (int i = 0; i < columns.Length; i++)
        {
            var data = columns[i];
            table.Columns.Add(new DataGridTextColumn
            {
                Header = OverviewMetrics.Label(data), Binding = new Binding($"[{i}]"),
                Width = DataGridLength.Auto, MinWidth = 85
            });
        }
        Sort();
        Restart();
    }

    public void SetSource(Catalog? catalog, bool visible)
    {
        shown = visible;
        var next = catalog?.Markets.ToArray() ?? [];
        if (!loadedCatalog || !markets.SequenceEqual(next))
        {
            loadedCatalog = true;
            markets = next;
            loadingSelection = true;
            rows = next.OrderBy(m => m.Symbol, StringComparer.Ordinal).ThenBy(m => m.Contract?.Product)
                .Select(market => new Row(market, columns)).ToArray();
            view = new ListCollectionView(rows);
            table.ItemsSource = view;
            Sort();
            loadingSelection = false;
            Restart();
        }
        else if (visible && worker == null && rows.Length > 0) Restart();
        else if (!visible) Cancel();
        if (next.Length == 0) status.Text = catalog?.Error == null ? "Ожидание каталога" : "Каталог недоступен · " + catalog.Error;
    }

    private void Sort()
    {
        bool wasLoading = loadingSelection;
        loadingSelection = true;
        view.CustomSort = new RowComparer(settings.OverviewSort, settings.OverviewDescending);
        RestoreSelection();
        loadingSelection = wasLoading;
        for (int i = 0; i < table.Columns.Count; i++)
            table.Columns[i].SortDirection = i - 1 == settings.OverviewSort
                ? settings.OverviewDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
    }

    private void Restart()
    {
        Cancel();
        if (!shown || rows.Length == 0) return;
        var run = new CancellationTokenSource();
        worker = run;
        _ = Load(rows.ToArray(), columns.ToArray(), run);
    }

    private async Task Load(Row[] entries, OverviewColumn[] metrics, CancellationTokenSource run)
    {
        var ct = run.Token;
        try
        {
            while (true)
            {
                var snapshots = new Dictionary<string, MarketSnapshot>();
                if (entries[0].Market.Venue != Venue.Alpha && metrics.Any(m => m.Minutes == 1440 && m.Kind != MetricKind.Atr))
                {
                    try { snapshots = await api.Snapshots(entries.Select(r => r.Market).ToArray(), ct); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
                int done = 0;
                foreach (var row in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var snapshot = snapshots.GetValueOrDefault(row.Market.Key);
                    try
                    {
                        if (row.Market.Venue == Venue.Alpha && metrics.Any(m => m.Minutes == 1440 && m.Kind != MetricKind.Atr))
                            snapshot = await api.AlphaSnapshot(row.Market, ct);
                        var values = new double?[metrics.Length];
                        var history = new List<int>();
                        for (int i = 0; i < metrics.Length; i++)
                        {
                            var m = metrics[i];
                            if (m.Minutes == 1440 && m.Kind != MetricKind.Atr)
                                values[i] = m.Kind == MetricKind.Turnover ? snapshot?.Turnover : snapshot?.Change;
                            if (values[i] == null && (m.Kind != MetricKind.Turnover || UsdQuotes.Supported(row.Market))) history.Add(i);
                        }
                        if (history.Count > 0)
                        {
                            int frame = new[] { 900, 300, 60 }.First(f => history.All(i => metrics[i].Minutes * 60 % f == 0));
                            long unit = frame * 1000L;
                            long end = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000) / unit * unit;
                            int count = history.Max(i => metrics[i].Minutes * 60 / frame) + 1;
                            var bars = await api.CandlePage(row.Market, frame, end, count, ct);
                            foreach (int i in history) values[i] = OverviewMetrics.Calculate(row.Market, bars, frame, end, metrics[i]);
                        }
                        ct.ThrowIfCancellationRequested();
                        row.Set(values, metrics);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        var fallback = metrics.Select(m => m.Minutes == 1440
                            ? m.Kind == MetricKind.Turnover ? snapshot?.Turnover : m.Kind == MetricKind.Change ? snapshot?.Change : null
                            : null).ToArray();
                        row.Set(fallback, metrics);
                    }
                    done++;
                    if (done % 12 == 0 || done == entries.Length)
                    {
                        status.Text = $"{done:N0} / {entries.Length:N0} · — нет данных";
                        if (settings.OverviewSort >= 0) RefreshSort();
                    }
                    if (done % 24 == 0) await Task.Yield();
                }
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        finally { if (ReferenceEquals(worker, run)) worker = null; run.Dispose(); }
    }

    private void RefreshSort()
    {
        loadingSelection = true;
        view.Refresh();
        RestoreSelection();
        loadingSelection = false;
    }

    private void RestoreSelection() =>
        table.SelectedItem = rows.FirstOrDefault(r => r.Market.Key == settings.OverviewChoice?.Key);

    public void Cancel()
    {
        worker?.Cancel();
        worker = null;
    }

    public void Dispose() => Cancel();

    internal sealed class Row(Market market, OverviewColumn[] metrics) : INotifyPropertyChanged
    {
        private double?[] values = new double?[metrics.Length];
        private OverviewColumn[] columns = metrics;
        public Market Market { get; } = market;
        public string Ticker => Market.Ticker;
        public string this[int i] => Number(i) is { } number
            ? number.ToString(columns[i].Kind == MetricKind.Turnover ? "N0" : "+0.00;-0.00;0.00", CultureInfo.CurrentCulture)
            : "—";
        public double? Number(int i) => i < values.Length ? values[i] : null;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(double?[] next, OverviewColumn[] kinds)
        {
            values = next;
            columns = kinds;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    internal sealed class RowComparer(int key, bool desc) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not Row a || y is not Row b) return 0;
            int ticker = StringComparer.Ordinal.Compare(a.Ticker, b.Ticker);
            if (ticker == 0) ticker = StringComparer.Ordinal.Compare(a.Market.Key, b.Market.Key);
            if (key < 0) return desc ? -ticker : ticker;
            double? av = a.Number(key), bv = b.Number(key);
            if (!av.HasValue || !bv.HasValue) return av.HasValue ? -1 : bv.HasValue ? 1 : ticker;
            int value = av.Value.CompareTo(bv.Value);
            return value == 0 ? ticker : desc ? -value : value;
        }
    }
}
