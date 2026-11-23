using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const long InterestPollMs = 55_000;
    private const long InterestDay = 86_400_000;
    private const int InterestPagesPerRead = 3;
    private readonly record struct InterestPage(long From, long Until, bool Archive);
    private sealed record InterestSlice(InterestPoint[] Points, long RetryAt = long.MaxValue, bool Failed = false);

    private sealed class InterestCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<InterestPage, InterestSlice> History = [];
        public long Touched, Checked;
        public InterestPoint[] Tail = [];
        public string? LatestStatus;
    }

    private readonly ConcurrentDictionary<(string Market, int Period), InterestCache> interests = new();

    public async Task<InterestReading> OpenInterest(Market market, int frame, CancellationToken ct, InterestWindow? visible = null)
    {
        if (InterestRoute(market, frame) is not { } route)
            return new("", 0, [], "Публичные данные для контракта недоступны");
        long touched = Environment.TickCount64;
        if (interests.Count > 96)
            foreach (var item in interests.Where(item => touched - Volatile.Read(ref item.Value.Touched) > 600_000))
                interests.TryRemove(item);
        var cache = interests.GetOrAdd((market.Key, route.Period), _ => new());
        Volatile.Write(ref cache.Touched, touched);
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (cache.Checked == 0 || Environment.TickCount64 - cache.Checked >= InterestPollMs)
                await RefreshInterestTail(market, route, cache, ct);
            if (visible is not { } window || window.From >= window.Until)
                return new(route.Unit, route.Period, cache.Tail, LatestStatus());

            bool pending = false, failed = false, inArchive = false;
            var pages = new List<InterestPoint[]>();
            if (route.History != null)
            {
                // The live tail covers the right edge. Request stable, reusable pages for the rest of the viewport.
                long now = CandleNow;
                long until = Math.Min(window.Until, cache.Tail.Length == 0 ? now + 1 : cache.Tail[0].Time);
                int fetches = 0;
                foreach (var page in InterestPages(market.Venue, route, Math.Max(0, window.From), until, now))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!cache.History.TryGetValue(page, out var slice) || Environment.TickCount64 >= slice.RetryAt)
                    {
                        if (fetches == InterestPagesPerRead)
                        {
                            pending = true;
                            break;
                        }
                        fetches++;
                        slice = await ReadInterestSlice(market, route, page, ct);
                        cache.History[page] = slice;
                    }
                    if (slice.Failed) { failed = true; break; }
                }
                foreach (var (page, slice) in cache.History)
                {
                    if (page.From >= window.Until || page.Until <= window.From || slice.Points.Length == 0) continue;
                    inArchive |= page.Archive;
                    pages.Add(slice.Points);
                }
            }

            string? status = pending ? "Подгружается история OI" : failed ? "Часть истории OI временно недоступна" :
                inArchive ? "История из архива Binance" :
                route.History == null && route.Period > 0 && cache.Tail.Length > 0 && window.From < cache.Tail[0].Time
                    ? "Доступно последнее окно биржи" : LatestStatus();
            var points = pages.Count == 0 ? cache.Tail : cache.Tail.Concat(pages.SelectMany(page => page))
                .DistinctBy(point => point.Time).OrderBy(point => point.Time).ToArray();
            return new(route.Unit, route.Period, points, status, pending);
        }
        finally { cache.Gate.Release(); }

        string? LatestStatus() => cache.LatestStatus ??
            (route.Period == 0 ? "Текущие значения · история с момента сбора" : null);
    }

    private async Task RefreshInterestTail(Market market, InterestRequest route, InterestCache cache, CancellationToken ct)
    {
        try
        {
            int count = Math.Min(route.PageSize, cache.Tail.Length == 0 ? 240 : 8);
            long observed = CandleNow;
            JsonElement root;
            if (market.Venue == Venue.Hyperliquid)
            {
                string dex = market.Symbol.Contains(':') ? market.Symbol.Split(':', 2)[0] : "";
                (root, observed) = await CachedTicker("hyperliquid:perp-contexts:" + dex,
                    token => HyperInfo("metaAndAssetCtxs", token, dex: dex), ct);
            }
            else if (market.Venue == Venue.Lighter)
                (root, observed) = await TickerBatch(route.Latest(count, observed), ct);
            else
            {
                await PaceHistory(market.Venue, ct);
                root = await Get(route.Latest(count, observed), ct);
                observed = CandleNow;
            }
            var points = DecodeInterest(market, root, route.Period, observed);
            var prices = cache.Tail.Where(point => point.Usd.HasValue).ToDictionary(point => point.Time);
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                if (point.Usd == null && prices.TryGetValue(point.Time, out var previous) && point.Value == previous.Value)
                    points[i] = previous;
            }
            await PriceInterest(market, route.Period, points, ct);
            ct.ThrowIfCancellationRequested();
            cache.Tail = points.Concat(cache.Tail).DistinctBy(point => point.Time).OrderBy(point => point.Time).TakeLast(1600).ToArray();
            cache.LatestStatus = points.Length == 0 ? "Биржа не вернула значения OI" : null;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
        {
            cache.LatestStatus = "Последние значения OI недоступны";
        }
        cache.Checked = Environment.TickCount64;
    }

    private static IEnumerable<InterestPage> InterestPages(Venue venue, InterestRequest route, long from, long until, long now)
    {
        // Use UTC day boundaries to join Binance's rolling REST window with its daily public files.
        long restStart = venue == Venue.Binance ? CandleStart(now, InterestDay) - 29 * InterestDay : 0;
        long pageSize = route.Period * 1000L * route.PageSize;
        while (until > from)
        {
            bool archive = venue == Venue.Binance && until <= restStart;
            long width = archive ? InterestDay : pageSize;
            long start = CandleStart(until - 1, width);
            yield return new(archive ? start : Math.Max(start, restStart), start + width, archive);
            until = archive ? start : Math.Max(start, restStart);
        }
    }

    private async Task<InterestSlice> ReadInterestSlice(Market market, InterestRequest route, InterestPage page, CancellationToken ct)
    {
        try
        {
            await PaceHistory(market.Venue, ct);
            long now = CandleNow;
            var points = page.Archive
                ? await BinanceInterestArchive(market, route.Period, page.From, ct)
                : DecodeInterest(market, await Get(route.History!(page.From, Math.Min(page.Until, now + 1)), ct), route.Period, now);
            points = points.Where(point => point.Time >= page.From &&
                (page.Archive ? point.Time <= page.Until : point.Time < page.Until)).ToArray();
            await PriceInterest(market, route.Period, points, ct);
            ct.ThrowIfCancellationRequested();
            // Today can still gain new points. The live tail handles the overlap with completed pages.
            long retry = points.Length == 0 ? Environment.TickCount64 + 600_000 : long.MaxValue;
            return new(points, retry);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            (ChartFeatures.ExpectedNetworkError(error) || error is InvalidDataException))
        {
            return new([], Environment.TickCount64 + InterestPollMs, Failed: true);
        }
    }

    private async Task<InterestPoint[]> BinanceInterestArchive(Market market, int period, long day, CancellationToken ct)
    {
        string family = market.Contract?.Product == "coinm" ? "cm" : "um";
        string date = DateTimeOffset.FromUnixTimeMilliseconds(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string symbol = Uri.EscapeDataString(market.Symbol);
        string url = $"https://data.binance.vision/data/futures/{family}/daily/metrics/{symbol}/{symbol}-metrics-{date}.zip";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        const int zipLimit = 400_000;
        using var source = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[zipLimit + 1];
        int count = await source.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
        if (count > zipLimit) throw new InvalidDataException("Файл Binance слишком велик");
        using var zip = new ZipArchive(new MemoryStream(bytes, 0, count));
        if (zip.Entries is not [var entry] || !entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || entry.Length > 800_000)
            throw new InvalidDataException("Неожиданный формат архива Binance");
        using var reader = new StreamReader(entry.Open());
        return await ReadInterestArchive(reader, market, period, day, ct);
    }

    internal static async Task<InterestPoint[]> ReadInterestArchive(TextReader reader, Market market, int period, long day, CancellationToken ct)
    {
        var culture = CultureInfo.InvariantCulture;
        string? header = await reader.ReadLineAsync(ct);
        if (header?.StartsWith("create_time,symbol,sum_open_interest,sum_open_interest_value", StringComparison.Ordinal) != true)
            throw new InvalidDataException("Не найдены столбцы OI в архиве Binance");
        var points = new List<InterestPoint>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            string[] fields = line.Split(',', 5);
            if (fields.Length < 4 || fields[1] != market.Symbol ||
                !DateTimeOffset.TryParseExact(fields[0], ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"],
                    culture, DateTimeStyles.AssumeUniversal, out var time) ||
                !double.TryParse(fields[2], NumberStyles.Float, culture, out double value) || !ValidUsd(value).HasValue) continue;
            long stamp = time.ToUnixTimeMilliseconds();
            if (stamp < day || stamp > day + InterestDay) continue;
            double? usd = market.Contract?.Product == "coinm" ? OiFaceValue(market, value) :
                UsdInterestQuote(market) && double.TryParse(fields[3], NumberStyles.Float, culture, out double quoteValue) ? ValidUsd(quoteValue) : null;
            points.Add(new(stamp, value, usd));
        }
        // Preserve real observation times while reducing 5-minute archive samples for wider chart timeframes.
        return points.OrderByDescending(point => point.Time).DistinctBy(point => CandleStart(point.Time, period * 1000L))
            .OrderBy(point => point.Time).ToArray();
    }
}
