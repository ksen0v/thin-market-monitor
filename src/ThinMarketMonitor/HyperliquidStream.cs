using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace ThinMarketMonitor;

// One public socket shares trade subscriptions across signal cells and charts, and BBO across both views.
internal sealed class HyperliquidStream
{
    internal const int SubscriptionLimit = 960;
    private readonly object gate = new();
    private readonly HashSet<Listener> listeners = [];
    private readonly Dictionary<Topic, long> active = [];
    private bool running;

    private readonly record struct Topic(string Type, string Coin);

    private sealed class Listener(Market[] markets, bool book, Action ready, Action<Trade>? trade, Action<Market, SpreadQuote>? quote)
    {
        public readonly Dictionary<Topic, Market> Markets = markets.ToDictionary(m => new Topic(book ? "bbo" : "trades", m.Symbol));
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
        public void Start()
        {
            if (Started) return;
            ready();
            Started = true;
        }
        public void Trade(Topic topic, Trade value)
        {
            if (Started && Markets.ContainsKey(topic)) trade?.Invoke(value);
        }
        public void Quote(Topic topic, SpreadQuote value)
        {
            if (Markets.TryGetValue(topic, out var market)) quote?.Invoke(market, value);
        }
    }

    public async Task Listen(Market[] markets, bool book, Action ready, Action<Trade>? trade,
        Action<Market, SpreadQuote>? quote, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var listener = new Listener(markets, book, ready, trade, quote);
        lock (gate)
        {
            var requested = listeners.SelectMany(l => l.Markets.Keys).ToHashSet();
            requested.UnionWith(listener.Markets.Keys);
            if (requested.Count > SubscriptionLimit) throw new IOException("Hyperliquid · лимит подписок");
            listeners.Add(listener);
            if (listener.Markets.Keys.All(active.ContainsKey)) listener.Start();
            if (!running)
            {
                running = true;
                _ = Task.Run(() => Run(markets[0]));
            }
        }
        try { await listener.Done.Task.WaitAsync(ct); }
        finally { lock (gate) listeners.Remove(listener); }
    }

    private async Task Run(Market sample)
    {
        using var socket = ExtendedStreams.Socket();
        using var session = new CancellationTokenSource();
        Task? receiving = null;
        Exception? failure = null;
        try
        {
            await ExtendedStreams.Connect(socket, ExtendedStreams.Address(sample), session.Token);
            receiving = Receive(socket, sample, session.Token);
            var sent = new Dictionary<Topic, long>();
            long nextPing = 0;
            long idleSince = 0;
            while (true)
            {
                if (receiving.IsCompleted) await receiving;
                HashSet<Topic> desired;
                Topic[] removals, additions;
                lock (gate)
                {
                    desired = listeners.SelectMany(l => l.Markets.Keys).ToHashSet();
                    removals = sent.Keys.Where(k => !desired.Contains(k)).ToArray();
                    additions = desired.Where(k => !sent.ContainsKey(k)).ToArray();
                    if (sent.Any(x => Environment.TickCount64 - x.Value > 20_000 && !active.ContainsKey(x.Key)))
                        throw new IOException("Hyperliquid · тайм-аут подтверждения подписки");
                }
                if (desired.Count == 0)
                {
                    if (idleSince == 0) idleSince = Environment.TickCount64;
                    if (Environment.TickCount64 - idleSince > 2500)
                    {
                        lock (gate)
                        {
                            if (listeners.Count == 0) break;
                        }
                    }
                }
                else idleSince = 0;
                foreach (Topic topic in removals.Concat(additions).Take(24))
                {
                    if (receiving.IsCompleted) await receiving;
                    bool add = desired.Contains(topic);
                    if (add && sent.Count >= SubscriptionLimit) break;
                    if (!add) lock (gate) active.Remove(topic);
                    await ExtendedStreams.Send(socket, new { method = add ? "subscribe" : "unsubscribe",
                        subscription = new { type = topic.Type, coin = topic.Coin } }, session.Token);
                    if (add) sent[topic] = Environment.TickCount64;
                    else sent.Remove(topic);
                    await Task.Delay(45, session.Token);
                }
                if (Environment.TickCount64 >= nextPing)
                {
                    await ExtendedStreams.Send(socket, new { method = "ping" }, session.Token);
                    nextPing = Environment.TickCount64 + 25_000;
                }
                await Task.Delay(100, session.Token);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            await session.CancelAsync();
            socket.Abort();
            if (receiving != null) try { await receiving; } catch (Exception) when (session.IsCancellationRequested) { }
            lock (gate)
            {
                var error = failure ?? new IOException("Hyperliquid · поток закрыт");
                foreach (var listener in listeners) listener.Done.TrySetException(error);
                listeners.Clear();
                active.Clear();
                running = false;
            }
        }
    }

    private async Task Receive(ClientWebSocket socket, Market sample, CancellationToken ct)
    {
        byte[] buffer = new byte[16384];
        using var message = new MemoryStream();
        while (true)
        {
            using var json = await ExtendedStreams.Read(socket, buffer, message, false, ct);
            var row = json.RootElement;
            if (row.ValueKind != JsonValueKind.Object) continue;
            string channel = row.Optional("channel");
            if (channel == "error") throw new IOException($"Hyperliquid · {row.Optional("data")}");
            if (channel == "subscriptionResponse")
            {
                var data = row.GetProperty("data");
                if (data.Optional("method") != "subscribe") continue;
                var subscription = data.GetProperty("subscription");
                var topic = new Topic(subscription.Str("type"), subscription.Str("coin"));
                lock (gate)
                {
                    active[topic] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var listener in listeners)
                        if (!listener.Started && listener.Markets.Keys.All(active.ContainsKey)) listener.Start();
                }
            }
            else if (channel == "bbo" && ExtendedStreams.DecodeBook(Venue.Hyperliquid, row) is { } book)
            {
                var topic = new Topic("bbo", book.Symbol);
                lock (gate)
                    foreach (var listener in listeners) listener.Quote(topic, book.Quote);
            }
            else if (channel == "trades")
            {
                var trades = ExtendedStreams.DecodeTrades(sample, row);
                lock (gate)
                    foreach (var trade in trades)
                    {
                        var topic = new Topic("trades", trade.Symbol);
                        if (active.TryGetValue(topic, out long since) && trade.Time >= since)
                            foreach (var listener in listeners) listener.Trade(topic, trade);
                    }
            }
        }
    }
}
