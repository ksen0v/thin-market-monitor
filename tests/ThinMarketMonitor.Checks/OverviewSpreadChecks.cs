using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class OverviewSpreadChecks
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        await HeaderSorting();
        const int count = 30;
        var linear = Enumerable.Range(0, count).Select(i =>
            new Market(Venue.Binance, $"M{i:00}USDT", $"M{i:00}", "USDT", new("usdm"))).ToArray();
        var missing = new Market(Venue.Binance, "BTCUSD_PERP", "BTC", "USD", new("coinm"));
        using var handler = new Books(linear);
        using var http = new HttpClient(handler);
        var settings = new Settings
        {
            OverviewSource = linear[0].Source, OverviewColumns = [new(MetricKind.Spread, 0)], OverviewSort = 0
        };
        int selected = 0, external = 0;
        using var panel = new OverviewPanel(new(http), settings, () => { }, _ => selected++,
            spreadInterval: TimeSpan.FromMilliseconds(2300), spreadTimeout: TimeSpan.FromMilliseconds(900));
        panel.UserSelected += _ => external++;
        var window = new Window { Content = panel, Width = 405, Height = 400, ShowInTaskbar = false };
        window.Show();
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var grid = (DataGrid)typeof(OverviewPanel).GetField("table", flags)!.GetValue(panel)!;
            OverviewPanel.Row[] Rows() => grid.Items.Cast<OverviewPanel.Row>().ToArray();
            panel.SetSource([.. linear, missing], visible: true);
            var original = Rows();
            int resets = 0;
            ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            };
            bool publishedTogether = false;
            original.Single(r => r.Market == linear[0]).PropertyChanged += (_, _) =>
                publishedTogether |= Rows().Count(r => r.Number(0) != null) == count;
            await handler.FirstResponse.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(original.Length == count + 1 && Rows().All(r => r[0] == "—") && resets == 0,
                "a returned batch stays unpublished while another source is pending");
            await Until(() => Rows()[0].Market == linear[^1] && Rows()[0].Number(0) != null);
            Check(publishedTogether && resets == 1 && Rows()[^1].Market == missing && Rows()[^1][0] == "—",
                "first snapshot applies once after the deadline, keeps markets without a BBO and sorts unknown last");

            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid)!;
            var anchor = Rows()[8];
            var selection = Rows()[9];
            grid.SelectedItem = selection;
            grid.CurrentCell = new DataGridCellInfo(selection, grid.Columns[1]);
            window.UpdateLayout();
            scroll.ScrollToVerticalOffset(8);
            await Until(() => scroll.VerticalOffset == 8);
            grid.Focus();
            bool hadFocus = grid.IsKeyboardFocusWithin;
            int selectedBefore = selected;
            var before = Rows();
            panel.SetSource([missing, .. linear.Reverse()], visible: true);
            Check(Rows().SequenceEqual(before) && resets == 1, "reordered catalog records preserve the view and the current sample");
            await handler.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Rows().SequenceEqual(before) && handler.LinearCalls == 2,
                "a later request leaves the published order stable until its whole snapshot completes");
            handler.ReleaseSecond.TrySetResult();
            await Until(() => Rows()[0].Market == linear[^2] && resets == 2 &&
                Rows()[(int)scroll.VerticalOffset] == anchor);
            Check(ReferenceEquals(grid.SelectedItem, selection) && ReferenceEquals(grid.CurrentCell.Item, selection) &&
                (!hadFocus || grid.IsKeyboardFocusWithin) && selected == selectedBefore && external == 0,
                "snapshot sort keeps the anchor, selection, current cell and focus without chart or Colibri navigation");

            var published = Rows();
            var retained = selection.Number(0);
            await Until(() => selection.SpreadStale);
            Check(resets == 2 && Rows().SequenceEqual(published) && retained == selection.Number(0) &&
                selection[0].EndsWith('*') && selection.SpreadToolTip != null && Rows()[^1][0] == "—",
                "failed snapshot marks the last values stale and leaves the whole order intact");

            typeof(DataGrid).GetMethod("OnSorting", flags)!.Invoke(grid, [new DataGridSortingEventArgs(grid.Columns[1])]);
            Check(Rows()[0].Market == linear[^1] && resets == 3 && selected == selectedBefore && external == 0,
                "the spread header sorts immediately using retained values");

            await handler.LateRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sameSymbol = selection.Market with { Venue = Venue.Bybit, Contract = new("linear") };
            handler.ReplacementSymbol = sameSymbol.Symbol;
            settings.OverviewSource = sameSymbol.Source;
            settings.OverviewChoice = new(selection.Market.Key, false);
            panel.SetSource([sameSymbol], visible: true);
            handler.ReleaseLate.TrySetResult();
            await Until(() => Rows() is [{ Market: var market } row] && market == sameSymbol && row.Number(0) != null);
            Check(grid.SelectedItem == null && Rows().Single().Number(0) is > 3 and < 5 &&
                selected == selectedBefore && external == 0,
                $"a late result cannot change the new source or match it by ticker alone (selection {grid.SelectedIndex}, " +
                $"spread {Rows().Single().Number(0)}, chart events {selected - selectedBefore}, external events {external})");

            settings.OverviewColumns = [];
            panel.UpdateColumns();
            var calls = handler.Calls;
            await Task.Delay(2450);
            Check(Rows().Length == 1 && grid.Columns.Count == 1 && handler.Calls == calls,
                "removing the spread column keeps the market and stops requests");
        }
        finally { window.Close(); }
        Console.WriteLine("Overview spread snapshots, deadline, stale values, stable viewport, source cancellation and independent UI passed.");
    }

    private static async Task HeaderSorting()
    {
        var markets = Enumerable.Range(0, 60).Select(i => new Market(Venue.Binance, $"M{i:00}USDT", $"M{i:00}", "USDT")).ToArray();
        var metrics = new[] { new OverviewColumn(MetricKind.Turnover, 1440), new(MetricKind.Change, 1440), new(MetricKind.Spread, 0) };
        var settings = new Settings { OverviewSource = markets[0].Source, OverviewColumns = metrics };
        int chartSelections = 0, colibriSelections = 0;
        using var http = new HttpClient();
        using var panel = new OverviewPanel(new(http), settings, () => { }, _ => chartSelections++) { Width = 510 };
        panel.UserSelected += _ => colibriSelections++;
        var window = new Window { Content = panel, Width = 525, Height = 340, ShowInTaskbar = false };
        window.Show();
        try
        {
            var grid = (DataGrid)typeof(OverviewPanel).GetField("table", InstanceMembers)!.GetValue(panel)!;
            panel.SetSource(markets, visible: false);
            await Layout();
            var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid)!;
            var entries = grid.Items.Cast<OverviewPanel.Row>().ToArray();
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].Set([100 + i, -i, null], metrics);
                entries[i].SetSpread(new(i / 100.0, 1));
            }
            var selected = entries[17];
            grid.SelectedItem = selected;
            grid.CurrentCell = new DataGridCellInfo(selected, grid.Columns[1]);
            grid.Focus();
            int selectionsBefore = chartSelections;

            await ScrollTo(15);
            ClickHeader(1);
            await AtTop(entries[^1], "$ 24 ч descending from the middle");
            ClickHeader(1);
            await AtTop(entries[0], "$ 24 ч ascending from the top");
            await ScrollTo(22);
            ClickHeader(1);
            entries[^1].Set([0, -59, null], metrics);
            RefreshValues();
            await AtTop(entries[^2], "$ 24 ч descending with new values before layout");

            grid.Focus();
            bool hasFocus = grid.IsKeyboardFocusWithin;
            await ScrollTo(12);
            var anchor = grid.Items[12];
            var rowToMove = (OverviewPanel.Row)grid.Items[20];
            rowToMove.Set([1000, rowToMove.Number(1), null], metrics);
            RefreshValues();
            await Layout();
            Check(grid.Items[0] == rowToMove && scroll.VerticalOffset == 13 && grid.Items[13] == anchor &&
                (!hasFocus || grid.IsKeyboardFocusWithin), "a later turnover refresh keeps the top row and focus");

            ClickHeader(2);
            await AtTop(entries[0], "% 24 ч");
            ClickHeader(3);
            await AtTop(entries[^1], "spread");
            ClickHeader(0);
            await AtTop(entries[0], "ticker ascending");
            ClickHeader(0);
            await AtTop(entries[^1], "ticker descending");
            Check(ReferenceEquals(grid.CurrentCell.Item, selected), "header sorts retain the current market cell");

            async Task Layout()
            {
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }

            async Task ScrollTo(int row)
            {
                scroll.ScrollToVerticalOffset(row);
                await Layout();
                Check(scroll.VerticalOffset == row, $"test viewport reached row {row}");
            }

            async Task AtTop(OverviewPanel.Row first, string label)
            {
                await Layout();
                Check(scroll.VerticalOffset == 0 && grid.Items[0] == first &&
                    grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow { IsVisible: true },
                    $"{label} shows the first row after layout (offset {scroll.VerticalOffset})");
                Check(ReferenceEquals(grid.SelectedItem, selected) && chartSelections == selectionsBefore && colibriSelections == 0,
                    $"{label} preserves the selected instrument without chart or Colibri navigation");
            }

            void ClickHeader(int column)
            {
                var header = Header(grid, grid.Columns[column]) ?? throw new InvalidOperationException("Missing column header");
                typeof(DataGridColumnHeader).GetMethod("OnClick", InstanceMembers)!.Invoke(header, null);
            }

            void RefreshValues() => typeof(OverviewPanel).GetMethod("RefreshSort", InstanceMembers)!.Invoke(panel, null);
        }
        finally { window.Close(); }
        Console.WriteLine("Overview header sorting starts at the first row, survives a concurrent metric refresh and preserves selection.");
    }

    private static DataGridColumnHeader? Header(DependencyObject root, DataGridColumn column)
    {
        if (root is DataGridColumnHeader header && header.Column == column) return header;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Header(VisualTreeHelper.GetChild(root, i), column) is { } child) return child;
        return null;
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while (!condition()) await Task.Delay(15, deadline.Token);
    }

    private sealed class Books(Market[] markets) : HttpMessageHandler
    {
        private int calls, linearCalls, inverseCalls;
        public int Calls => Volatile.Read(ref calls);
        public int LinearCalls => Volatile.Read(ref linearCalls);
        public string? ReplacementSymbol { get; set; }
        public readonly TaskCompletionSource FirstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LateRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri?.Host == "dapi.binance.com")
            {
                if (Interlocked.Increment(ref inverseCalls) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Reply("[]");
            }
            if (request.RequestUri?.Host == "api.bybit.com")
                return Reply(JsonSerializer.Serialize(new { retCode = 0, time = Now, result = new
                    { list = new[] { new { symbol = ReplacementSymbol, bid1Price = 100, ask1Price = 104, bid1Size = 1, ask1Size = 1 } } } }));
            Check(request.RequestUri?.Host == "fapi.binance.com", "unexpected quote request");
            int call = Interlocked.Increment(ref linearCalls);
            if (call == 1) FirstResponse.TrySetResult();
            if (call == 2)
            {
                SecondRequest.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(ct);
            }
            if (call == 3) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (call == 4)
            {
                LateRequest.TrySetResult();
                await ReleaseLate.Task;
            }
            return Reply(JsonSerializer.Serialize(markets.Select((market, i) => new
            {
                symbol = market.Symbol, bidPrice = 100, askPrice = call >= 2 && i == markets.Length - 1 ? 105 :
                    100 + (markets.Length - i) * 0.01, bidQty = 1, askQty = 1, time = Now
            })));
        }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static HttpResponseMessage Reply(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
