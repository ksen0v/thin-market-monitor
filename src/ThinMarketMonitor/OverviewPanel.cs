using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal static class OverviewMetrics
{
    public static string Label(OverviewColumn c) =>
        c.Kind == MetricKind.Spread ? "Спред %" : (c.Kind switch { MetricKind.Turnover => "Оборот $", MetricKind.Change => "Изм. %", _ => "ATR %" }) +
        $" · {MainWindow.WindowName(c.Minutes * 60)}";

    public static double? Calculate(Market m, Candle[] bars, int frame, long end, OverviewColumn metric)
    {
        if (metric.Kind == MetricKind.Spread) return null;
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
    private readonly Button source = new() { MinHeight = 32, Margin = new Thickness(8, 7, 8, 7),
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Popup sourceMenu = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
    private readonly Dictionary<MarketSource, (Button Row, TextBlock Mark)> sourceOptions = new();
    private readonly Button sourceMode = new() { MinHeight = 25, Margin = new Thickness(0, 4, 5, 4) };
    private readonly TextBlock sourceLegend = Terminal.Copy("");
    private bool choosingSource;
    private readonly DataGrid table = new()
    {
        IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, GridLinesVisibility = DataGridGridLinesVisibility.None,
        SelectionMode = DataGridSelectionMode.Single, AlternationCount = 2,
        CanUserReorderColumns = false, CanUserResizeColumns = false, CanUserResizeRows = false,
        BorderThickness = new Thickness(0)
    };
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private Row[] rows = [];
    private ListCollectionView view;
    private CancellationTokenSource? worker;
    private OverviewSpreadSampler? spreadQuotes;
    private readonly TimeSpan spreadInterval;
    private readonly TimeSpan spreadTimeout;
    private Market[] markets = [];
    private MarketSource boundSource;
    private string? catalogDetails;
    private string? spreadDetails;
    private bool loadingSelection;
    private bool shown;
    private bool loadedCatalog;
    private bool userSelecting;
    private readonly CheckBox colibriSync = new() { Content = "Colibri", Margin = new Thickness(9, 0, 9, 7),
        ToolTip = "Переключать выбранный стакан Colibri при выборе инструмента" };
    private OverviewColumn[] columns = [];
    public event Action<Market>? UserSelected;

    public OverviewPanel(ExchangeApi api, Settings settings, Action changed, Action<Market> select,
        TimeSpan? spreadInterval = null, TimeSpan? spreadTimeout = null)
    {
        this.api = api;
        this.settings = settings;
        this.changed = changed;
        this.select = select;
        this.spreadInterval = spreadInterval ?? TimeSpan.FromMinutes(1);
        this.spreadTimeout = spreadTimeout ?? TimeSpan.FromSeconds(12);
        Width = 390;
        Background = Terminal.Brush("Surface");
        Margin = new Thickness(1, 0, 0, 0);
        UpdateSourceButton();
        BuildSourceMenu();
        SetDock(source, Dock.Top);
        Children.Add(source);
        colibriSync.IsChecked = settings.ColibriOverviewSync;
        colibriSync.Click += (_, _) =>
        {
            settings.ColibriOverviewSync = colibriSync.IsChecked == true;
            changed();
        };
        SetDock(colibriSync, Dock.Top);
        Children.Add(colibriSync);
        status.Margin = new Thickness(8, 4, 8, 5);
        status.FontSize = 11;
        status.Foreground = Terminal.Brush("Muted");
        SetDock(status, Dock.Bottom);
        Children.Add(status);
        view = new ListCollectionView(rows);
        table.Background = Terminal.Brush("Plot");
        table.Foreground = Terminal.Brush("Text");
        table.RowStyle = Terminal.Style("OverviewRow");
        table.CellStyle = Terminal.Style("OverviewCell");
        table.ColumnHeaderStyle = Terminal.Style("OverviewColumnHeader");
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(table, true);
        VirtualizingPanel.SetScrollUnit(table, ScrollUnit.Item);
        table.ItemsSource = view;
        table.SelectionChanged += (_, _) =>
        {
            if (!loadingSelection && table.SelectedItem is Row selected)
            {
                select(selected.Market);
                if (userSelecting) UserSelected?.Invoke(selected.Market);
            }
        };
        table.PreviewMouseLeftButtonDown += (_, _) => MarkUserSelection();
        table.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) MarkUserSelection();
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
        UpdateIntegration();
    }

    private void MarkUserSelection()
    {
        userSelecting = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => userSelecting = false));
    }

    public void UpdateIntegration()
    {
        colibriSync.Visibility = settings.ColibriEnabled ? Visibility.Visible : Visibility.Collapsed;
        colibriSync.IsChecked = settings.ColibriOverviewSync;
    }

    private void BuildSourceMenu()
    {
        var choices = new StackPanel { Margin = new Thickness(4) };
        foreach (var market in MarketSource.All)
        {
            var label = new DockPanel();
            var mark = new TextBlock
            {
                MinWidth = 16, Margin = new Thickness(7, 0, 3, 0), TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol")
            };
            SetDock(mark, Dock.Right);
            label.Children.Add(mark);
            label.Children.Add(SourceLabel(market));
            var row = new Button
            {
                Style = Terminal.Style("IconButton"), Content = label, MinHeight = 26,
                Padding = new Thickness(6, 0, 4, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            row.Click += (_, _) =>
            {
                if (choosingSource)
                {
                    choosingSource = false;
                    if (settings.OverviewSource == market) { UpdateSourceOptions(); return; }
                    settings.OverviewSource = market;
                    settings.OverviewChoice = null;
                    loadedCatalog = false;
                    UpdateSourceButton();
                }
                else
                {
                    if (settings.OverviewSource == market) return;
                    settings.CycleListingRule(market, overview: true);
                }
                UpdateSourceOptions();
                changed();
            };
            choices.Children.Add(row);
            sourceOptions.Add(market, (row, mark));
        }
        var layout = new DockPanel();
        var header = Terminal.Rule(new Thickness(0, 0, 0, 1));
        var tools = new DockPanel();
        sourceLegend.Margin = new Thickness(9, 4, 6, 4);
        sourceLegend.FontSize = 11;
        sourceLegend.VerticalAlignment = VerticalAlignment.Center;
        sourceMode.Click += (_, _) =>
        {
            choosingSource = !choosingSource;
            UpdateSourceOptions();
        };
        SetDock(sourceMode, Dock.Right);
        tools.Children.Add(sourceMode);
        tools.Children.Add(sourceLegend);
        header.Child = tools;
        SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var surface = new Border
        {
            Child = layout, Background = Terminal.Brush("Surface"), BorderBrush = Terminal.Brush("Outline"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), MaxHeight = 455
        };
        System.Windows.Documents.TextElement.SetForeground(surface, Terminal.Brush("Text"));
        System.Windows.Documents.TextElement.SetFontFamily(surface, (FontFamily)Application.Current.FindResource("TerminalFont"));
        System.Windows.Documents.TextElement.SetFontSize(surface, 12);
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        surface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            sourceMenu.IsOpen = false;
            source.Focus();
            e.Handled = true;
        };
        sourceMenu.Child = surface;
        sourceMenu.PlacementTarget = source;
        sourceMenu.VerticalOffset = 3;
        sourceMenu.Opened += (_, _) => sourceOptions[settings.OverviewSource].Row.Focus();
        source.Click += (_, _) =>
        {
            choosingSource = false;
            UpdateSourceOptions();
            surface.Width = Math.Max(265, source.ActualWidth);
            sourceMenu.IsOpen = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) sourceMenu.IsOpen = false; };
    }

    private void UpdateSourceOptions()
    {
        sourceMode.Content = choosingSource ? "К условиям" : "Сменить источник";
        sourceLegend.Text = choosingSource ? "Выберите биржу для таблицы" : "✓ Есть   × Нет   — Любой";
        foreach (var (market, (row, mark)) in sourceOptions)
        {
            bool active = market == settings.OverviewSource;
            var (text, caption, foreground, background) = active ? ("•", "Источник списка", "Accent", "AccentSurface") : choosingSource
                ? ("", "Выбрать источник списка", "Text", "Surface") :
                settings.OverviewRule(market) switch
                {
                    ListingRule.Require => ("✓", "Требовать листинг", "Accent", "AccentSurface"),
                    ListingRule.Exclude => ("×", "Исключать при листинге", "Excluded", "ExcludedSurface"),
                    _ => ("—", "Без условия", "Muted", "Surface")
                };
            mark.Text = text;
            mark.Foreground = row.Foreground = Terminal.Brush(foreground);
            row.Background = Terminal.Brush(background);
            row.ToolTip = active || choosingSource ? caption : $"{caption} · нажмите для смены";
            AutomationProperties.SetName(row, market.Label);
            AutomationProperties.SetItemStatus(row, caption);
        }
    }

    private static StackPanel SourceLabel(MarketSource market)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(Terminal.VenueLogo(market.Venue, 18));
        label.Children.Add(new TextBlock { Text = market.Venue.Label(), Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center });
        if (market.Venue != Venue.Alpha)
            label.Children.Add(new TextBlock { Text = market.Segment.ToString(), Foreground = Terminal.Brush("Muted"),
                FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return label;
    }

    private void UpdateSourceButton()
    {
        var content = new DockPanel();
        var arrow = Terminal.Icon("chevron");
        SetDock(arrow, Dock.Right);
        content.Children.Add(arrow);
        content.Children.Add(SourceLabel(settings.OverviewSource));
        source.Content = content;
        AutomationProperties.SetName(source, $"Биржа и сегмент · {settings.OverviewSource.Label}");
    }

    public void UpdateColumns()
    {
        if (columns.SequenceEqual(settings.OverviewColumns) && table.Columns.Count > 0) return;
        columns = settings.OverviewColumns.ToArray();
        foreach (var row in rows) row.UpdateColumns(columns);
        table.Columns.Clear();
        table.Columns.Add(new DataGridTextColumn { Header = "Тикер", Binding = new Binding("Ticker"), MinWidth = 118,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = TextStyle() });
        var numberHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), table.ColumnHeaderStyle);
        numberHeader.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
        for (int i = 0; i < columns.Length; i++)
        {
            var data = columns[i];
            string unit = data.Kind switch { MetricKind.Turnover => "$", MetricKind.Change => "%", _ => "ATR %" };
            table.Columns.Add(new DataGridTextColumn
            {
                Header = data.Kind == MetricKind.Spread ? "Спред %" : $"{unit} {MainWindow.WindowName(data.Minutes * 60)}", Binding = new Binding($"[{i}]"),
                Width = data.Kind == MetricKind.Turnover ? 105 : 78,
                HeaderStyle = numberHeader, ElementStyle = TextStyle(i, data.Kind)
            });
        }
        Sort();
        Restart();
    }

    private static Style TextStyle(int index = -1, MetricKind kind = MetricKind.Turnover)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        if (index < 0)
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        else
        {
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding($"Values[{index}]")
                { Converter = new MetricBrush(kind) }));
            if (kind == MetricKind.Spread)
                style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding(nameof(Row.SpreadToolTip))));
        }
        return style;
    }

    private sealed class MetricBrush(MetricKind kind) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            Terminal.Brush(value switch
            {
                double number when kind == MetricKind.Change && number > 0 => "Rising",
                double number when kind == MetricKind.Change && number < 0 => "Falling",
                double => "Text",
                _ => "Muted"
            });

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public void SetSource(IReadOnlyList<Market> next, bool visible, string? pending = null, string? details = null)
    {
        shown = visible;
        catalogDetails = details;
        if (!loadedCatalog || boundSource != settings.OverviewSource || markets.Length != next.Count ||
            !markets.ToHashSet().SetEquals(next))
        {
            loadedCatalog = true;
            boundSource = settings.OverviewSource;
            markets = next.ToArray();
            loadingSelection = true;
            rows = next.OrderBy(m => m.Symbol, StringComparer.Ordinal).ThenBy(m => m.Contract?.Product)
                .Select(market => new Row(market, columns)).ToArray();
            view = new ListCollectionView(rows);
            table.ItemsSource = view;
            Sort();
            var selected = rows.FirstOrDefault(r => r.Market.Key == settings.OverviewChoice?.Key);
            if (selected == null) table.CurrentCell = default;
            table.SelectedItem = selected;
            (table.Template?.FindName("DG_ScrollViewer", table) as ScrollViewer)?.ScrollToTop();
            loadingSelection = false;
            Restart();
        }
        else if (visible && worker == null && rows.Length > 0 && columns.Length > 0) Restart();
        else if (!visible) Cancel();
        if (next.Count == 0) status.Text = pending ?? "Нет инструментов по этим условиям";
        UpdateStatusTip();
    }

    private bool SortingSpread => settings.OverviewSort >= 0 && settings.OverviewSort < columns.Length &&
        columns[settings.OverviewSort].Kind == MetricKind.Spread;

    private void Sort()
    {
        SortKeepingViewport(new RowComparer(settings.OverviewSort, settings.OverviewDescending));
        for (int i = 0; i < table.Columns.Count; i++)
            table.Columns[i].SortDirection = i - 1 == settings.OverviewSort
                ? settings.OverviewDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
    }

    private void Restart()
    {
        Cancel();
        if (!shown || rows.Length == 0) return;
        status.Text = $"{rows.Length:N0} инструментов";
        if (columns.Length == 0) return;
        var run = new CancellationTokenSource();
        worker = run;
        if (columns.Any(m => m.Kind == MetricKind.Spread))
        {
            spreadQuotes = new(api, markets);
            spreadDetails = "Спред · сбор котировок";
            UpdateStatusTip();
        }
        _ = LoadOverview(rows, columns, spreadQuotes, run);
    }

    private async Task LoadOverview(Row[] entries, OverviewColumn[] metrics, OverviewSpreadSampler? quotes,
        CancellationTokenSource run)
    {
        var ct = run.Token;
        try
        {
            await Task.WhenAll(
                quotes == null ? Task.CompletedTask : LoadSpreads(quotes, entries, run),
                metrics.Any(m => m.Kind != MetricKind.Spread) ? Load(entries, metrics, ct) : Task.CompletedTask);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(worker, run)) worker = null;
            run.Dispose();
        }
    }

    private async Task LoadSpreads(OverviewSpreadSampler quotes, Row[] entries, CancellationTokenSource run)
    {
        var ct = run.Token;
        bool first = true;
        while (true)
        {
            var snapshot = await Task.Run(() => quotes.Capture(first, spreadTimeout, ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(worker, run)) return;
            int stale = 0;
            bool numbersChanged = false;
            var updated = new List<Row>();
            foreach (var row in entries)
            {
                var value = snapshot.TryGetValue(row.Market.Key, out var spread) ? spread : (OverviewSpreadValue?)null;
                var (numberChanged, stateChanged) = row.SetSpread(value);
                numbersChanged |= numberChanged;
                if (numberChanged || stateChanged) updated.Add(row);
                if (row.SpreadStale) stale++;
            }
            foreach (var row in updated) row.NotifySpread();
            spreadDetails = $"Спред · свежие котировки {snapshot.Count:N0}/{entries.Length:N0} · устаревшие {stale:N0}";
            UpdateStatusTip();
            if (numbersChanged && SortingSpread) RefreshSort();
            first = false;
            await Task.Delay(spreadInterval, ct);
        }
    }

    private void UpdateStatusTip() => status.ToolTip = catalogDetails == null ? spreadDetails :
        spreadDetails == null ? catalogDetails : $"{catalogDetails}\n{spreadDetails}";

    private async Task Load(Row[] entries, OverviewColumn[] metrics, CancellationToken ct)
    {
        while (true)
        {
            var snapshots = new Dictionary<string, MarketSnapshot>();
            if (entries[0].Market.Venue != Venue.Alpha && metrics.Any(m => m.Minutes == 1440 && m.Kind is MetricKind.Turnover or MetricKind.Change))
            {
                try { snapshots = await api.Snapshots(entries.Select(r => r.Market).ToArray(), ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                foreach (var row in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!snapshots.TryGetValue(row.Market.Key, out var snapshot)) continue;
                    row.Set(metrics.Select((metric, i) => SnapshotValue(metric, snapshot) ?? row.Number(i)).ToArray(), metrics);
                }
                if (snapshots.Count != 0 && settings.OverviewSort >= 0 && !SortingSpread) RefreshSort();
            }
            int done = 0;
            foreach (var row in entries)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = snapshots.GetValueOrDefault(row.Market.Key);
                try
                {
                    if (row.Market.Venue == Venue.Alpha && metrics.Any(m => m.Minutes == 1440 && m.Kind is MetricKind.Turnover or MetricKind.Change))
                        snapshot = await api.AlphaSnapshot(row.Market, ct);
                    var values = new double?[metrics.Length];
                    var history = new List<int>();
                    for (int i = 0; i < metrics.Length; i++)
                    {
                        var m = metrics[i];
                        if (m.Kind == MetricKind.Spread) continue;
                        values[i] = SnapshotValue(m, snapshot);
                        if (values[i] == null && (m.Kind != MetricKind.Turnover ||
                            UsdQuotes.Supported(row.Market) && ExchangeApi.HasHistoricalQuoteVolume(row.Market))) history.Add(i);
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
                    var fallback = metrics.Select(m => SnapshotValue(m, snapshot)).ToArray();
                    row.Set(fallback, metrics);
                }
                done++;
                if (done % 12 == 0 || done == entries.Length)
                {
                    status.Text = done == entries.Length ? $"{done:N0} инструментов" : $"{done:N0} / {entries.Length:N0}";
                    if (settings.OverviewSort >= 0 && !SortingSpread) RefreshSort();
                }
                if (done % 24 == 0) await Task.Yield();
            }
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }

    private static double? SnapshotValue(OverviewColumn metric, MarketSnapshot? snapshot) =>
        metric.Minutes != 1440 ? null : metric.Kind switch
        {
            MetricKind.Turnover => snapshot?.Turnover,
            MetricKind.Change => snapshot?.Change,
            _ => null
        };

    private void RefreshSort()
    {
        if (view.CustomSort == null) return;
        for (int i = 1; i < view.Count; i++)
        {
            if (view.CustomSort.Compare(view.GetItemAt(i - 1), view.GetItemAt(i)) <= 0) continue;
            SortKeepingViewport();
            return;
        }
    }

    private void SortKeepingViewport(IComparer? sort = null)
    {
        var scroll = table.Template?.FindName("DG_ScrollViewer", table) as ScrollViewer;
        int top = (int)(scroll?.VerticalOffset ?? 0);
        var anchor = top < table.Items.Count ? table.Items[top] : null;
        double horizontal = scroll?.HorizontalOffset ?? 0;
        var current = table.CurrentCell;
        bool restoreFocus = Keyboard.FocusedElement is DependencyObject focused && Ancestor<DataGridCell>(focused) != null &&
            table.IsKeyboardFocusWithin;
        var selection = table.SelectedItem ?? rows.FirstOrDefault(r => r.Market.Key == settings.OverviewChoice?.Key);
        bool wasLoading = loadingSelection;
        loadingSelection = true;
        try
        {
            if (sort == null) view.Refresh();
            else view.CustomSort = sort;
            if (table.SelectedItem != selection) table.SelectedItem = selection;
            bool hasCurrentCell = current.IsValid && table.Columns.Contains(current.Column);
            if (hasCurrentCell && current != table.CurrentCell) table.CurrentCell = current;
            if (scroll == null || anchor == null) return;
            int position = table.Items.IndexOf(anchor);
            if (position < 0) return;
            scroll.ScrollToVerticalOffset(position);
            scroll.ScrollToHorizontalOffset(horizontal);
            if (!restoreFocus) return;
            table.UpdateLayout();
            if (hasCurrentCell && table.ItemContainerGenerator.ContainerFromItem(current.Item) is DataGridRow row &&
                current.Column.GetCellContent(row) is { } content && Ancestor<DataGridCell>(content) is { } cell)
                cell.Focus();
            else table.Focus();
            scroll.ScrollToVerticalOffset(position);
            scroll.ScrollToHorizontalOffset(horizontal);
        }
        finally { loadingSelection = wasLoading; }
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is Visual visual)
        {
            if (visual is T match) return match;
            element = VisualTreeHelper.GetParent(visual);
        }
        return null;
    }

    public void Cancel()
    {
        spreadQuotes?.Dispose();
        spreadQuotes = null;
        spreadDetails = null;
        UpdateStatusTip();
        worker?.Cancel();
        worker = null;
    }

    public void Dispose()
    {
        sourceMenu.IsOpen = false;
        Cancel();
    }

    internal sealed class Row(Market market, OverviewColumn[] metrics) : INotifyPropertyChanged
    {
        private double?[] values = new double?[metrics.Length];
        private OverviewColumn[] columns = metrics;
        private long spreadTime;
        public Market Market { get; } = market;
        public string Ticker => Market.Ticker;
        public double?[] Values => values;
        public bool SpreadStale { get; private set; }
        public string? SpreadToolTip => SpreadStale
            ? $"Свежих bid/ask нет. Последняя котировка в {DateTimeOffset.FromUnixTimeMilliseconds(spreadTime).ToLocalTime():HH:mm:ss}"
            : null;
        public string this[int i] => Number(i) is { } number
            ? number.ToString(columns[i].Kind == MetricKind.Turnover ? "N0" : columns[i].Kind == MetricKind.Spread ? "0.######" : "+0.00;-0.00;0.00", CultureInfo.CurrentCulture) +
                (columns[i].Kind == MetricKind.Spread && SpreadStale ? "*" : "")
            : "—";
        public double? Number(int i) => i >= 0 && i < values.Length ? values[i] : null;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void UpdateColumns(OverviewColumn[] kinds)
        {
            Set(kinds.Select(kind => Number(Array.IndexOf(columns, kind))).ToArray(), kinds);
            if (kinds.Any(kind => kind.Kind == MetricKind.Spread)) return;
            SpreadStale = false;
            spreadTime = 0;
        }

        public void Set(double?[] next, OverviewColumn[] kinds)
        {
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i].Kind == MetricKind.Spread)
                {
                    next[i] = Number(Array.FindIndex(columns, c => c.Kind == MetricKind.Spread));
                }
            if (columns.SequenceEqual(kinds) && values.SequenceEqual(next)) return;
            values = next;
            columns = kinds;
            NotifyValues();
        }

        public (bool NumberChanged, bool StateChanged) SetSpread(OverviewSpreadValue? sample)
        {
            int index = Array.FindIndex(columns, kind => kind.Kind == MetricKind.Spread);
            if (index < 0) return default;
            double? old = values[index];
            bool wasStale = SpreadStale;
            if (sample is { } fresh)
            {
                values[index] = fresh.Percent;
                spreadTime = fresh.QuoteTime;
            }
            SpreadStale = sample == null && old.HasValue;
            return (old != values[index], wasStale != SpreadStale);
        }

        public void NotifySpread()
        {
            NotifyValues();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpreadToolTip)));
        }

        private void NotifyValues()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Values)));
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
