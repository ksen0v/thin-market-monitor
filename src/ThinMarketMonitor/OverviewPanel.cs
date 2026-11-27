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
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Label(OverviewColumn c) =>
        c.Kind == MetricKind.Spread ? "Спред %" : (c.Kind switch { MetricKind.Turnover => "Оборот $", MetricKind.Change => "Изм. %", _ => "ATR %" }) +
        $" · {MainWindow.WindowName(c.Minutes * 60)}";

    public static string Display(MetricKind kind, double number)
    {
        if (!double.IsFinite(number)) return "—";
        if (kind == MetricKind.Turnover) return ChartVolume.CompactUsd(number, "K");
        if (kind == MetricKind.Change)
        {
            double rounded = Math.Round(number, 2, MidpointRounding.AwayFromZero);
            return rounded == 0 ? "0%" : rounded.ToString("0.##", Russian) + "%";
        }
        if (number is > 0 and < 0.01) return "<0,01%";
        return number.ToString("0.##", Russian) + "%";
    }

    public static string Detail(MetricKind kind, double? value)
    {
        if (value is not { } number || !double.IsFinite(number)) return "—";
        if (kind == MetricKind.Turnover) return number.ToString("N2", Russian) + " USD";
        string format = Math.Abs(number) is > 0 and < 0.0000000001 ? "G10" : "0.##########";
        return number.ToString(format, Russian) + "%";
    }

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

