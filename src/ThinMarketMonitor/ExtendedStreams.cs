using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text.Json;

namespace ThinMarketMonitor;

internal static class ExtendedStreams
{
    public static int ChunkSize(Venue venue) => venue switch
    {
        Venue.Htx => 60, Venue.Kraken => 60, Venue.Weex => 100, Venue.Lighter => 50, Venue.Hyperliquid => 120, _ => 100
    };

    internal static Uri Address(Market m) => new(m.Venue switch
    {
        Venue.Htx => m.Contract?.Product switch
        {
            "linear" => "wss://api.hbdm.com/linear-swap-ws",
            "inverse" => "wss://api.hbdm.com/swap-ws",
            "delivery" => "wss://api.hbdm.com/ws",
            _ => "wss://api.huobi.pro/ws"
        },
        Venue.Kraken => m.Contract == null ? "wss://ws.kraken.com/v2" : "wss://uws.kraken.com/v2/public",
        Venue.Weex => m.Contract == null ? "wss://ws-spot.weex.com/v3/ws/public" : "wss://ws-contract.weex.com/v3/ws/public",
        Venue.Lighter => "wss://mainnet.zklighter.elliot.ai/stream",
        Venue.Hyperliquid => "wss://api.hyperliquid.xyz/ws",
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    });

    private static readonly SemaphoreSlim WeexSockets = new(20);
    private static readonly SemaphoreSlim WeexBackgroundSockets = new(18);

    public static async Task Open(Market[] markets, bool book, bool chart, Action ready, Action<Trade>? trade,
        Action<Market, SpreadQuote>? quote, CancellationToken ct)
    {
        Market first = markets[0];
        bool reserved = false, background = false;
        try
        {
            if (first.Venue == Venue.Weex)
            {
                if (!chart)
                {
                    background = WeexBackgroundSockets.Wait(0);
                    if (!background) throw new IOException("WEEX · лимит публичных соединений");
                }
                reserved = WeexSockets.Wait(0);
                if (!reserved) throw new IOException("WEEX · лимит публичных соединений");
            }
            await Run(first, markets, book, ready, trade, quote, ct);
        }
        finally
        {
            if (reserved) WeexSockets.Release();
            if (background) WeexBackgroundSockets.Release();
        }
    }

    private static async Task Run(Market first, Market[] markets, bool book, Action ready, Action<Trade>? trade,
        Action<Market, SpreadQuote>? quote, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var socket = Socket();
        if (first.Venue == Venue.Weex) socket.Options.SetRequestHeader("User-Agent", "ThinMarketMonitor/0.8");
        await Connect(socket, Address(first), session.Token);
        var lookup = markets.ToDictionary(m => m.StreamSymbol, StringComparer.Ordinal);
        var ack = new HashSet<string>(StringComparer.Ordinal);
        var htxStarts = new Dictionary<string, long>(StringComparer.Ordinal);
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var write = new SemaphoreSlim(1);
        var reader = Receive();
        var heartbeat = Heartbeat();
        try
        {
            foreach (var request in Requests(first, lookup.Keys.ToArray(), book))
            {
                await Write(request);
                await Task.Delay(first.Venue == Venue.Lighter ? 170 : 120, session.Token);
                if (reader.IsCompleted) await reader;
            }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await await Task.WhenAny(reader, confirmation.Task).WaitAsync(deadline.Token);
            }
            await await Task.WhenAny(reader, heartbeat);
        }
        finally
        {
            await session.CancelAsync();
            socket.Abort();
            try { await Task.WhenAll(reader, heartbeat); } catch (Exception) when (session.IsCancellationRequested) { }
        }

        async Task Write(object message)
        {
            await write.WaitAsync(session.Token);
            try { await Send(socket, message, session.Token); }
            finally { write.Release(); }
        }

