using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class OverviewPresentationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        CompactMetrics();
        await ColumnsAndBookmarks();
        await DrawerAndSettings();
        await QuoteFilterKeepsPosition();
        await LeveragedPairs();
        Console.WriteLine("Overview quote filters preserve position and selection. Compact metrics, column fitting, bookmarks and settings size passed.");
    }

    private static void CompactMetrics()
    {
        var columns = new OverviewColumn[]
        {
            new(MetricKind.Turnover, 1440), new(MetricKind.Change, 1440), new(MetricKind.Atr, 60), new(MetricKind.Spread, 0)
        };
        var market = new Market(Venue.Gate, "BTC_USDT", "BTC", "USDT");
        var row = new OverviewPanel.Row(market, columns);
        row.Set([296_801.23, 64.09, 1.507, null], columns);
        row.SetSpread(new(0.00001234, 1000));
        Check(row[0] == "296K" && row[1] == "64%" && row[2] == "1,51%" && row[3] == "<0,01%" &&
            row.SpreadDisplayToolTip == "0,00001234%", "table cells abbreviate turnover, show whole changes and retain small spread values in the tooltip");
        row.SetSpread(null);
        Check(row[3] == "<0,01%*" && row.SpreadDisplayToolTip.Contains("Свежих bid/ask нет"),
            "the compact spread keeps its stale marker and explanation");
        Check(OverviewMetrics.Display(MetricKind.Turnover, 4_900) == "4K" &&
            OverviewMetrics.Display(MetricKind.Turnover, 107.99) == "107" &&
            OverviewMetrics.Display(MetricKind.Turnover, 1_000) == "1K" &&
            OverviewMetrics.Display(MetricKind.Turnover, 1_000_000) == "1M" &&
            OverviewMetrics.Display(MetricKind.Turnover, 1_000_000_000) == "1B" &&
            OverviewMetrics.Display(MetricKind.Change, -64.09) == "-64%" &&
            OverviewMetrics.Display(MetricKind.Change, -0.1) == "0%" &&
            OverviewMetrics.Display(MetricKind.Spread, 0) == "0%" &&
            OverviewMetrics.Display(MetricKind.Atr, double.NaN) == "—", "compact metrics cover boundaries, zero and unavailable values");
        var other = new OverviewPanel.Row(market with { Symbol = "ADA_USDT", Base = "ADA" }, columns);
        other.Set([296_800.99, 64.08, null, null], columns);
        Check(row[0] == other[0] && row[1] == other[1] &&
            new OverviewPanel.RowComparer(0, true).Compare(row, other) < 0 &&
            new OverviewPanel.RowComparer(1, true).Compare(row, other) < 0, "equal compact text still sorts by the underlying numbers");
    }

    private static async Task LeveragedPairs()
    {
        using var http = new HttpClient(new GatePairs());
        var catalog = await new ExchangeApi(http).LoadCatalog(new MarketSource(Venue.Gate), CancellationToken.None);
        var settings = new Settings { HideOverviewLeveragedTokens = true, OverviewQuotes = ["USDT"] };
        Check(catalog.Error == null && catalog.Markets.Select(m => m.Base).ToHashSet()
            .SetEquals(new[] { "XRP", "XRP5L", "BTC3S", "PENGU3L", "TEST3S", "BTC" }), "Gate fixture passed through the spot adapter");
        Check(catalog.Markets.Where(settings.ShowsOverviewMarket).Select(m => m.Base).SequenceEqual(new[] { "XRP", "TEST3S" }),
            "the leveraged filter uses Gate's product metadata and combines with exact quote filtering");
        var future = catalog.Markets.Single(m => m.Base == "BTC3S") with { Contract = new("futures/usdt") };
        Check(settings.ShowsOverviewMarket(future) && Settings.Deserialize(JsonSerializer.Serialize(settings)).HideOverviewLeveragedTokens &&
            !Settings.Deserialize("{}").HideOverviewLeveragedTokens, "the switch is saved and does not affect futures");
        Check(catalog.Markets.All(settings.ShowsSignalMarket), "the overview switch does not affect signal markets");
        settings.HideSignalLeveragedTokens = true;
        Check(catalog.Markets.Where(settings.ShowsSignalMarket).Select(m => m.Base).SequenceEqual(new[] { "XRP", "TEST3S", "BTC" }) &&
            settings.ShowsSignalMarket(future) && Settings.Deserialize(JsonSerializer.Serialize(settings)).HideSignalLeveragedTokens,
            "signals have an independent persistent switch that excludes only confirmed leveraged spot tokens");
    }

    private sealed class GatePairs : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                [{"id":"XRP_USDT","base":"XRP","base_name":"XRP","quote":"USDT","trade_status":"tradable"},
                 {"id":"XRP5L_USDT","base":"XRP5L","base_name":"XRP5xLong","quote":"USDT","trade_status":"tradable"},
                 {"id":"BTC3S_USDT","base":"BTC3S","base_name":"BTC3xShort","quote":"USDT","trade_status":"tradable"},
                 {"id":"PENGU3L_USDT","base":"PENGU3L","base_name":"三倍多PENGU","quote":"USDT","trade_status":"tradable"},
                 {"id":"TEST3S_USDT","base":"TEST3S","base_name":"Unrelated token","quote":"USDT","trade_status":"tradable"},
                 {"id":"BTC_USDC","base":"BTC","base_name":"Bitcoin","quote":"USDC","trade_status":"tradable"}]
                """)});
    }

    private static async Task ColumnsAndBookmarks()
    {
        var turnover = new OverviewColumn(MetricKind.Turnover, 1440);
        var spread = new OverviewColumn(MetricKind.Spread, 0);
        var metrics = new[] { turnover, spread };
        var market = new Market(Venue.Binance, "M00USDT", "M00", "USDT");
        var all = Enumerable.Range(0, 45).Select(i => market with { Symbol = $"M{i:00}USDT", Base = $"M{i:00}" }).ToArray();
        all[^1] = market with
        {
            Symbol = "LONGUSDT", Base = "LONG",
            Identifiers = new(Display: "Z-LONG-TICKER-ALTERNATIVE-MARKET-LABEL-WITH-DESCRIPTION/USDT")
        };
        const double wideTurnover = 1.23456789e23;
        var settings = new Settings { OverviewSource = market.Source, OverviewColumns = metrics };
        int saved = 0, selected = 0, colibri = 0, listingChanges = 0;
        using var http = new HttpClient();
        using var panel = new OverviewPanel(new(http), settings, () => listingChanges++, _ => selected++, savePresentation: () => saved++);
        panel.UserSelected += _ => colibri++;
        var window = new Window { Content = panel, Width = 570, Height = 375, ShowInTaskbar = false };
        window.Show();
        try
        {
            var grid = Field<DataGrid>(panel, "table");
            panel.SetSource(all, visible: false);
            var rows = grid.Items.Cast<OverviewPanel.Row>().ToArray();
            double[] initial = grid.Columns.Select(c => c.Width.Value).ToArray();
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].Set([100 + i, null], metrics);
                rows[i].SetSpread(new(0.000001 + i / 100.0, 1));
            }
            rows[^1].Set([wideTurnover, null], metrics);
            await Until(() => grid.Columns[0].Width.Value > initial[0] && grid.Columns[1].Width.Value > initial[1]);
            await Layout(window);
            Check(rows[0][1] == "<0,01%" && rows[^1][0].EndsWith('T'), "small spread and large turnover fixtures are displayed");
            var firstValue = (TextBlock)grid.Columns[1].GetCellContent(FindRow(grid, rows[0]))!;
            Check(firstValue.Text == "100" && Equals(firstValue.ToolTip, "100,00 USD"), "turnover cells expose the full value on hover");
            Check(grid.ItemContainerGenerator.ContainerFromIndex(rows.Length - 1) == null, "column fitting retains vertical virtualization");
            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid)!;
            Check(scroll.ScrollableWidth > 0, "large values and ticker enable horizontal scrolling");
            double auto = grid.Columns[1].Width.Value;
            var grip = HeaderGrip(grid, 1);
            Drag(grip, 35);
            await Layout(window);
            Check(settings.OverviewColumnWidths.TryGetValue(turnover.WidthKey, out double manual) && manual >= auto + 30,
                $"dragging a header persists its exact metric width (auto {auto}, actual {grid.Columns[1].Width.Value})");
            settings.OverviewColumns = [spread, turnover];
            panel.UpdateColumns();
            await Layout(window);
            Check(grid.Columns[2].Width.Value == manual, "metric width follows the metric when columns change order");
            settings.OverviewColumns = [spread];
            panel.UpdateColumns();
            settings.OverviewColumns = metrics;
            panel.UpdateColumns();
            await Layout(window);
            Check(grid.Columns[1].Width.Value == manual &&
                Settings.Deserialize(JsonSerializer.Serialize(settings)).OverviewColumnWidths[turnover.WidthKey] == manual,
                "remove/re-add and settings serialization preserve manual width");
            for (int i = 0; i < rows.Length; i++) rows[i].Set([100 + i, null], metrics);
            rows[^1].Set([wideTurnover, null], metrics);
            grip = HeaderGrip(grid, 1);
            Press(grip, clicks: 2);
            await Layout(window);
            Check(!settings.OverviewColumnWidths.ContainsKey(turnover.WidthKey), "double click removes the manual width");
            Check(grid.Columns[1].Width.Value == auto, "double click restores content fitting");

            grid.SelectedItem = rows[3];
            int selectionCount = selected;
            var first = BookmarkButton(FindRow(grid, rows[0]));
            Press(first);
            Choose(panel, "Голубая");
            Press(BookmarkButton(FindRow(grid, rows[2])));
            Choose(panel, "Голубая");
            Press(BookmarkButton(FindRow(grid, rows[1])));
            Choose(panel, "Розовая");
            Check(rows[0].Bookmark == BookmarkColor.Blue && rows[1].Bookmark == BookmarkColor.Pink && rows[2].Bookmark == BookmarkColor.Blue &&
                settings.OverviewSort == -1 && !settings.OverviewDescending, "row palettes assign distinct colors without invoking ticker sort");

            typeof(DataGrid).GetMethod("OnSorting", Instance)!.Invoke(grid, [new DataGridSortingEventArgs(grid.Columns[1])]);
            await Layout(window);
            scroll.ScrollToVerticalOffset(10);
            await Until(() => scroll.VerticalOffset == 10);
            Press(Field<Button>(panel, "priorityButton"));
            Choose(panel, "Голубая");
            await Layout(window);
            Check(scroll.VerticalOffset == 0 && grid.Items[0] == rows[2] && grid.Items[1] == rows[0] && grid.Items[2] == rows[^1],
                "priority shows the first row, keeps other colors, and sorts both groups by descending turnover");
            typeof(DataGrid).GetMethod("OnSorting", Instance)!.Invoke(grid, [new DataGridSortingEventArgs(grid.Columns[1])]);
            await Layout(window);
            Check(grid.Items[0] == rows[0] && grid.Items[1] == rows[2] && grid.Items[2] == rows[1],
                "reversing turnover sorts within both bookmark groups");
            Press(BookmarkButton(FindRow(grid, rows[0])));
            Choose(panel, "Снять закладку");
            await Layout(window);
            Check(grid.Items[0] == rows[2] && rows[0].Bookmark == null, "removing a prioritized bookmark moves only that market out of the group");
            Press(Field<Button>(panel, "priorityButton"));
            Choose(panel, "Без приоритета");
            await Layout(window);
            var restored = Settings.Deserialize(JsonSerializer.Serialize(settings));
            var future = rows[2].Market with { Contract = new("usdm") };
            Check(grid.Items.Count == all.Length && restored.OverviewBookmarks[rows[2].Market.Key] == BookmarkColor.Blue &&
                !restored.OverviewBookmarks.ContainsKey(future.Key) &&
                restored.OverviewBookmarkPriority == null && scroll.VerticalOffset == 0, "clearing priority retains separate exact-market bookmarks");
            Check(grid.SelectedItem == rows[3] && selected == selectionCount && colibri == 0 && listingChanges == 2 && saved >= 7,
                "bookmark and resize operations save presentation without selecting a chart, sending Colibri or invoking ticker sort");
        }
        finally { window.Close(); }
    }

    private static async Task DrawerAndSettings()
    {
        var kraken = new MarketSource(Venue.Kraken);
        var settings = new Settings { Overview = true, OverviewSource = kraken, OverviewColumns = [] };
        using var http = new HttpClient(new NoUpdates());
        using var window = new MainWindow(false, settings, testHttp: http) { Width = 1160, Height = 740 };
        Field<Dictionary<MarketSource, Catalog>>(window, "catalogs")[kraken] = new(new[] { "USD", "USDT", "USDC", "EUR", "GBP" }
            .Select(quote => new Market(Venue.Kraken, "BTC" + quote, "BTC", quote)).ToArray());
        typeof(MainWindow).GetMethod("RefreshOverview", Instance)!.Invoke(window, null);
        window.Show();
        try
        {
            await Layout(window);
            var drawer = (ColumnDefinition)window.FindName("OverviewDrawerColumn");
            var divider = (GridSplitter)window.FindName("OverviewDivider");
            var chart = (ContentControl)window.FindName("OverviewChartHost");
            Drag(divider, -145);
            await Layout(window);
            double preferred = settings.OverviewDrawerWidth;
            Check(preferred > 510 && Math.Abs(drawer.ActualWidth - preferred) < 1.1,
                $"the overview divider saves its new width ({preferred}, actual {drawer.ActualWidth})");
            window.Width = window.MinWidth;
            await Layout(window);
            Check(drawer.ActualWidth < preferred && chart.ActualWidth >= 284 && settings.OverviewDrawerWidth == preferred,
                $"a narrow window leaves room for the chart without discarding the saved width ({drawer.ActualWidth}, {chart.ActualWidth}, {settings.OverviewDrawerWidth})");
            window.Width = 1160;
            await Layout(window);
            var toggle = (Button)window.FindName("DrawerSwitch");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout(window);
            Check(drawer.ActualWidth == 0 && divider.Visibility == Visibility.Collapsed, "collapsing the drawer gives the chart the full width");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout(window);
            Check(Math.Abs(drawer.ActualWidth - preferred) < 1.1 && Settings.Deserialize(JsonSerializer.Serialize(settings)).OverviewDrawerWidth == preferred,
                "reopening and deserializing restore the preferred width");

            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = Field<SettingsPanel>(window, "settingsPanel");
            var navigation = Field<Button[]>(dialog, "navigation");
            int[] sections = [1, 2, 8, 3, 9, 4, 5, 6, 7, 1, 8, 0];
            await Select(0);
            var standard = DialogBounds();
            foreach (int page in sections)
            {
                await Select(page);
                Check(DialogBounds() == standard, $"settings page {page} keeps the size and position of Filters (expected {standard}, actual {DialogBounds()})");
            }
            await Select(1);
            var minutes = Descendants(dialog).OfType<TextBox>().Single(b =>
                AutomationProperties.GetName(b) == "Пауза между сигналами по монете, минуты");
            var apply = Descendants(dialog).OfType<Button>().Single(b =>
                AutomationProperties.GetName(b) == "Применить паузу между сигналами");
            Check(minutes.Text == "0" && !apply.IsEnabled, "the repeat cooldown is off by default in Modules");
            minutes.Text = "-5";
            Check(!apply.IsEnabled && settings.SignalCooldownMinutes == 0, "invalid minutes cannot change the saved setting");
            minutes.Text = "5";
            Check(apply.IsEnabled, "five minutes can be saved");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Settings.Deserialize(JsonSerializer.Serialize(settings)).SignalCooldownMinutes == 5 && !apply.IsEnabled,
                "applying the pause persists its duration through the normal settings dialog");
            await Select(5);
            var quotes = Field<WrapPanel>(dialog, "overviewQuotes");
            var grid = Field<DataGrid>(Field<OverviewPanel>(window, "overviewPanel"), "table");
            Check(grid.Items.Count == 5 && quotes.Children.OfType<ToggleButton>().Any(b => Equals(b.Content, "GBP")),
                "all quotes are shown initially, including a quote discovered in the Kraken catalog");
            Toggle("Все", false);
            Toggle("USDC", true);
            Toggle("USD", true);
            Check(grid.Items.Cast<OverviewPanel.Row>().Select(row => row.Market.Quote).Order().SequenceEqual(new[] { "USD", "USDC" }) &&
                Settings.Deserialize(JsonSerializer.Serialize(settings)).OverviewQuotes!.SequenceEqual(new[] { "USD", "USDC" }),
                "the overview list and saved settings reflect the checked quotes");
            Toggle("Все", true);
            Check(grid.Items.Count == 5 && settings.OverviewQuotes == null, "all quotes can be restored");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Select(0);
            var clamped = DialogBounds();
            Check(clamped.Height < standard.Height && clamped.Height < dialog.MaxHeight + 1.1,
                "settings fit the available height in a small window");
            foreach (int page in sections)
            {
                await Select(page);
                Check(DialogBounds() == clamped, $"settings page {page} keeps the same size and position in a small window");
            }

            Rect DialogBounds() => new(dialog.TranslatePoint(new Point(), window), dialog.RenderSize);

            async Task Select(int page)
            {
                navigation[page].Focus();
                navigation[page].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout(window);
            }

            void Toggle(string name, bool value)
            {
                var button = quotes.Children.OfType<ToggleButton>().Single(b => Equals(b.Content, name));
                button.IsChecked = value;
                button.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            }
        }
        finally { window.Close(); }
    }

    private static async Task QuoteFilterKeepsPosition()
    {
        var source = new MarketSource(Venue.Kraken);
        var turnover = new OverviewColumn(MetricKind.Turnover, 1440);
        var settings = new Settings
        {
            Overview = true, OverviewSource = source, OverviewColumns = [turnover], OverviewSort = 0, OverviewDescending = true
        };
        var markets = Enumerable.Range(0, 60).SelectMany(i => new[] { "USDC", "USDT" }.Select(quote =>
            new Market(source.Venue, $"M{i:00}/{quote}", $"M{i:00}", quote))).ToArray();
        using var http = new HttpClient(new PendingMetrics());
        using var window = new MainWindow(false, settings, testHttp: http) { Width = 1060, Height = 680 };
        Field<Dictionary<MarketSource, Catalog>>(window, "catalogs")[source] = new(markets);
        typeof(MainWindow).GetMethod("RefreshOverview", Instance)!.Invoke(window, null);
        var panel = Field<OverviewPanel>(window, "overviewPanel");
        var grid = Field<DataGrid>(panel, "table");
        window.Show();
        try
        {
            await Layout(window);
            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid)!;
            var rows = grid.Items.Cast<OverviewPanel.Row>().ToArray();
            for (int i = 0; i < rows.Length; i++) rows[i].Set([100_000 - i], settings.OverviewColumns);
            var selection = rows[53];
            grid.SelectedItem = selection;
            grid.CurrentCell = new DataGridCellInfo(selection, grid.Columns[1]);
            var chart = Field<ChartCell>(window, "overviewChart");
            int navigations = 0;
            panel.UserSelected += _ => navigations++;
            var settingsButton = (Button)window.FindName("SettingsButton");
            settingsButton.Focus();
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = Field<SettingsPanel>(window, "settingsPanel");
            var quotes = Field<WrapPanel>(dialog, "overviewQuotes");
            await ScrollTo(37);
            var anchor = TopRow();
            Check(anchor == rows[37] && anchor.Market.Quote == "USDT", "a USDT row is at the top of a scrolled mixed list");

            ToggleUsdc(false);
            await Layout(window);
            Check(TopRow() == anchor && grid.Items.Count == 60 && grid.Items.Cast<OverviewPanel.Row>().All(r => r.Market.Quote == "USDT"),
                $"removing USDC keeps the same USDT row at the top (offset {scroll.VerticalOffset})");
            ToggleUsdc(true);
            await Layout(window);
            Check(TopRow() == anchor && grid.Items.Count == 120 && grid.Items[0] == rows[0] && rows[0].Number(0) == 100_000,
                "adding USDC back keeps the viewport and the already sorted turnover values");

            await ScrollTo(42);
            Check(TopRow() == rows[42] && TopRow().Market.Quote == "USDC", "an excluded USDC row is now the first visible row");
            ToggleUsdc(false);
            await Layout(window);
            Check(TopRow() == rows[43], "when the top row is hidden, the next nearest USDT row becomes the anchor");
            ToggleUsdc(true);
            ToggleUsdc(false);
            await Layout(window);
            Check(TopRow() == rows[43] && grid.Items.Count == 60, "rapid changes use the pending viewport for the next filter change");
            Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "Готово")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout(window);
            Check(TopRow() == rows[43] && grid.SelectedItem == selection && ReferenceEquals(grid.CurrentCell.Item, selection) &&
                chart.Choice?.Key == selection.Market.Key && navigations == 0,
                "closing settings keeps the viewport, current cell, chart and exact selection without Colibri navigation");

            async Task ScrollTo(int index)
            {
                scroll.ScrollToVerticalOffset(index);
                await Until(() => scroll.VerticalOffset == index);
            }

            OverviewPanel.Row TopRow() => (OverviewPanel.Row)grid.Items[(int)scroll.VerticalOffset];

            void ToggleUsdc(bool enabled)
            {
                var button = quotes.Children.OfType<ToggleButton>().Single(b => Equals(b.Content, "USDC"));
                button.IsChecked = enabled;
                button.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            }
        }
        finally { window.Close(); }
    }

    private sealed class PendingMetrics : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class NoUpdates : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static DataGridRow FindRow(DataGrid table, OverviewPanel.Row row) =>
        (DataGridRow?)table.ItemContainerGenerator.ContainerFromItem(row) ?? throw new InvalidOperationException("Expected a visible row");
    private static Button BookmarkButton(DependencyObject container) =>
        Descendants(container).OfType<Button>().Single(b => b.Style == Terminal.Style("OverviewBookmarkButton"));
    private static Thumb HeaderGrip(DataGrid grid, int index)
    {
        var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => h.Column == grid.Columns[index]);
        return (Thumb)header.Template.FindName("PART_RightHeaderGripper", header);
    }

    private static void Press(UIElement element, int clicks = 1)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, clicks);
        element.RaiseEvent(args);
    }

    private static void Choose(OverviewPanel panel, string name)
    {
        var menu = Field<ContextMenu>(panel, "bookmarkMenu");
        Check(menu.IsOpen, "the bookmark press opened its palette");
        menu.Items.Cast<MenuItem>().Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == name)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
    }

    private static void Drag(Thumb thumb, double delta)
    {
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance)!.GetValue(owner)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task Layout(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}
