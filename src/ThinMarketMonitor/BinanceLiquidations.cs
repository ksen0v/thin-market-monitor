using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed class BinanceLiquidations
{
    private readonly object gate = new();
    private readonly Dictionary<string, Feed> feeds = new(StringComparer.Ordinal);
    private sealed class Feed
    {
        public readonly CancellationTokenSource Stop = new();
        public readonly List<Lease> Subscribers = [];
        public string Status = "Binance · подключение";
    }
    private sealed class Lease(Action dispose, Action<ChartExecution> execute, Action<string> state) : IDisposable
    {
        private Action? release = dispose;
        public Action<ChartExecution> Execute { get; } = execute;
        public Action<string> State { get; } = state;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    public static bool Supports(Market market) => market is { Venue: Venue.Binance, Contract.Product: "usdm" or "coinm" }
        && market.Contract.Multiplier > 0;

    public IDisposable Watch(Market market, Action<ChartExecution> execution, Action<string> status)
    {
        lock (gate)
        {
            bool start = !feeds.TryGetValue(market.Key, out var feed);
            if (start) feeds[market.Key] = feed = new();
            var current = feed!;
            Lease? lease = null;
            lease = new(() =>
            {
                lock (gate)
                {
                    current.Subscribers.Remove(lease!);
                    if (current.Subscribers.Count != 0) return;
                    feeds.Remove(market.Key);
                    current.Stop.Cancel();
                }
            }, execution, status);
            current.Subscribers.Add(lease);
            status(current.Status);
            if (start) _ = Task.Run(() => Run(market, current));
            return lease;
        }
    }

    private async Task Run(Market market, Feed feed)
    {
        var ct = feed.Stop.Token;
        string stream = Uri.EscapeDataString(market.Symbol.ToLowerInvariant()) + "@forceOrder";
        // Both symbol types are published on the /market stream after the COIN-M migration.
        var url = new Uri("wss://fstream.binance.com/market/ws/" + stream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var socket = ExtendedStreams.Socket();
                    await ExtendedStreams.Connect(socket, url, ct);
                    State("Binance · поток открыт · снимки 1 с");
                    using var message = new MemoryStream();
                    byte[] buffer = new byte[16 * 1024];
                    while (!ct.IsCancellationRequested)
                    {
                        using var json = await ExtendedStreams.Read(socket, buffer, message, false, ct, Timeout.InfiniteTimeSpan);
                        if (Decode(market, json.RootElement) is not { } item) continue;
                        foreach (var listener in Subscribers()) listener.Execute(item);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    (ChartFeatures.ExpectedNetworkError(ex) || ex is WebSocketException))
                {
                    State("Binance · поток прерван · повтор через 8 с");
                    await Task.Delay(8000, ct);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        finally { feed.Stop.Dispose(); }

        Lease[] Subscribers() { lock (gate) return feed.Subscribers.ToArray(); }
        void State(string status)
        {
            Lease[] listeners;
            lock (gate)
            {
                feed.Status = status;
                listeners = feed.Subscribers.ToArray();
            }
            foreach (var listener in listeners) listener.State(status);
        }
    }

    internal static ChartExecution? Decode(Market market, JsonElement root)
    {
        if (root.TryGetProperty("data", out var wrapped)) root = wrapped;
        if (!Supports(market) || root.Optional("e") != "forceOrder" || !root.TryGetProperty("o", out var order) ||
            order.Optional("s") != market.Symbol || order.Optional("X") != "FILLED") return null;
        string type = order.Optional("st");
        if (type.Length != 0 && type != (market.Contract!.Product == "coinm" ? "2" : "1")) return null;
        double filled = order.Number("z"), last = order.Number("l"), price = order.Number("ap");
        long time = (long)order.Number("T");
        // Average and last fill are the same price only when the last fill accounts for the whole order.
        if (filled <= 0 || filled != last || !double.IsFinite(filled) || price <= 0 || !double.IsFinite(price) || time <= 0) return null;
        double amount = market.Turnover(filled, price);
        if (!double.IsFinite(amount) || amount <= 0) return null;
        var side = order.Optional("S") switch { "SELL" => LiquidationSide.Long, "BUY" => LiquidationSide.Short, _ => LiquidationSide.Unknown };
        // No order ID is published. Snapshots have an event time in addition to the fill time.
        string id = FormattableString.Invariant($"{root.Optional("E")}:{time}:{order.Optional("S")}:{price:R}:{filled:R}");
        return new(time, price, amount, id, side);
    }
}
