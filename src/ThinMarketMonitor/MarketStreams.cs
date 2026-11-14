using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed record FeedStatus(MarketSource Source, int Count, bool Connected, string Detail)
{
    public Venue Venue => Source.Venue;
}

internal sealed class MarketStreams : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, long> lastTrades = new();
    private readonly ConcurrentDictionary<string, long> sizedTrades = new();
    private readonly ConcurrentDictionary<int, FeedStatus> feeds = new();
    private readonly ConcurrentQueue<Signal> signals = new();
    private readonly Dictionary<string, SpikeDetector> detectors;
    private readonly Dictionary<string, VolumeImpulse> volumes;
    private HashSet<string>? eligible;
    private int pendingSignals;
    private readonly Task completion;

    public MarketStreams(ExchangeApi api, IReadOnlyList<Market> markets, Threshold[] thresholds, MarketStreams? previous = null)
        : this(api, markets, thresholds, [], null, previous) { }

    public MarketStreams(ExchangeApi api, IReadOnlyList<Market> markets, Threshold[] thresholds, VolumeRule[] volumeRules,
        IEnumerable<Market>? eligibleMarkets, MarketStreams? previous = null)
    {
        SetEligible(eligibleMarkets);
        detectors = markets.ToDictionary(m => m.Key,
            m => previous?.detectors.GetValueOrDefault(m.Key) ?? new SpikeDetector(thresholds));
        volumes = volumeRules.Length == 0 ? new() : markets.Where(UsdQuotes.Supported)
            .ToDictionary(m => m.Key, _ => new VolumeImpulse(volumeRules));
        int nextId = 0;
        var tasks = new List<Task>();
        foreach (var venue in markets.Where(m => m.Streaming).GroupBy(m => (m.Source, m.Contract?.Product)))
        {
            int index = 0;
            foreach (var group in venue.Chunk(venue.Key.Source.Venue is Venue.Mexc or Venue.KuCoin && venue.Key.Product != null ? 30 :
                venue.Key.Source.Venue == Venue.Mexc ? 30 : 100))
            {
                int id = nextId++;
                int delay = index++ * 550;
                feeds[id] = new(venue.Key.Source, group.Length, false, "подключение");
                tasks.Add(Task.Run(() => Run(api, group, id, delay, stop.Token)));
            }
        }
        completion = Task.WhenAll(tasks);
    }

    public FeedStatus[] Status() => feeds.Values.OrderBy(x => x.Venue).ToArray();
    public int FreshCount(long now) => lastTrades.Values.Count(x => x >= now - 15_000 && x <= now + 3000);
    internal bool HasFreshTrade(Market market, long now) => lastTrades.TryGetValue(market.Key, out long time)
        && time >= now - 15_000 && time <= now + 3000;
    internal bool HasSizedTrade(Market market, long now) => sizedTrades.TryGetValue(market.Key, out long time)
        && time >= now - 15_000 && time <= now + 3000;
    public int VolumeReadyCount(IEnumerable<Market> markets, long now) =>
        markets.Count(m => volumes.TryGetValue(m.Key, out var detector) && detector.HasCoverage(now));
    public bool TryRead(out Signal? signal)
    {
        if (!signals.TryDequeue(out signal)) return false;
        Interlocked.Decrement(ref pendingSignals);
        return true;
    }

    public void SetEligible(IEnumerable<Market>? markets) =>
        Volatile.Write(ref eligible, markets?.Select(m => m.Key).ToHashSet(StringComparer.Ordinal));

    private void Publish(Signal? signal)
    {
        if (signal == null) return;
        if (Interlocked.Increment(ref pendingSignals) <= 4096) signals.Enqueue(signal);
        else Interlocked.Decrement(ref pendingSignals);
    }

    public void Dispose()
    {
        stop.Cancel();
        _ = completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }

    private async Task Run(ExchangeApi api, Market[] markets,
        int id, int delay, CancellationToken ct)
    {
        var instruments = markets.ToDictionary(x => x.Symbol,
            x => (Market: x, Detector: detectors[x.Key], Volume: volumes.GetValueOrDefault(x.Key)), StringComparer.OrdinalIgnoreCase);
        int backoff = 3;
        var source = markets[0].Source;
        try
        {
            await Task.Delay(delay, ct);
            while (!ct.IsCancellationRequested)
            {
                var started = DateTimeOffset.UtcNow;
                try
                {
                    await Open(api, markets, ct,
                        () =>
                        {
                            feeds[id] = new(source, markets.Length, true, "подписка подтверждена");
                            long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            foreach (var instrument in instruments.Values) instrument.Volume?.ResetCoverage(since);
                        }, trade =>
                        {
                            if (ct.IsCancellationRequested || !instruments.TryGetValue(trade.Symbol, out var instrument)) return;
                            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            if (trade.Time >= now - 5000 && trade.Time <= now + 3000 && double.IsFinite(trade.Price) && trade.Price > 0)
                            {
                                lastTrades.AddOrUpdate(instrument.Market.Key, trade.Time, (_, old) => Math.Max(old, trade.Time));
                                if (trade.Size > 0 && double.IsFinite(trade.Size) && !string.IsNullOrEmpty(trade.Id))
                                    sizedTrades.AddOrUpdate(instrument.Market.Key, trade.Time, (_, old) => Math.Max(old, trade.Time));
                            }
                            bool canSignal = Volatile.Read(ref eligible)?.Contains(instrument.Market.Key) != false;
                            Signal? signal;
                            lock (instrument.Detector) signal = instrument.Detector.Observe(instrument.Market, trade, now, canSignal);
                            Publish(signal);
                            Publish(instrument.Volume?.Observe(instrument.Market, trade, now, canSignal));
                        });
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    string detail = ex switch
                    {
                        WebSocketException => "WebSocket недоступен",
                        HttpRequestException h when h.StatusCode.HasValue => $"HTTP {(int)h.StatusCode.Value}",
                        OperationCanceledException => "тайм-аут",
                        _ => ex.Message
                    };
                    feeds[id] = new(source, markets.Length, false, detail);
                }
                finally
                {
                    foreach (var instrument in instruments.Values) instrument.Volume?.ResetCoverage(null);
                }
                if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(2)) backoff = 3;
                await Task.Delay(backoff * 1000 + Random.Shared.Next(1200), ct);
                backoff = Math.Min(backoff * 2, 60);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal static async Task Open(ExchangeApi api, Market[] markets, CancellationToken ct,
        Action subscribed, Action<Trade> publish)
    {
        var venue = markets[0].Venue;
        string? product = markets[0].Contract?.Product;
        var symbols = markets.Select(m => m.Symbol).ToArray();
        int heartbeat = venue == Venue.Bitget ? 25_000 : venue == Venue.KuCoin && product != null ? 12_000 : 18_000;
        Uri address;
        if (venue == Venue.KuCoin && product == null)
            (address, heartbeat) = await api.KuCoinSocket(ct);
        else
            address = new Uri(venue switch
            {
                Venue.Binance => product switch { "usdm" => "wss://fstream.binance.com/market/stream",
                    "coinm" => "wss://dstream.binance.com/stream", _ => "wss://data-stream.binance.vision/stream" },
                Venue.Alpha => "wss://nbstream.binance.com/w3w/wsa/stream/stream",
                Venue.Bybit => "wss://stream.bybit.com/v5/public/" + (product ?? "spot"),
                Venue.Okx => "wss://ws.okx.com/ws/v5/public",
                Venue.Bitget => "wss://ws.bitget.com/v2/ws/public",
                Venue.KuCoin => "wss://x-push-futures.kucoin.com",
                Venue.Gate => product == null ? "wss://api.gateio.ws/ws/v4/" : "wss://fx-ws.gateio.ws/v4/ws/" + product.Split('/')[1],
                Venue.Mexc => product == null ? "wss://wbs-api.mexc.com/ws" : "wss://contract.mexc.com/edge",
                _ => throw new ArgumentOutOfRangeException(nameof(venue))
            });

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(12);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(14));
            await socket.ConnectAsync(address, connect.Token);
        }
        var requests = Subscriptions(venue, symbols, product).ToArray();
        int remaining = venue is Venue.Okx or Venue.Bitget ? symbols.Length : requests.Length;
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = Receive(socket, venue, product, publish, () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                subscribed();
                confirmation.TrySetResult();
            }
        }, connection.Token);
        Task ping = Task.CompletedTask;
        try
        {
            using var subscribe = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            subscribe.CancelAfter(TimeSpan.FromSeconds(18));
            foreach (string request in requests)
            {
                await Send(socket, request, subscribe.Token);
                await Task.Delay(220, subscribe.Token);
                if (receiving.IsCompleted) await receiving;
            }
            var ready = await Task.WhenAny(receiving, confirmation.Task).WaitAsync(subscribe.Token);
            await ready;
            if (venue is not (Venue.Binance or Venue.Alpha))
                ping = Ping(socket, venue, product, heartbeat, connection.Token);
            await await Task.WhenAny(receiving, ping == Task.CompletedTask ? receiving : ping);
        }
        finally
        {
            await connection.CancelAsync();
            socket.Abort();
            try { await Task.WhenAll(ping, receiving); }
            catch (Exception) when (connection.IsCancellationRequested) { }
        }
    }

    internal static IEnumerable<string> Subscriptions(Venue venue, string[] symbols, string? product = null)
    {
        var batches = symbols.Chunk(venue == Venue.Bybit ? 10 : venue is Venue.Bitget or Venue.Okx ? 25 :
            product != null && venue is Venue.Mexc or Venue.KuCoin ? 1 : symbols.Length);
        int id = 0;
        foreach (string[] batch in batches)
        {
            id++;
            object message = venue switch
            {
                Venue.Binance or Venue.Alpha => new { method = "SUBSCRIBE",
                    @params = batch.Select(x => x.ToLowerInvariant() + "@aggTrade"), id },
                Venue.Bybit => new { op = "subscribe", args = batch.Select(x => "publicTrade." + x) },
                Venue.Okx => new { op = "subscribe", args = batch.Select(x => new { channel = "trades", instId = x }) },
                Venue.Bitget => new { op = "subscribe", args = batch.Select(x => new { instType = product ?? "SPOT", channel = "trade", instId = x }) },
                Venue.KuCoin when product != null => new { id = id.ToString(CultureInfo.InvariantCulture),
                    action = "SUBSCRIBE", channel = "trade", tradeType = "FUTURES", symbol = batch[0] },
                Venue.KuCoin => new { id = id.ToString(CultureInfo.InvariantCulture), type = "subscribe",
                    topic = "/market/match:" + string.Join(",", batch), response = true, privateChannel = false },
                Venue.Gate => new { time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), channel = product == null ? "spot.trades" : "futures.trades",
                    @event = "subscribe", payload = batch },
                Venue.Mexc when product != null => new { method = "sub.deal", param = new { symbol = batch[0] } },
                Venue.Mexc => new { method = "SUBSCRIPTION", @params = batch.Select(x => "spot@public.aggre.deals.v3.api.pb@100ms@" + x) },
                _ => throw new ArgumentOutOfRangeException(nameof(venue))
            };
            yield return JsonSerializer.Serialize(message);
        }
    }

    private static Task Send(ClientWebSocket socket, string message, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, ct);

    private static async Task Ping(ClientWebSocket socket, Venue venue, string? product, int heartbeat, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(heartbeat));
        while (await timer.WaitForNextTickAsync(ct))
        {
            string message = venue switch
            {
                Venue.Bybit => """{"op":"ping"}""",
                Venue.Mexc => product == null ? """{"method":"PING"}""" : """{"method":"ping"}""",
                Venue.KuCoin when product != null => JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), op = "ping" }),
                Venue.KuCoin => JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), type = "ping" }),
                Venue.Gate => JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), channel = product == null ? "spot.ping" : "futures.ping" }),
                _ => "ping"
            };
            await Send(socket, message, ct);
        }
    }

    private static async Task Receive(ClientWebSocket socket, Venue venue, string? product, Action<Trade> publish, Action acknowledge, CancellationToken ct)
    {
        byte[] buffer = new byte[16_384];
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Сервер закрыл поток");
            message.Write(buffer, 0, frame.Count);
            if (message.Length > 2_000_000) throw new IOException("Слишком большое сообщение");
            if (!frame.EndOfMessage) continue;
            var payload = message.GetBuffer().AsMemory(0, (int)message.Length);
            if (frame.MessageType == WebSocketMessageType.Binary && venue == Venue.Mexc)
                MexcTrades.Decode(payload.Span, publish);
            else if (payload.Length > 0 && payload.Span[0] == (byte)'{' &&
                (frame.MessageType == WebSocketMessageType.Text || venue == Venue.KuCoin && product != null))
            {
                using var document = JsonDocument.Parse(payload);
                DecodeJson(venue, document.RootElement, publish, product);
                if (IsAcknowledgement(venue, document.RootElement, product)) acknowledge();
            }
            message.SetLength(0);
        }
    }

    internal static bool IsAcknowledgement(Venue venue, JsonElement root, string? product = null) => venue switch
    {
        Venue.Binance or Venue.Alpha => root.TryGetProperty("id", out _) &&
            root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Null,
        Venue.Bybit => root.Optional("op") == "subscribe" && root.Optional("success") == "True",
        Venue.Okx or Venue.Bitget or Venue.Gate => root.Optional("event") == "subscribe",
        Venue.KuCoin => product == null ? root.Optional("type") == "ack" : root.Optional("result") == "True",
        Venue.Mexc when product != null => root.Optional("channel") == "rs.sub.deal",
        Venue.Mexc => root.Optional("code") == "0" && root.Optional("msg").Contains("spot@", StringComparison.Ordinal),
        _ => false
    };

    internal static void DecodeJson(Venue venue, JsonElement root, Action<Trade> emit, string? product = null)
    {
        if (root.Optional("event") == "error" || root.Optional("type") == "error" ||
            root.Optional("success") == "False" ||
            (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) ||
            (root.Optional("code") is { Length: > 0 } code && code != "0" && code != "200000"))
            throw new IOException("Биржа отклонила подписку");
        switch (venue)
        {
            case Venue.Binance:
            case Venue.Alpha:
                var trade = root.TryGetProperty("data", out var wrapped) ? wrapped : root;
                if (trade.Optional("e") == "aggTrade")
                    emit(new(trade.Str("s"), trade.GetProperty("T").Int(), trade.GetProperty("p").Num(), trade.Number("q"), trade.Optional("a")));
                break;
            case Venue.Bybit:
                if (root.Optional("topic").StartsWith("publicTrade.", StringComparison.Ordinal))
                    ReadRows(root, null, "s", "T", "p", "v", "i", emit);
                break;
            case Venue.Okx:
            case Venue.Bitget:
                if (root.TryGetProperty("arg", out var arg) && arg.Optional("channel") is "trades" or "trade" &&
                    root.TryGetProperty("data", out _))
                    ReadRows(root, arg.Str("instId"), null, "ts", venue == Venue.Okx ? "px" : "price",
                        venue == Venue.Okx ? "sz" : "size", "tradeId", emit);
                break;
            case Venue.KuCoin:
                if (product != null && root.Optional("T") == "trade.FUTURES")
                {
                    var tradeRow = root.GetProperty("d");
                    emit(new(tradeRow.Str("s"), tradeRow.GetProperty("M").Int() / 1_000_000, tradeRow.Number("p"),
                        tradeRow.Number("q"), tradeRow.Optional("ti")));
                }
                else if (product == null && root.Optional("type") == "message" &&
                    root.Optional("topic").StartsWith("/market/match:", StringComparison.Ordinal))
                {
                    var item = root.GetProperty("data");
                    emit(new(item.Str("symbol"), item.GetProperty("time").Int() / 1_000_000, item.GetProperty("price").Num(),
                        item.Number("size"), item.Optional("tradeId")));
                }
                break;
            case Venue.Gate:
                if (product != null && root.Optional("channel") == "futures.trades" && root.Optional("event") == "update")
                    foreach (var tradeRow in root.GetProperty("result").EnumerateArray())
                        emit(new(tradeRow.Str("contract"), (long)tradeRow.Number("create_time_ms"),
                            tradeRow.Number("price"), Math.Abs(tradeRow.Number("size")), tradeRow.Optional("id")));
                else if (root.Optional("channel") == "spot.trades" && root.Optional("event") == "update")
                {
                    var item = root.GetProperty("result");
                    emit(new(item.Str("currency_pair"), (long)item.GetProperty("create_time_ms").Num(), item.GetProperty("price").Num(),
                        item.Number("amount"), item.Optional("id")));
                }
                break;
            case Venue.Mexc when product != null && root.Optional("channel") == "push.deal":
                string symbol = root.Str("symbol");
                foreach (var item in root.GetProperty("data").EnumerateArray())
                    emit(new(symbol, (long)item.Number("t"), item.Number("p"), item.Number("v"), item.Optional("i")));
                break;
        }
    }

    private static void ReadRows(JsonElement root, string? symbol, string? symbolField,
        string timeField, string priceField, string sizeField, string idField, Action<Trade> emit)
    {
        // Some venues send snapshots or batches in reverse chronological order.
        var rows = root.GetProperty("data");
        if (rows.GetArrayLength() == 1)
        {
            var item = rows[0];
            emit(Parse(item));
            return;
        }
        foreach (var trade in rows.EnumerateArray()
            .Select(Parse)
            .OrderBy(x => x.Time))
            emit(trade);

        Trade Parse(JsonElement x) => new(symbol ?? x.Str(symbolField!), x.GetProperty(timeField).Int(),
            x.GetProperty(priceField).Num(), x.Number(sizeField), x.Optional(idField));
    }
}

