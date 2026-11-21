using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ThinMarketMonitor;

internal static class OverviewSpreadChecks
{
    public static async Task Run()
    {
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
