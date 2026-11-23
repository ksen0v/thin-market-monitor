using System.Collections.Concurrent;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const int InterestPoints = 240;
    private const long InterestPollMs = 55_000;
    private sealed class InterestCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Touched;
        public long Checked;
        public InterestReading? Reading;
    }
    private sealed record InterestRequest(int Period, string Unit, Func<int, long, string> Url);
    private readonly ConcurrentDictionary<(string Market, int Period), InterestCache> interests = new();

    public static bool SupportsOpenInterest(Market market) => InterestRoute(market, 60) != null;

    public async Task<InterestReading> OpenInterest(Market market, int frame, CancellationToken ct)
    {
        if (InterestRoute(market, frame) is not { } route)
            return new("", 0, [], "Публичные данные для контракта недоступны");
        long now = Environment.TickCount64;
        if (interests.Count > 256)
            foreach (var item in interests.Where(item => now - Volatile.Read(ref item.Value.Touched) > 600_000))
                interests.TryRemove(item);
        var cache = interests.GetOrAdd((market.Key, route.Period), _ => new());
        Volatile.Write(ref cache.Touched, now);
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (cache.Reading != null && Environment.TickCount64 - cache.Checked < InterestPollMs) return cache.Reading;
            try
            {
                int limit = cache.Reading?.Points.Length > 0 ? 8 : 200;
                long receivedAt = CandleNow;
                JsonElement root;
                if (market.Venue == Venue.Hyperliquid)
                {
                    string dex = market.Symbol.Contains(':') ? market.Symbol.Split(':', 2)[0] : "";
                    (root, receivedAt) = await CachedTicker("hyperliquid:perp-contexts:" + dex,
                        token => HyperInfo("metaAndAssetCtxs", token, dex: dex), ct);
                }
                else if (market.Venue == Venue.Lighter)
                    (root, receivedAt) = await TickerBatch(route.Url(limit, receivedAt), ct);
                else
                {
                    await PaceHistory(market.Venue, ct);
                    root = await Get(route.Url(limit, receivedAt), ct);
                    receivedAt = CandleNow;
                }
                ct.ThrowIfCancellationRequested();
                var points = DecodeInterest(market, root, route.Period, receivedAt);
                var previous = cache.Reading?.Points ?? [];
                cache.Reading = new(route.Unit, route.Period, points.Reverse().Concat(previous).DistinctBy(p => p.Time)
                    .OrderBy(p => p.Time).TakeLast(InterestPoints).ToArray(),
                    points.Length == 0 ? "Биржа не вернула данные" : null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is System.Net.Http.HttpRequestException or System.IO.IOException or JsonException or OperationCanceledException
                    or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                cache.Reading = new(route.Unit, route.Period, cache.Reading?.Points ?? [], "Данные биржи недоступны");
            }
            cache.Checked = Environment.TickCount64;
            return cache.Reading;
        }
        finally { cache.Gate.Release(); }
    }

    private static InterestRequest? InterestRoute(Market market, int tf)
    {
        if (market.Contract is not { } contract) return null;
        string symbol = Uri.EscapeDataString(market.Symbol), product = contract.Product;
        string basis = contract.NativeBase ?? market.Base;
        int step = tf <= 300 ? 300 : tf <= 900 ? 900 : tf <= 3600 ? 3600 : tf <= 14400 ? 14400 : 86400;
        string interval = step switch { 300 => "5m", 900 => "15m", 3600 => "1h", 14400 => "4h", _ => "1d" };
        string minuteInterval = step switch { 300 => "5min", 900 => "15min", 3600 => "1hour", 14400 => "4hour", _ => "1day" };
        string bybitInterval = step < 3600 ? minuteInterval : interval;
        int htxStep = Math.Max(3600, step);
        string htxInterval = htxStep switch { 3600 => "60min", 14400 => "4hour", _ => "1day" };
        return market.Venue switch
        {
            Venue.Binance when product == "usdm" => new(step, basis, (n, _) =>
                $"https://fapi.binance.com/futures/data/openInterestHist?symbol={symbol}&period={interval}&limit={n}"),
            Venue.Binance when product == "coinm" && market.Symbol.EndsWith("_PERP", StringComparison.Ordinal) => new(step, "контр.", (n, _) =>
                $"https://dapi.binance.com/futures/data/openInterestHist?pair={symbol[..^5]}&contractType=PERPETUAL&period={interval}&limit={n}"),
            Venue.Binance when product == "coinm" => new(0, "контр.", (_, _) => Coinm + $"openInterest?symbol={symbol}"),
            Venue.Bybit when contract.Perpetual => new(step, product == "inverse" ? market.Quote : basis, (n, _) =>
                Bybit + $"open-interest?category={product}&symbol={symbol}&intervalTime={bybitInterval}&limit={n}"),
            Venue.Okx => new(Math.Min(step, 14400), "контр.", (n, _) => Okx +
                $"rubik/stat/contracts/open-interest-history?instId={symbol}&period={(step >= 14400 ? "4H" : step == 3600 ? "1H" : interval)}&limit={Math.Min(n, 100)}"),
            Venue.Bitget when product is "USDT-FUTURES" or "USDC-FUTURES" => new(step, basis, (_, _) =>
                $"https://api.bitget.com/api/v3/market/open-interest-history?symbol={symbol}&period={interval}"),
            Venue.Bitget => new(0, basis, (_, _) =>
                $"https://api.bitget.com/api/v3/market/open-interest?category={product}&symbol={symbol}"),
            Venue.Mexc => new(0, "контр.", (_, _) => MexcContract + $"ticker?symbol={symbol}"),
            Venue.KuCoin => new(step, "контр.", (n, _) =>
                $"https://api.kucoin.com/api/ua/v1/market/open-interest?symbol={symbol}&interval={minuteInterval}&pageSize={Math.Min(n, 100)}"),
            Venue.Gate when product.StartsWith("futures/", StringComparison.Ordinal) => new(step, "контр.", (n, _) =>
                GateContract + $"{product}/contract_stats?contract={symbol}&interval={interval}&limit={Math.Min(n, 100)}"),
            Venue.Htx when product is "linear" or "inverse" => new(htxStep, "контр.", (n, _) => HtxContract +
                $"{(product == "linear" ? "linear-swap-api" : "swap-api")}/v1/swap_his_open_interest?contract_code={symbol}&period={htxInterval}&amount_type=1&size={n}"),
            Venue.Htx when product == "delivery" => new(0, "контр.", (_, _) => HtxContract + $"api/v1/contract_open_interest?contract_code={symbol}"),
            Venue.Kraken => new(step, "контр.", (n, now) => "https://futures.kraken.com/api/charts/v1/analytics/" +
                $"{symbol}/open-interest?interval={step}&since={now / 1000 - (long)step * n}"),
            Venue.Weex => new(0, "контр.", (_, _) => WeexFutures + $"openInterest?symbol={symbol}"),
            Venue.Lighter => new(0, "USD", (_, _) => Lighter + $"orderBookDetails?market_id={symbol}"),
            Venue.Hyperliquid => new(0, basis, (_, _) => ""),
            _ => null
        };
    }

    internal static InterestPoint[] DecodeInterest(Market market, JsonElement root, int period, long observedAt)
    {
        string symbol = market.Symbol;
        IEnumerable<InterestPoint> points = market.Venue switch
        {
            Venue.Binance when period == 0 => root.Optional("symbol") == symbol ? [Point(root, "time", "openInterest")] : [],
            Venue.Binance => root.EnumerateArray().Where(row => market.Contract?.Product == "coinm"
                ? row.Optional("pair") + "_PERP" == symbol && row.Optional("contractType") == "PERPETUAL"
                : row.Optional("symbol") == symbol).Select(row => Point(row, "timestamp", "sumOpenInterest")),
            Venue.Bybit => MatchedBybit(),
            Venue.Okx => Envelope(root, "0").EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 4)
                .Select(row => new InterestPoint(row[0].Int(), row[1].Num())),
            Venue.Bitget => BitgetInterest(),
            Venue.Mexc => MexcInterest(),
            Venue.KuCoin => Envelope(root, "200000").EnumerateArray().Select(row => Point(row, "ts", "openInterest")),
            Venue.Gate => root.EnumerateArray().Select(row => Point(row, "time", "open_interest", 1000)),
            Venue.Htx => HtxInterest(),
            Venue.Kraken => KrakenInterest(),
            Venue.Weex => root.Optional("symbol") == symbol ? [Point(root, "time", "openInterest")] : [],
            Venue.Lighter => Named(LighterField(root, "order_book_details"), "market_id")
                .Where(row => row.Optional("market_type") == "perp")
                .Select(row => new InterestPoint(observedAt, row.Number("open_interest_usd"))),
            Venue.Hyperliquid => HyperInterest(),
            _ => []
        };
        return points.Where(point => point.Time > 0 && point.Time <= observedAt + 3000 &&
            double.IsFinite(point.Value) && point.Value >= 0).ToArray();

        IEnumerable<JsonElement> Named(JsonElement container, string field) =>
            container.EnumerateArray().Where(row => row.Optional(field) == symbol);

        IEnumerable<InterestPoint> MatchedBybit()
        {
            var data = BybitResult(root);
            return data.Optional("symbol") == symbol && data.Optional("category") == market.Contract?.Product
                ? data.GetProperty("list").EnumerateArray().Select(row => Point(row, "timestamp", "singleOpenInterest")) : [];
        }

        IEnumerable<InterestPoint> BitgetInterest()
        {
            var data = Envelope(root, "00000");
            return Named(data.GetProperty("list"), "symbol").Select(row => period == 0
                ? new InterestPoint((long)data.Number("ts"), row.Number("openInterest"))
                : Point(row, "timestamp", "openInterest"));
        }

        IEnumerable<InterestPoint> MexcInterest()
        {
            var data = Envelope(root, "0");
            return data.Optional("symbol") == symbol ? [Point(data, "timestamp", "holdVol")] : [];
        }

        IEnumerable<InterestPoint> HtxInterest()
        {
            var data = HtxField(root, "data");
            if (period == 0)
                return Named(data, "contract_code").Select(row => new InterestPoint((long)root.Number("ts"), row.Number("volume")));
            return data.Optional("contract_code") == symbol
                ? data.GetProperty("tick").EnumerateArray().Where(row => row.Optional("amount_type") == "1")
                    .Select(row => Point(row, "ts", "volume")) : [];
        }

        IEnumerable<InterestPoint> KrakenInterest()
        {
            var result = root.GetProperty("result");
            var timestamp = result.GetProperty("timestamp");
            var data = result.GetProperty("data");
            if (timestamp.GetArrayLength() != data.GetArrayLength()) throw new JsonException("Несогласованные данные Kraken");
            // In the OHLC response use the opening reading at the bucket timestamp, not the later close.
            return Enumerable.Range(0, timestamp.GetArrayLength()).Select(i =>
                new InterestPoint(timestamp[i].Int() * 1000, data[i].ValueKind == JsonValueKind.Array ? data[i][0].Num() : data[i].Num()));
        }

        IEnumerable<InterestPoint> HyperInterest()
        {
            var universe = root[0].GetProperty("universe");
            var contexts = root[1];
            if (universe.GetArrayLength() != contexts.GetArrayLength()) throw new JsonException("Несогласованные данные Hyperliquid");
            return Enumerable.Range(0, universe.GetArrayLength()).Where(i => universe[i].Optional("name") == symbol)
                .Select(i => new InterestPoint(observedAt, contexts[i].Number("openInterest")));
        }
    }

    private static InterestPoint Point(JsonElement row, string time, string number, int scale = 1) =>
        new((long)(row.Number(time) * scale), row.Number(number));
}
