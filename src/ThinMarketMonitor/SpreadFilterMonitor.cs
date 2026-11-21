using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

namespace ThinMarketMonitor;

internal readonly record struct SpreadFilterResult(Market[] Markets, int Pending, int Unsupported);

internal sealed class SpreadFilterMonitor : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Market[] candidates;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, SpreadQuote>> batches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SpreadQuote> live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> errors = new(StringComparer.Ordinal);
    private readonly Task completion;

    internal static string BatchKey(Market m) => $"{m.Source}:{m.Contract?.Product}";

    public SpreadFilterMonitor(ExchangeApi api, IReadOnlyList<Market> candidates)
    {
        this.candidates = candidates.ToArray();
        var tasks = new List<Task>();
        foreach (var group in candidates.Where(ExchangeApi.SpreadSupported).Where(m => !ExchangeApi.StreamedBook(m.Venue))
                     .GroupBy(BatchKey))
        {
            var markets = group.ToArray();
            tasks.Add(Task.Run(() => Refresh(api, markets, stop.Token)));
        }
        var alphaMarkets = candidates.Where(m => m.Venue == Venue.Alpha && ExchangeApi.SpreadSupported(m)).ToArray();
        int index = 0;
        foreach (var group in alphaMarkets.Chunk(100))
        {
            int delay = index++ * 550;
            tasks.Add(Task.Run(async () =>
            {
                try { await Task.Delay(delay, stop.Token); await StreamAlpha(group, stop.Token); }
                catch (Exception) when (stop.IsCancellationRequested) { }
            }));
        }
        foreach (var venue in candidates.Where(m => m.Venue is Venue.Hyperliquid or Venue.Lighter).GroupBy(m => m.Venue))
        {
            int delay = 0;
            foreach (var group in venue.Chunk(ExtendedStreams.ChunkSize(venue.Key)))
            {
                int stagger = delay;
                tasks.Add(Task.Run(() => StreamExtended(api, group, stagger, stop.Token)));
                delay += 550;
            }
        }
        completion = Task.WhenAll(tasks);
    }

    public SpreadQuote? Quote(Market market) => ExchangeApi.StreamedBook(market.Venue)
        ? live.TryGetValue(market.Key, out var value) ? value : null
        : batches.TryGetValue(BatchKey(market), out var batch) && batch.TryGetValue(market.Key, out var quote) ? quote : null;

    public SpreadFilterResult Evaluate(IEnumerable<Market> markets, SpreadRule rule, long now) => Apply(markets, rule, Quote, now);

    internal static SpreadFilterResult Apply(IEnumerable<Market> markets, SpreadRule rule,
        Func<Market, SpreadQuote?> quote, long now)
    {
        if (!rule.Enabled) return new(markets.ToArray(), 0, 0);
        var accepted = new List<Market>();
        int pending = 0, unsupported = 0;
        foreach (var market in markets)
        {
            if (!ExchangeApi.SpreadSupported(market)) { unsupported++; continue; }
            if (quote(market)?.Percent(now) is not { } spread) { pending++; continue; }
            if (rule.Accepts(spread)) accepted.Add(market);
        }
        return new(accepted.ToArray(), pending, unsupported);
    }

    public string Coverage(long now) => string.Join("\n", candidates.GroupBy(BatchKey).Select(group =>
    {
        var first = group.First();
        int fresh = group.Count(m => Quote(m)?.Percent(now) != null);
        string detail = !ExchangeApi.SpreadSupported(first) ? "источник спреда не поддерживается" :
            errors.FirstOrDefault(e => e.Key == group.Key || e.Key.StartsWith(group.Key + ":", StringComparison.Ordinal)).Value ??
            (fresh == group.Count() ? "готово" : "ожидание свежих bid/ask");
        return $"Спред · {first.Source.Label}{(first.Contract == null ? "" : " · " + first.Contract.Product)} · {fresh}/{group.Count()} · {detail}";
    }));

    private async Task Refresh(ExchangeApi api, Market[] markets, CancellationToken ct)
    {
        string key = BatchKey(markets[0]);
        try
        {
            while (true)
            {
                int delay = 5000;
                try
                {
                    batches[key] = await api.BestQuotes(markets, ct);
                    errors.TryRemove(key, out _);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    batches.TryRemove(key, out _);
                    errors[key] = Error(ex);
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }) delay = 90_000;
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task StreamAlpha(Market[] markets, CancellationToken ct)
    {
        string key = BatchKey(markets[0]) + ":" + markets[0].Symbol;
        var lookup = markets.ToDictionary(m => m.Symbol, StringComparer.Ordinal);
        int backoff = 3;
        try
        {
            while (true)
            {
                long started = Environment.TickCount64;
                try
                {
                    using var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connect.CancelAfter(TimeSpan.FromSeconds(14));
                    await socket.ConnectAsync(new Uri("wss://nbstream.binance.com/w3w/wsa/stream/stream"), connect.Token);
                    byte[] subscription = JsonSerializer.SerializeToUtf8Bytes(new { method = "SUBSCRIBE",
                        @params = markets.Select(m => m.Symbol.ToLowerInvariant() + "@bookTicker").ToArray(), id = 1 });
                    await socket.SendAsync(new ArraySegment<byte>(subscription), WebSocketMessageType.Text, true, connect.Token);
                    errors.TryRemove(key, out _);
                    byte[] buffer = new byte[16_384];
                    using var message = new MemoryStream();
                    while (true)
                    {
                        using var receive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        receive.CancelAfter(TimeSpan.FromSeconds(30));
                        var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receive.Token);
                        if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Поток закрыт");
                        message.Write(buffer, 0, frame.Count);
                        if (message.Length > 2_000_000) throw new IOException("Слишком большое сообщение");
                        if (!frame.EndOfMessage) continue;
                        if (frame.MessageType == WebSocketMessageType.Text)
                        {
                            using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                            if (json.RootElement.TryGetProperty("error", out _)) throw new IOException("Подписка отклонена");
                            var row = json.RootElement.TryGetProperty("data", out var data) ? data : json.RootElement;
                            if (row.ValueKind == JsonValueKind.Object && row.Optional("e") == "bookTicker" &&
                                lookup.TryGetValue(row.Optional("s"), out var market))
                            {
                                var quote = DecodeAlpha(market, row);
                                PutQuote(market, quote);
                            }
                        }
                        message.SetLength(0);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { errors[key] = Error(ex); }
                finally { foreach (var market in markets) live.TryRemove(market.Key, out _); }
                if (Environment.TickCount64 - started > 120_000) backoff = 3;
                await Task.Delay(backoff * 1000 + Random.Shared.Next(1000), ct);
                backoff = Math.Min(60, backoff * 2);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task StreamExtended(ExchangeApi api, Market[] markets, int delay, CancellationToken ct)
    {
        var first = markets[0];
        string key = BatchKey(first) + ":" + first.Symbol;
        int backoff = 3;
        try
        {
            await Task.Delay(delay, ct);
            while (true)
            {
                long started = Environment.TickCount64;
                try
                {
                    void Connected() => errors.TryRemove(key, out _);
                    if (first.Venue == Venue.Hyperliquid)
                        await api.HyperStream.Listen(markets, true, Connected, null, PutQuote, ct);
                    else
                        await ExtendedStreams.Open(markets, true, false, Connected, null, PutQuote, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { errors[key] = ex is IOException ? ex.Message : Error(ex); }
                finally { foreach (var market in markets) live.TryRemove(market.Key, out _); }
                if (Environment.TickCount64 - started > 120_000) backoff = 3;
                await Task.Delay(backoff * 1000 + Random.Shared.Next(1000), ct);
                backoff = Math.Min(60, backoff * 2);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private void PutQuote(Market market, SpreadQuote quote) =>
        live.AddOrUpdate(market.Key, quote, (_, old) => quote.Time >= old.Time ? quote : old);

    internal static SpreadQuote DecodeAlpha(Market market, JsonElement row) =>
        ExchangeApi.DecodeBestQuote(market, row,
            long.TryParse(row.Optional("E"), out long time) ? time : 0);

    private static string Error(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode.HasValue => $"HTTP {(int)h.StatusCode.Value}",
        OperationCanceledException => "тайм-аут котировок",
        JsonException => "неподдерживаемый ответ API",
        _ => "источник котировок недоступен"
    };

    public void Dispose()
    {
        stop.Cancel();
        _ = completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