        async Task Heartbeat()
        {
            if (first.Venue != Venue.Kraken) { await Task.Delay(Timeout.Infinite, session.Token); return; }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(session.Token)) await Write(new { method = "ping" });
        }

        async Task Receive()
        {
            byte[] buffer = new byte[16384];
            using var message = new MemoryStream();
            while (true)
            {
                using var json = await Read(socket, buffer, message, first.Venue == Venue.Htx, session.Token);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string type = root.Optional("type"), channel = root.Optional("channel");
                if (first.Venue == Venue.Htx && root.TryGetProperty("ping", out var ping))
                { await Write(new { pong = ping.Int() }); continue; }
                if (first.Venue == Venue.Weex && root.Optional("event") == "ping")
                { await Write(new { method = "PONG", id = 0 }); continue; }
                if (first.Venue == Venue.Lighter && type == "ping")
                { await Write(new { type = "pong" }); continue; }
                if (root.Optional("status") == "error" || root.Optional("success") == "False" || type == "error" ||
                    root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ||
                    first.Venue == Venue.Weex && root.Optional("result") == "False")
                    throw new IOException($"{first.Venue.Label()} · {root.Optional("error")}{root.Optional("err-msg")}{root.Optional("msg")}");
                if (first.Venue == Venue.Weex && root.Optional("id") == "1" && root.Optional("result") == "True")
                    ack.UnionWith(lookup.Keys);
                else if (first.Venue == Venue.Kraken && root.Optional("method") == "subscribe" &&
                         root.TryGetProperty("result", out var result))
                    ack.Add(result.Optional("symbol"));
                else if (first.Venue == Venue.Htx && HtxTopic(root.Optional("subbed")) is { Length: > 0 } symbol)
                {
                    ack.Add(symbol);
                    if (root.TryGetProperty("ts", out var start)) htxStarts[symbol] = start.Int();
                }
                else if (first.Venue == Venue.Lighter && type == (book ? "subscribed/ticker" : "subscribed/trade"))
                    ack.Add(ChannelSymbol(channel));
                if (!confirmation.Task.IsCompleted && lookup.Keys.All(ack.Contains))
                {
                    ready();
                    confirmation.TrySetResult();
                }
                if (book && quote != null)
                {
                    if (DecodeBook(first.Venue, root) is { } decoded && lookup.TryGetValue(decoded.Symbol, out var m))
                        quote(m, decoded.Quote);
                }
                else if (trade != null)
                {
                    foreach (var t in DecodeTrades(first, root))
                        if (lookup.TryGetValue(t.Symbol, out var m) && ack.Contains(t.Symbol) && t.Time >= htxStarts.GetValueOrDefault(t.Symbol))
                            trade(t with { Symbol = m.Symbol });
                }
            }
        }
    }

    internal static IEnumerable<object> Requests(Market first, string[] symbols, bool book)
    {
        switch (first.Venue)
        {
            case Venue.Htx:
                foreach (var symbol in symbols) yield return new { sub = $"market.{symbol}.trade.detail", id = symbol };
                break;
            case Venue.Kraken when first.Contract == null:
                yield return new { method = "subscribe", @params = new { channel = "trade", symbol = symbols, snapshot = false } };
                break;
            case Venue.Kraken:
                yield return new { method = "subscribe", @params = new { channel = "trade", asset_class = "derivatives", symbol = symbols, snapshot = false } };
                break;
            case Venue.Weex:
                yield return new { method = "SUBSCRIBE", @params = symbols.Select(s => s + "@trade").ToArray(), id = 1 };
                break;
            case Venue.Lighter:
                foreach (var symbol in symbols) yield return new { type = "subscribe", channel = $"{(book ? "ticker" : "trade")}/{symbol}" };
                break;
        }
    }

    internal static Trade[] DecodeTrades(Market m, JsonElement root)
    {
        IEnumerable<Trade> trades = [];
        string type = root.Optional("type"), channel = root.Optional("channel");
        if (m.Venue == Venue.Htx && HtxTopic(root.Optional("ch")) is { Length: > 0 } symbol &&
            root.TryGetProperty("tick", out var tick))
        {
            var applied = new HashSet<string>(StringComparer.Ordinal);
            trades = tick.GetProperty("data").EnumerateArray().Select((row, index) =>
            {
                string id = row.Optional("tradeId");
                if (id.Length == 0) id = row.Optional("id");
                if (id.Length > 0 && !applied.Add(id)) id += ":" + index;
                return new Trade(symbol, row.GetProperty("ts").Int(), row.Number("price"), row.Number("amount"), id);
            });
        }
        else if (m.Venue == Venue.Kraken && channel == "trade" && type == "update")
            trades = root.GetProperty("data").EnumerateArray().Select(row => new Trade(row.Str("symbol"),
                ExchangeApi.IsoTime(row.Optional("timestamp")), row.Number("price"), row.Number("qty"), row.Optional("trade_id")));
        else if (m.Venue == Venue.Weex && root.Optional("e") == "trade")
            trades = root.GetProperty("d").EnumerateArray().Select(row => new Trade(root.Str("s"),
                row.GetProperty("T").Int(), row.Number("p"), row.Number("q"), row.Optional("t")));
        else if (m.Venue == Venue.Lighter && type == "update/trade")
        {
            string index = ChannelSymbol(channel);
            trades = new[] { "trades", "liquidation_trades" }
                .SelectMany(property => root.TryGetProperty(property, out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray().AsEnumerable() : [])
                .Where(row => row.Optional("market_id") == index)
                .Select(row => new Trade(index, row.GetProperty("timestamp").Int(), row.Number("price"), row.Number("size"), row.Optional("trade_id")));
        }
        else if (m.Venue == Venue.Hyperliquid && channel == "trades")
            trades = root.GetProperty("data").EnumerateArray().Select(row => new Trade(row.Str("coin"),
                row.GetProperty("time").Int(), row.Number("px"), row.Number("sz"), row.Optional("tid")));
        return trades.OrderBy(t => t.Time).ToArray();
    }

    internal static (string Symbol, SpreadQuote Quote)? DecodeBook(Venue venue, JsonElement root)
    {
        if (venue == Venue.Hyperliquid && root.Optional("channel") == "bbo")
        {
            var data = root.GetProperty("data");
            var levels = data.GetProperty("bbo");
            if (levels.GetArrayLength() != 2) throw new JsonException("Некорректный BBO Hyperliquid");
            return (data.Str("coin"), Book(levels[0], levels[1], "px", "sz", data.GetProperty("time").Int()));
        }
        if (venue == Venue.Lighter && root.Optional("type") is "subscribed/ticker" or "update/ticker")
        {
            var data = root.GetProperty("ticker");
            long time = root.TryGetProperty("last_updated_at", out var updated) ? updated.Int() / 1000 : 0;
            return (ChannelSymbol(root.Str("channel")), Book(Level(data, "b"), Level(data, "a"), "price", "size", time));
        }
        return null;

        JsonElement Level(JsonElement row, string key) => row.TryGetProperty(key, out var side) ? side : default;
        SpreadQuote Book(JsonElement bid, JsonElement ask, string price, string size, long time) =>
            ExchangeApi.SizedBook(Number(bid, price), Number(ask, price), Number(bid, size), Number(ask, size), time);
        double Number(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object ? row.Number(key) : double.NaN;
    }

    private static string HtxTopic(string topic) => topic.StartsWith("market.", StringComparison.Ordinal) &&
        topic.EndsWith(".trade.detail", StringComparison.Ordinal) ? topic[7..^13] : "";
    private static string ChannelSymbol(string channel) => channel.Contains(':') ? channel.Split(':', 2)[1] : "";

    public static ClientWebSocket Socket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(18);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(12);
        return socket;
    }

    public static async Task Connect(ClientWebSocket socket, Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(14));
        await socket.ConnectAsync(address, timeout.Token);
    }

    public static async Task Send(ClientWebSocket socket, object value, CancellationToken ct) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, ct);

    public static async Task<JsonDocument> Read(ClientWebSocket socket, byte[] buffer, MemoryStream message, bool gzip, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(70));
        message.SetLength(0);
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Биржа закрыла поток");
            if (message.Length + frame.Count > 2_000_000) throw new IOException("Слишком большое сообщение");
            message.Write(buffer, 0, frame.Count);
            if (!frame.EndOfMessage) continue;
            if (gzip && frame.MessageType == WebSocketMessageType.Binary)
            {
                message.Position = 0;
                using var unzip = new GZipStream(message, CompressionMode.Decompress, leaveOpen: true);
                using var plain = new MemoryStream();
                int size;
                while ((size = unzip.Read(buffer)) != 0)
                {
                    if (plain.Length + size > 2_000_000) throw new IOException("Слишком большое сообщение HTX");
                    plain.Write(buffer, 0, size);
                }
                return JsonDocument.Parse(plain.ToArray());
            }
            return JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        }
    }
}