internal static class MexcTrades
{
    // Fields from MEXC's PushDataV3ApiWrapper and PublicAggreDealsV3Api definitions.
    public static void Decode(ReadOnlySpan<byte> data, Action<Trade> publish)
    {
        string symbol = "";
        ReadOnlySpan<byte> deals = default;
        var reader = new WireReader(data);
        while (reader.Next(out int field, out var bytes, out _))
        {
            if (field == 3) symbol = Encoding.UTF8.GetString(bytes);
            if (field == 314) deals = bytes;
        }
        if (symbol.Length == 0 || deals.IsEmpty) return;
        var batch = new WireReader(deals);
        var trades = new List<Trade>();
        while (batch.Next(out int field, out var bytes, out _))
        {
            if (field != 1) continue;
            var item = new WireReader(bytes);
            double price = 0;
            double size = 0;
            long time = 0;
            string? id = null;
            while (item.Next(out int number, out var value, out ulong integer))
            {
                if (number == 1) double.TryParse(value, CultureInfo.InvariantCulture, out price);
                if (number == 2) double.TryParse(value, CultureInfo.InvariantCulture, out size);
                if (number == 4) time = (long)integer;
                if (number == 5) id = Encoding.UTF8.GetString(value);
            }
            if (time > 0 && price > 0) trades.Add(new(symbol, time, price, size, id));
        }
        foreach (var trade in trades.OrderBy(t => t.Time)) publish(trade);
    }

    private ref struct WireReader(ReadOnlySpan<byte> input)
    {
        private ReadOnlySpan<byte> remaining = input;

        private ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64 && !remaining.IsEmpty; shift += 7)
            {
                byte value = remaining[0];
                remaining = remaining[1..];
                result |= (ulong)(value & 127) << shift;
                if (value < 128) return result;
            }
            throw new IOException("Некорректное сообщение MEXC");
        }

        public bool Next(out int field, out ReadOnlySpan<byte> bytes, out ulong integer)
        {
            field = 0;
            bytes = default;
            integer = 0;
            if (remaining.IsEmpty) return false;
            ulong tag = Varint();
            field = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0: integer = Varint(); return true;
                case 1: bytes = Take(8); return true;
                case 2: bytes = Take(checked((int)Varint())); return true;
                case 5: bytes = Take(4); return true;
                default: throw new IOException("Неподдерживаемое сообщение MEXC");
            }
        }

        private ReadOnlySpan<byte> Take(int size)
        {
            if (size > remaining.Length) throw new IOException("Неполное сообщение MEXC");
            var data = remaining[..size];
            remaining = remaining[size..];
            return data;
        }
    }
}
