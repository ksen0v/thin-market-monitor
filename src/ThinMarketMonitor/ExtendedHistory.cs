using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private static string HtxMarket(Market market) => market.Contract?.Product switch
    {
        "linear" => HtxContract + "linear-swap-ex/market/",
        "inverse" => HtxContract + "swap-ex/market/",
        "delivery" => HtxContract + "market/",
        _ => HtxSpot + "market/"
    };

    private static string HtxParameter(Market m) => m.Contract?.Product is "linear" or "inverse" ? "contract_code" : "symbol";
    private static string WeexMarket(Market m) => m.Contract == null ? WeexSpot + "market/" : WeexFutures;

    private async Task<Candle[]> ExtendedCandlePage(Market m, int frame, long before, int count, CancellationToken ct,
        Action<Candle[]>? onBatch)
    {
        if (frame < 60) return [];
        long unit = frame * 1000L;
        long start = Math.Max(0, before - unit * count);
        string symbol = Uri.EscapeDataString(m.Symbol);
        string interval = frame switch
        {
            60 => "1m", 300 => "5m", 900 => "15m", 3600 => "1h", 14400 => "4h", 86400 => "1d",
            _ => throw new ArgumentOutOfRangeException(nameof(frame))
        };
        long now = CandleNow;
        long open = CandleStart(now, unit, CandleOffset(m, frame));
        long current = open + unit;
        bool latestOnly = m.Venue is Venue.Kraken or Venue.Htx && m.Contract == null;
        if (latestOnly)
        {
            int limit = m.Venue == Venue.Kraken ? 720 : 2000;
            int offset = (int)Math.Clamp((current - before) / unit, 0, limit);
            if (offset >= limit) return [];
            int size = Math.Min(limit, count + offset + 1);
            return await Page(start, before, size);
        }
        var result = new List<Candle>();
        int page = m.Venue switch { Venue.Hyperliquid => 500, Venue.Lighter => 240, Venue.Weex => 100, _ => 450 };
        long end = before;
        if (m.Venue == Venue.Weex)
        {
            // The range endpoint rejects an endTime inside the open candle. The latest endpoint supplies it.
            if (before >= current)
            {
                int limit = Math.Min(count, m.Contract == null ? 100 : 1000);
                var latest = await ReadWeex($"klines?symbol={symbol}&interval={interval}&limit={limit}");
                var bars = InRange(latest, start, before);
                AddBatch(bars);
                if (bars.Length != 0) end = bars[0].Time;
            }
            end = Math.Min(end, open);
        }
        while (end > start)
        {
            long first = Math.Max(start, end - unit * page);
            AddBatch(await Page(first, end, (int)Math.Ceiling((end - first) / (double)unit)));
            end = first;
        }
        return result.DistinctBy(x => x.Time).OrderBy(x => x.Time).ToArray();

        void AddBatch(Candle[] bars)
        {
            if (bars.Length == 0) return;
            result.AddRange(bars);
            onBatch?.Invoke(bars);
        }

        async Task<Candle[]> Page(long from, long until, int size)
        {
            JsonElement root;
            if (m.Venue == Venue.Weex)
                root = await ReadWeex($"historyKlines?symbol={symbol}&interval={interval}&limit={size}" +
                    $"&startTime={from}&endTime={until - 1}" + (m.Contract == null ? "" : "&priceType=LAST"));
            else if (m.Venue == Venue.Hyperliquid)
                root = await HyperInfo("candleSnapshot", ct, req: new { coin = m.Symbol, interval, startTime = from, endTime = until - 1 });
            else
            {
                var url = m.Venue switch
                {
                    Venue.Htx => HtxMarket(m) + $"history/kline?{HtxParameter(m)}={symbol}&period={HtxInterval(frame)}" +
                        (latestOnly ? $"&size={size}" : $"&from={from / 1000}&to={(until - 1) / 1000}"),
                    Venue.Kraken when m.Contract == null => KrakenSpot + $"OHLC?pair={Uri.EscapeDataString(m.RestSymbol)}&interval={frame / 60}&since={from / 1000}",
                    Venue.Kraken => KrakenCharts + $"{symbol}/{interval}?from={from / 1000}&to={(until - 1) / 1000}&count={size}",
                    Venue.Lighter => Lighter + $"candles?market_id={symbol}&resolution={interval}&start_timestamp={from}&end_timestamp={until - 1}&count_back={size}",
                    _ => throw new ArgumentOutOfRangeException(nameof(m))
                };
                await PaceHistory(m.Venue, ct);
                root = await Get(url, ct);
            }
            return InRange(root, from, until);
        }

        Candle[] InRange(JsonElement root, long from, long until)
        {
            var inRange = DecodeExtendedCandles(m, root).Where(c => c.Time >= from && c.Time < until).ToArray();
            var valid = inRange.Where(c => c.Low > 0 &&
                double.IsFinite(c.High) && double.IsFinite(c.Open) && double.IsFinite(c.Close) &&
                c.High >= Math.Max(c.Open, c.Close) && c.Low <= Math.Min(c.Open, c.Close))
                .DistinctBy(c => c.Time).OrderBy(c => c.Time).ToArray();
            if (inRange.Length != 0 && valid.Length == 0) throw new JsonException($"{m.Venue.Label()} · некорректные цены свечей");
            return valid;
        }

        async Task<JsonElement> ReadWeex(string path)
        {
            await PaceHistory(Venue.Weex, ct);
            using var response = await http.GetAsync(WeexMarket(m) + path, ct);
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.Optional("msg") is { Length: > 0 } message)
                    throw new HttpRequestException($"WEEX · HTTP {(int)response.StatusCode} · {root.Optional("code")} {message}",
                        null, response.StatusCode);
                response.EnsureSuccessStatusCode();
                return root.Clone();
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                response.EnsureSuccessStatusCode();
                throw;
            }
        }
    }

    private static string HtxInterval(int frame) => frame switch
    {
        60 => "1min", 300 => "5min", 900 => "15min", 3600 => "60min", 14400 => "4hour", _ => "1day"
    };

    private static JsonElement KrakenPairSeries(Market market, JsonElement root)
    {
        var result = KrakenResult(root);
        if (result.TryGetProperty(market.Symbol, out var series)) return series;
        if (result.TryGetProperty(market.RestSymbol, out series)) return series;
        throw new JsonException("Kraken вернул данные другой пары");
    }

    internal static IEnumerable<Candle> DecodeExtendedCandles(Market m, JsonElement root)
    {
        var rows = m.Venue switch
        {
            Venue.Htx => HtxField(root, "data"),
            Venue.Kraken when m.Contract == null => KrakenPairSeries(m, root),
            Venue.Kraken => root.GetProperty("candles"),
            Venue.Lighter => LighterField(root, "c"),
            _ => root
        };
        foreach (var row in rows.EnumerateArray())
        {
            switch (m.Venue)
            {
                case Venue.Htx:
                    double htx = m.Contract?.Unit == QuantityUnit.Quote ? m.Turnover(row.Number("vol"), 1) :
                        row.Number(m.Contract == null ? "vol" : "trade_turnover");
                    yield return new(row.GetProperty("id").Int() * 1000, row.Number("open"), row.Number("high"), row.Number("low"),
                        row.Number("close"), ValidVolume(htx));
                    break;
                case Venue.Kraken when m.Contract == null:
                    // Public OHLC supplies base volume and the same candle's actual weighted average trade price.
                    yield return new(row[0].Int() * 1000, row[1].Num(), row[2].Num(), row[3].Num(), row[4].Num(),
                        ValidVolume(row[5].Num() * row[6].Num()));
                    break;
                case Venue.Kraken:
                    yield return new(row.GetProperty("time").Int(), row.Number("open"), row.Number("high"), row.Number("low"),
                        row.Number("close"), m.Contract?.Unit == QuantityUnit.Quote ? ValidVolume(m.Turnover(row.Number("volume"), 1)) : null);
                    break;
                case Venue.Weex:
                    yield return new(row[0].Int(), row[1].Num(), row[2].Num(), row[3].Num(), row[4].Num(), At(row, 7));
                    break;
                default:
                    // Lighter has native quote volume V. Hyperliquid v is base size and lacks a VWAP.
                    yield return new(row.GetProperty("t").Int(), row.Number("o"), row.Number("h"), row.Number("l"), row.Number("c"),
                        m.Venue == Venue.Lighter ? ValidVolume(row.Number("V")) : null);
                    break;
            }
        }
    }

    private async Task<Trade[]> ExtendedRecentTrades(Market m, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(m.Symbol);
        JsonElement root;
        if (m.Venue == Venue.Hyperliquid) root = await HyperInfo("recentTrades", ct, coin: m.Symbol);
        else
        {
            var url = m.Venue switch
            {
                Venue.Htx => HtxMarket(m) + $"history/trade?{HtxParameter(m)}={symbol}&size=100",
                Venue.Kraken when m.Contract == null => KrakenSpot + $"Trades?pair={Uri.EscapeDataString(m.RestSymbol)}&count=250",
                Venue.Kraken => KrakenFutures + $"history?symbol={symbol}",
                Venue.Weex => WeexMarket(m) + $"trades?symbol={symbol}&limit=100",
                Venue.Lighter => Lighter + $"recentTrades?market_id={symbol}&limit=100",
                _ => throw new ArgumentOutOfRangeException(nameof(m))
            };
            await PaceHistory(m.Venue, ct);
            root = await Get(url, ct);
        }
        var rows = m.Venue switch
        {
            Venue.Htx => HtxField(root, "data").EnumerateArray().SelectMany(x => x.GetProperty("data").EnumerateArray()),
            Venue.Kraken when m.Contract == null => KrakenPairSeries(m, root).EnumerateArray().AsEnumerable(),
            Venue.Kraken => KrakenFuturesField(root, "history").EnumerateArray().AsEnumerable(),
            Venue.Lighter => LighterField(root, "trades").EnumerateArray().AsEnumerable(),
            _ => root.EnumerateArray().AsEnumerable()
        };
        return rows.Select(row => m.Venue switch
        {
            Venue.Htx => new Trade(m.Symbol, row.GetProperty("ts").Int(), row.Number("price"), row.Number("amount"),
                row.Optional("tradeId") is { Length: > 0 } tradeId ? tradeId : row.Optional("id")),
            Venue.Kraken when m.Contract == null => new(m.Symbol, (long)(row[2].Num() * 1000), row[0].Num(), row[1].Num(),
                row.GetArrayLength() > 6 ? row[6].Str() : null),
            Venue.Kraken => new(m.Symbol, IsoTime(row.Optional("time")), row.Number("price"), row.Number("size"),
                row.Optional("sequence_id")),
            Venue.Weex => new(m.Symbol, row.GetProperty("time").Int(), row.Number("price"), row.Number("qty"), row.Optional("id")),
            Venue.Lighter => new(m.Symbol, row.GetProperty("timestamp").Int(), row.Number("price"), row.Number("size"), row.Optional("trade_id")),
            _ => new(m.Symbol, row.GetProperty("time").Int(), row.Number("px"), row.Number("sz"), row.Optional("tid"))
        }).Where(t => t.Time > 0 && double.IsFinite(t.Price) && t.Price > 0)
            .OrderBy(t => t.Time).ToArray();
    }
}