internal sealed partial class OverviewPanel : DockPanel, IDisposable
{
    private readonly ExchangeApi api;
    private readonly Settings settings;
    private readonly Action<Market> select;
    private readonly DataGrid table = new()
    {
        IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, GridLinesVisibility = DataGridGridLinesVisibility.None,
        SelectionMode = DataGridSelectionMode.Single, AlternationCount = 2,
        CanUserReorderColumns = false, CanUserResizeColumns = true, CanUserResizeRows = false,
        BorderThickness = new Thickness(0)
    };
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<string, Row> catalogRows = new(StringComparer.Ordinal);
    private Row[] rows = [];
    private ListCollectionView view;
    private sealed record Viewport(Row? TopRow, int Offset, double? HorizontalOffset = null);
    private DispatcherOperation? viewportUpdate;
    private Viewport? pendingViewport;
    private CancellationTokenSource? worker;
    private OverviewSpreadSampler? spreadQuotes;
    private readonly TimeSpan spreadInterval;
    private readonly TimeSpan spreadTimeout;
    private Market[] markets = [];
    private string? catalogDetails;
    private string? catalogPending;
    private bool catalogUnavailable;
    private bool noSources;
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
        TimeSpan? spreadInterval = null, TimeSpan? spreadTimeout = null, Action? savePresentation = null)
    {
        this.api = api;
        this.settings = settings;
        this.select = select;
        this.savePresentation = savePresentation ?? changed;
        this.spreadInterval = spreadInterval ?? TimeSpan.FromMinutes(1);
        this.spreadTimeout = spreadTimeout ?? TimeSpan.FromSeconds(12);
        Background = Terminal.Brush("Surface");
        Margin = new Thickness(1, 0, 0, 0);
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
        table.PreviewMouseDown += OverviewMouseDown;
        table.AddHandler(Button.ClickEvent, new RoutedEventHandler(OverviewButtonClick));
        table.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(ColumnDragStarted), handledEventsToo: true);
        table.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(ColumnDragCompleted), handledEventsToo: true);
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
            SortFromHeader();
            changed();
        };
        Children.Add(table);
        fitTimer.Tick += (_, _) =>
        {
            fitTimer.Stop();
            FitAutomaticColumns();
        };
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

    public void UpdateColumns()
    {
        if (columns.SequenceEqual(settings.OverviewColumns) && table.Columns.Count > 0)
        {
            ApplyColumnWidths();
            return;
        }
        columns = settings.OverviewColumns.ToArray();
        foreach (var row in rows) row.UpdateColumns(columns);
        tickerFit = 0;
        metricFits = new double[columns.Length];
        table.Columns.Clear();
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = TickerHeader(), SortMemberPath = nameof(Row.Ticker), MinWidth = 100, MaxWidth = 1200,
            CellTemplate = (DataTemplate)Application.Current.FindResource("OverviewTicker")
        });
        var numberHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), table.ColumnHeaderStyle);
        numberHeader.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
        for (int i = 0; i < columns.Length; i++)
        {
            var data = columns[i];
            string unit = data.Kind switch { MetricKind.Turnover => "$", MetricKind.Change => "%", _ => "ATR %" };
            table.Columns.Add(new DataGridTextColumn
            {
                Header = data.Kind == MetricKind.Spread ? "Спред %" : $"{unit} {MainWindow.WindowName(data.Minutes * 60)}", Binding = new Binding($"[{i}]"),
                MinWidth = 56, MaxWidth = 1200,
                HeaderStyle = numberHeader, ElementStyle = TextStyle(i, data.Kind)
            });
        }
        ApplyColumnWidths();
        QueueColumnFit();
        Sort();
        Restart();
    }

    private static Style TextStyle(int index, MetricKind kind)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding($"Values[{index}]")
            { Converter = new MetricBrush(kind) }));
        style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, kind == MetricKind.Spread
            ? new Binding(nameof(Row.SpreadDisplayToolTip))
            : new Binding($"Values[{index}]") { Converter = new MetricDetail(kind) }));
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

    private sealed class MetricDetail(MetricKind kind) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            OverviewMetrics.Detail(kind, value as double?);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public void SetSource(IReadOnlyList<Market> next, bool visible, string? pending = null, string? details = null,
        bool catalogUnavailable = false, bool noSources = false)
    {
        shown = visible;
        catalogDetails = details;
        catalogPending = pending;
        this.catalogUnavailable = catalogUnavailable;
        this.noSources = noSources;
        var matching = settings.OverviewQuotes == null && !settings.HideOverviewLeveragedTokens
            ? next : next.Where(settings.ShowsOverviewMarket).ToArray();
        var keys = next.Select(market => market.Key).ToHashSet(StringComparer.Ordinal);
        bool preserveViewport = loadedCatalog && rows.Any(row => keys.Contains(row.Market.Key));
        if (catalogRows.Count != 0)
        {
            foreach (var key in catalogRows.Keys.Where(key => !keys.Contains(key)).ToArray()) catalogRows.Remove(key);
        }
        if (!loadedCatalog || markets.Length != matching.Count || !markets.ToHashSet().SetEquals(matching))
        {
            var previouslyVisible = rows.ToHashSet();
            markets = matching.ToArray();
            var replacement = markets.OrderBy(m => m.Symbol, StringComparer.Ordinal).ThenBy(m => m.Contract?.Product)
                .Select(market => GetCatalogRow(market, previouslyVisible)).ToArray();
            var nextRows = replacement.ToDictionary(row => row.Market.Key, StringComparer.Ordinal);
            Viewport viewport = preserveViewport ? CaptureViewport(nextRows) : new(null, 0);
            CancelViewportUpdate();
            var current = preserveViewport ? table.CurrentCell : default;
            string? selectedKey = settings.OverviewChoice?.Key ??
                (preserveViewport ? (table.SelectedItem as Row)?.Market.Key : null);
            loadedCatalog = true;
            rows = replacement;
            tickerFit = 0;
            if (!preserveViewport) Array.Clear(metricFits);
            QueueColumnFit();
            bookmarkMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            loadingSelection = true;
            try
            {
                view = new ListCollectionView(rows) { CustomSort = CurrentComparer() };
                table.ItemsSource = view;
                table.SelectedItem = selectedKey == null ? null : nextRows.GetValueOrDefault(selectedKey);
                if (current.IsValid && current.Item is Row old && table.Columns.Contains(current.Column) &&
                    nextRows.TryGetValue(old.Market.Key, out var currentRow))
                    table.CurrentCell = new DataGridCellInfo(currentRow, current.Column);
                else if (table.SelectedItem == null) table.CurrentCell = default;
            }
            finally { loadingSelection = false; }
            if (preserveViewport) QueueViewportUpdate(viewport);
            ApplyViewport(viewport);
            Restart();
        }
        else if (visible && worker == null && rows.Length > 0 && columns.Length > 0) Restart();
        else if (!visible) Cancel();
        UpdateDiagnostics();
    }

    private Row GetCatalogRow(Market market, HashSet<Row> previouslyVisible)
    {
        if (!catalogRows.TryGetValue(market.Key, out var row) || row.Market != market)
            return catalogRows[market.Key] = new Row(market, columns, BookmarkFor(market));
        if (!previouslyVisible.Contains(row))
        {
            row.UpdateColumns(columns);
            if (row.SetSpread(null).StateChanged) row.NotifySpread();
        }
        return row;
    }

    private bool SortingSpread => settings.OverviewSort >= 0 && settings.OverviewSort < columns.Length &&
        columns[settings.OverviewSort].Kind == MetricKind.Spread;

    private ScrollViewer? TableScroll => table.Template?.FindName("DG_ScrollViewer", table) as ScrollViewer;

    private void SortFromHeader()
    {
        var top = new Viewport(null, 0);
        QueueViewportUpdate(top);
        Sort();
        ApplyViewport(top);
    }

    private Viewport CaptureViewport(IReadOnlyDictionary<string, Row> nextRows)
    {
        var scroll = TableScroll;
        int position = pendingViewport?.TopRow is { } pendingRow ? table.Items.IndexOf(pendingRow) : -1;
        int offset = position >= 0 ? position : pendingViewport?.Offset ?? (int)(scroll?.VerticalOffset ?? 0);
        double horizontal = pendingViewport?.HorizontalOffset ?? scroll?.HorizontalOffset ?? 0;
        if (offset == 0 || nextRows.Count == 0) return new(null, offset, horizontal);
        int count = table.Items.Count;
        int top = Math.Clamp(offset, 0, Math.Max(0, count - 1));
        // When a quote is hidden, prefer the next surviving row at the same distance.
        for (int distance = 0; distance < count; distance++)
        {
            if (top + distance < count && nextRows.TryGetValue(((Row)table.Items[top + distance]).Market.Key, out var after))
                return new(after, offset, horizontal);
            if (distance > 0 && top - distance >= 0 && nextRows.TryGetValue(((Row)table.Items[top - distance]).Market.Key, out var before))
                return new(before, offset, horizontal);
        }
        return new(null, offset, horizontal);
    }

    private void QueueViewportUpdate(Viewport viewport)
    {
        CancelViewportUpdate();
        pendingViewport = viewport;
        viewportUpdate = Dispatcher.InvokeAsync(() =>
        {
            ApplyViewport(viewport);
            table.UpdateLayout();
            pendingViewport = null;
            viewportUpdate = null;
        }, DispatcherPriority.Loaded);
    }

    private void CancelViewportUpdate()
    {
        viewportUpdate?.Abort();
        viewportUpdate = null;
        pendingViewport = null;
    }

    private void ApplyViewport(Viewport viewport)
    {
        if (TableScroll is not { } scroll) return;
        int index = viewport.TopRow == null ? -1 : table.Items.IndexOf(viewport.TopRow);
        scroll.ScrollToVerticalOffset(index >= 0 ? index : viewport.Offset);
        if (viewport.HorizontalOffset is { } horizontal) scroll.ScrollToHorizontalOffset(horizontal);
    }

    private RowComparer CurrentComparer() => new(settings.OverviewSort, settings.OverviewDescending, settings.OverviewBookmarkPriority);

    private void Sort()
    {
        SortRows(CurrentComparer());
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
            if (updated.Count != 0) QueueColumnFit();
            spreadDetails = $"Спред · свежие котировки {snapshot.Count:N0}/{entries.Length:N0} · устаревшие {stale:N0}";
            UpdateStatusTip();
            if (numbersChanged && SortingSpread) RefreshSort();
            first = false;
            await Task.Delay(spreadInterval, ct);
        }
    }

    internal void UpdateDiagnostics()
    {
        status.Visibility = settings.DebugMode || rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Length == 0)
            status.Text = noSources ? "Включите биржу над графиком." : catalogPending == null ? "Нет инструментов по этим условиям" : settings.DebugMode ? catalogPending
                : catalogUnavailable ? "Каталог недоступен" : "Загрузка инструментов…";
        UpdateStatusTip();
    }

    private void UpdateStatusTip() => status.ToolTip = !settings.DebugMode ? null : catalogDetails == null ? spreadDetails :
        spreadDetails == null ? catalogDetails : $"{catalogDetails}\n{spreadDetails}";

    private async Task Load(Row[] entries, OverviewColumn[] metrics, CancellationToken ct)
    {
        while (true)
        {
            var snapshots = new Dictionary<string, MarketSnapshot>();
            if (metrics.Any(m => m.Minutes == 1440 && m.Kind is MetricKind.Turnover or MetricKind.Change))
            {
                try { snapshots = await api.Snapshots(entries.Select(r => r.Market).Where(market => market.Venue != Venue.Alpha).ToArray(), ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                foreach (var row in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!snapshots.TryGetValue(row.Market.Key, out var snapshot)) continue;
                    row.Set(metrics.Select((metric, i) => SnapshotValue(metric, snapshot) ?? row.Number(i)).ToArray(), metrics);
                }
                if (snapshots.Count != 0 && settings.OverviewSort >= 0 && !SortingSpread) RefreshSort();
                if (snapshots.Count != 0) QueueColumnFit();
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
                    if (done == 12 || done % 240 == 0 || done == entries.Length) QueueColumnFit();
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
            SortRows();
            return;
        }
    }

    private void SortRows(IComparer? sort = null)
    {
        var scroll = pendingViewport == null ? TableScroll : null;
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
        fitTimer.Stop();
        if (bookmarkMenu != null) bookmarkMenu.IsOpen = false;
        CancelViewportUpdate();
        Cancel();
    }

    internal sealed class Row(Market market, OverviewColumn[] metrics, BookmarkColor? bookmark = null) : INotifyPropertyChanged
    {
        private double?[] values = new double?[metrics.Length];
        private OverviewColumn[] columns = metrics;
        private long spreadTime;
        public Market Market { get; } = market;
        public string Ticker => Market.Ticker;
        public string SourceLabel => Market.Source.Label;
        public string SegmentLabel => Market.Source.Segment.ToString();
        public ImageSource VenueLogo => Terminal.VenueLogoSource(Market.Venue);
        public BookmarkColor? Bookmark { get; private set; } = bookmark;
        public Brush? BookmarkFill => Bookmark is { } color ? ColorBrush(color) : null;
        public Brush BookmarkBrush => BookmarkFill ?? Terminal.Brush("Muted");
        public string BookmarkHint => $"{Ticker} · закладка: {(Bookmark is { } color ? ColorName(color) : "нет")}";
        public void SetBookmark(BookmarkColor? color)
        {
            Bookmark = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BookmarkFill)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BookmarkBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BookmarkHint)));
        }
        public double?[] Values => values;
        public bool SpreadStale { get; private set; }
        public string? SpreadToolTip => SpreadStale
            ? $"Свежих bid/ask нет. Последняя котировка в {DateTimeOffset.FromUnixTimeMilliseconds(spreadTime).ToLocalTime():HH:mm:ss}"
            : null;
        public string SpreadDisplayToolTip
        {
            get
            {
                int index = Array.FindIndex(columns, column => column.Kind == MetricKind.Spread);
                string value = OverviewMetrics.Detail(MetricKind.Spread, Number(index));
                return SpreadToolTip is { } details ? $"{value}\n{details}" : value;
            }
        }
        public string this[int i]
        {
            get
            {
                if (Number(i) is not { } number) return "—";
                var kind = columns[i].Kind;
                string text = OverviewMetrics.Display(kind, number);
                return kind == MetricKind.Spread && SpreadStale ? text + "*" : text;
            }
        }
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpreadDisplayToolTip)));
        }

        private void NotifyValues()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Values)));
        }
    }

    internal sealed class RowComparer(int key, bool desc, BookmarkColor? priority = null) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not Row a || y is not Row b) return 0;
            if (priority is { } color && (a.Bookmark == color) != (b.Bookmark == color)) return a.Bookmark == color ? -1 : 1;
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
