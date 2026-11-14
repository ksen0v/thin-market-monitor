using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace ThinMarketMonitor;

internal readonly record struct VolumeFilterResult(Market[] Markets, int Pending, int Unsupported);
internal readonly record struct VolumeSnapshot(long Time, IReadOnlyDictionary<int, double> Amounts);

internal sealed class VolumeFilterMonitor : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<MarketSource, (long Time, Dictionary<string, double> Values)> daily = new();
    private readonly ConcurrentDictionary<string, VolumeSnapshot> history = new(StringComparer.Ordinal);
    private readonly Task completion;
    private readonly Market[] candidates;
    private readonly VolumeRule[] rules;
    private readonly long validFor;
    private readonly long alphaDayValidFor;

    public VolumeFilterMonitor(ExchangeApi api, IReadOnlyList<Market> candidates, VolumeRule[] rules)
    {
        this.candidates = candidates.ToArray();
        this.rules = rules;
        int count = candidates.GroupBy(m => m.Venue).Select(g => g.Count(m => UsdQuotes.Supported(m.Quote))).DefaultIfEmpty().Max();
        validFor = Math.Clamp(count * 850L, 180_000, 600_000);
        alphaDayValidFor = Math.Clamp(candidates.Count(m => m.Venue == Venue.Alpha) * 1800L, 180_000, 1_200_000);
        var tasks = new List<Task>();
        foreach (var group in candidates.Where(UsdQuotes.Supported).GroupBy(m => m.Source))
        {
            var bulk = group.Where(ExchangeApi.HasBulkDay).ToArray();
            if (bulk.Length > 0 && rules.Any(r => r.Seconds == 86400))
                tasks.Add(Task.Run(() => RefreshDay(api, bulk, stop.Token)));
            int[] periods = rules.Select(r => r.Seconds).Distinct().ToArray();
            var historical = group.Where(m => periods.Any(s => s != 86400 || !ExchangeApi.HasBulkDay(m))).ToArray();
            if (historical.Length > 0) tasks.Add(Task.Run(() => RefreshHistory(api, historical, periods, stop.Token)));
        }
        completion = Task.WhenAll(tasks);
    }

    public VolumeFilterResult Evaluate(long now) => Apply(candidates, rules, (market, seconds) =>
    {
        if (seconds == 86400 && ExchangeApi.HasBulkDay(market))
            return daily.TryGetValue(market.Source, out var day) && now - day.Time <= 180_000 &&
                   day.Values.TryGetValue(market.Key, out double turnover) ? turnover : null;
        long maxAge = market.Venue == Venue.Alpha && seconds == 86400 ? alphaDayValidFor : validFor;
        return history.TryGetValue(market.Key, out var bars) && now - bars.Time <= maxAge &&
               bars.Amounts.TryGetValue(seconds, out double value) ? value : null;
    });

    internal static VolumeFilterResult Apply(IEnumerable<Market> markets, IEnumerable<VolumeRule> conditions,
        Func<Market, int, double?> getVolume)
    {
        var rules = conditions.Where(r => r.Enabled).ToArray();
        if (rules.Length == 0) return new(markets.ToArray(), 0, 0);
        var accepted = new List<Market>();
        int pending = 0, unsupported = 0;
        foreach (var market in markets)
        {
            if (!UsdQuotes.Supported(market)) { unsupported++; continue; }
            bool unknown = false, rejected = false;
            foreach (var rule in rules)
            {
                double? volume = getVolume(market, rule.Seconds);
                if (volume == null) unknown = true;
                else if (volume.Value < rule.Usd) { rejected = true; break; }
            }
            if (rejected) continue;
            if (unknown) pending++;
            else accepted.Add(market);
        }
        return new(accepted.ToArray(), pending, unsupported);
    }

    private async Task RefreshDay(ExchangeApi api, Market[] markets, CancellationToken ct)
    {
        var source = markets[0].Source;
        try
        {
            while (true)
            {
                int delay = 60_000;
                try
                {
                    var values = await api.Snapshots(markets, ct);
                    daily[source] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values.Where(x => x.Value.Turnover.HasValue)
                        .ToDictionary(x => x.Key, x => x.Value.Turnover!.Value));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    daily.TryRemove(source, out _);
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }) delay = 90_000;
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private async Task RefreshHistory(ExchangeApi api, Market[] markets, int[] periods, CancellationToken ct)
    {
        int interval = markets[0].Venue is Venue.Mexc or Venue.Alpha ? 600 : 350;
        bool alphaDay = markets[0].Venue == Venue.Alpha && periods.Contains(86400);
        long next = 0;
        async Task Pace(CancellationToken token)
        {
            long wait = next - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait, token);
            next = Environment.TickCount64 + interval;
        }

        try
        {
            while (true)
            {
                foreach (var market in markets)
                {
                    try
                    {
                        int[] candlePeriods = periods.Where(s => s != 86400 || !alphaDay && !ExchangeApi.HasBulkDay(market)).ToArray();
                        var amounts = await api.HistoricalVolumes(market, candlePeriods, Pace, ct);
                        if (alphaDay)
                        {
                            await Pace(ct);
                            if (await api.AlphaDayVolume(market, ct) is { } day) amounts[86400] = day;
                        }
                        history[market.Key] = new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), amounts);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        history.TryRemove(market.Key, out _);
                        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                            await Task.Delay(60_000, ct);
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        _ = completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
