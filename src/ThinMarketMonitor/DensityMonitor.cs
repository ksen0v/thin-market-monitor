using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;

namespace ThinMarketMonitor;

internal sealed record DensityVenueStatus(Venue Venue, int Markets, int Sampled, int Failed, int Limited,
    long CycleSeconds, string? Error);

internal sealed class DensityMonitor(ExchangeApi api) : IDisposable
{
    private readonly Dictionary<Venue, Scan> scans = [];
    private readonly Channel<DensityLevel> notices = Channel.CreateBounded<DensityLevel>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
    });
    private bool disposed;

    public void Configure(DensityOptions options, IReadOnlyDictionary<MarketSource, Catalog> catalogs)
    {
        if (disposed) return;
        foreach (var venue in Enum.GetValues<Venue>())
        {
            var rule = options.Venues.GetValueOrDefault(venue);
            var scan = scans.GetValueOrDefault(venue);
            if (rule == null || (!rule.Spot && !rule.Futures))
            {
                if (scans.Remove(venue)) scan!.Dispose();
                continue;
            }

            var markets = MarketSource.All.Where(s => s.Venue == venue && rule.Enabled(s.Segment))
                .SelectMany(s => catalogs.GetValueOrDefault(s) is { Error: null } catalog ? catalog.Markets : [])
                .Where(ExchangeApi.SupportsDensity).DistinctBy(m => m.Key).OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
            var filters = new ScanFilters(options.IgnoreFlicker, options.IgnoreMirrored, options.Popup || options.Sound);
            if (scan == null || scan.Rule != rule)
            {
                scan?.Dispose();
                scans[venue] = new(api, venue, markets, rule, filters, notices.Writer);
            }
            else
            {
                scan.Filters = filters;
                scan.SetMarkets(markets);
            }
        }
    }

    public DensityLevel[] ForMarket(Market? market)
    {
        if (market == null || !scans.TryGetValue(market.Venue, out var scan)) return [];
        return scan.Levels(market.Key, Now);
    }

    public DensityLevel[] Visible()
    {
        long now = Now;
        return scans.Values.SelectMany(s => s.Visible(now)).ToArray();
    }

    public DensityVenueStatus[] Status() => scans.Values.Select(s => s.Status).ToArray();

    public bool TryRead(out DensityLevel? level)
    {
        while (notices.Reader.TryRead(out var pending))
        {
            var current = ForMarket(pending.Market);
            if (Now - pending.Seen <= 20_000 && current.Any(x => x.Key == pending.Key && x.FirstSeen == pending.FirstSeen))
            {
                level = pending;
                return true;
            }
        }
        level = null;
        return false;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var scan in scans.Values) scan.Dispose();
        scans.Clear();
        notices.Writer.TryComplete();
    }

    private sealed record ScanFilters(bool IgnoreFlicker, bool IgnoreMirrored, bool Notify);

    private sealed class Scan : IDisposable
    {
        private sealed class State(Market market, DensityVenueRule rule)
        {
            public Market Market { get; } = market;
            public DensityTracker Tracker { get; } = new(market, rule);
            public int[] Depths { get; } = ExchangeApi.DensityDepths(market);
            public int DepthIndex;
            public int ShallowerSamples;
            public bool NeedsDepth;
            public bool Limited;
            public long Attempt;
            public bool Sampled;
            public bool Failed;
        }

        private sealed record Active(DensityLevel[] Levels, long Expires);
        private readonly ExchangeApi api;
        private readonly ChannelWriter<DensityLevel> notices;
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<string, Active> active = new(StringComparer.Ordinal);
        private readonly int batchSize;
        private readonly Venue venue;
        private readonly Task worker;
        private volatile Market[] markets;
        private volatile DensityVenueStatus status;
        private volatile bool disposed;
        public volatile ScanFilters Filters;
        public DensityVenueRule Rule { get; }
        public DensityVenueStatus Status => status;

        public Scan(ExchangeApi api, Venue venue, Market[] markets, DensityVenueRule rule, ScanFilters filters,
            ChannelWriter<DensityLevel> notices)
        {
            this.api = api;
            this.venue = venue;
            this.notices = notices;
            this.markets = markets;
            Rule = rule;
            Filters = filters;
            batchSize = venue switch { Venue.Alpha or Venue.Lighter or Venue.Kraken => 2, Venue.Hyperliquid => 3, _ => 6 };
            status = new(venue, markets.Length, 0, 0, 0, 0, null);
            worker = Task.Run(() => Run(stop.Token));
        }

        public void SetMarkets(Market[] next)
        {
            if (!markets.SequenceEqual(next)) markets = next;
        }

        public DensityLevel[] Levels(string key, long now) =>
            active.TryGetValue(key, out var entry) && entry.Expires >= now ? entry.Levels : [];

        public IEnumerable<DensityLevel> Visible(long now) =>
            active.Values.Where(a => a.Expires >= now).SelectMany(a => a.Levels);

        private async Task Run(CancellationToken ct)
        {
            var states = new Dictionary<string, State>(StringComparer.Ordinal);
            Market[]? previous = null;
            State[] ordered = [];
            int cursor = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var current = markets;
                    if (!ReferenceEquals(previous, current))
                    {
                        var keys = current.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
                        foreach (string key in states.Keys)
                            if (!keys.Contains(key)) Remove(key);
                        foreach (var market in current)
                            if (!states.TryGetValue(market.Key, out var existing) || existing.Market != market)
                            {
                                Remove(market.Key);
                                states.Add(market.Key, new(market, Rule));
                            }
                        ordered = current.Select(m => states[m.Key]).ToArray();
                        cursor = ordered.Length == 0 ? 0 : cursor % ordered.Length;
                        previous = current;
                        status = new(venue, current.Length, 0, 0, 0, 0, null);
                    }

                    if (ordered.Length == 0)
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        continue;
                    }
                    long started = Stopwatch.GetTimestamp();
                    long now = Now;
                    var batch = new HashSet<State>();
                    int discoverySlots = Math.Max(1, batchSize / 2);
                    Fill(discoverySlots);
                    var tracked = ordered.Where(s => s.Tracker.HasCandidates || s.NeedsDepth).ToArray();
                    foreach (var state in tracked.Where(s => now - s.Attempt >= 2500).OrderBy(s => s.Attempt))
                    {
                        if (batch.Count >= batchSize) break;
                        batch.Add(state);
                    }
                    Fill(batchSize);
                    long cycleSeconds = (ordered.Length + discoverySlots - 1L) / discoverySlots;
                    long maxGap = Math.Clamp((tracked.Length / Math.Max(1, batchSize - discoverySlots) + 2L) * 2000,
                        12_000, 600_000);
                    if (batch.Count == 0)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }
                    var results = await Task.WhenAll(batch.Select(s => Sample(s, now, maxGap, ct))).ConfigureAwait(false);
                    string? error = results.FirstOrDefault(r => r.Error != null).Error;
                    int failed = ordered.Count(s => s.Failed);
                    status = new(venue, current.Length, ordered.Count(s => s.Sampled), failed,
                        ordered.Count(s => s.Limited), cycleSeconds,
                        error ?? (failed > 0 ? status.Error : null));
                    var delay = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(started);
                    var backoff = results.Max(r => r.Delay);
                    if (results.All(r => r.Error != null)) backoff = Max(backoff, TimeSpan.FromSeconds(3));
                    delay = Max(delay, backoff);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);

                    void Fill(int limit)
                    {
                        for (int i = 0; i < ordered.Length && batch.Count < limit; i++)
                        {
                            var state = ordered[cursor];
                            cursor = (cursor + 1) % ordered.Length;
                            if (now - state.Attempt >= 2500) batch.Add(state);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

            void Remove(string key)
            {
                states.Remove(key);
                active.TryRemove(key, out _);
            }
        }

        private async Task<(string? Error, TimeSpan Delay)> Sample(State state, long started, long maxGap, CancellationToken ct)
        {
            state.Attempt = started;
            try
            {
                var book = await api.ReadDensityBook(state.Market, state.Depths[state.DepthIndex], Rule.MaxDistance, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                state.Limited = book.Limited;
                state.NeedsDepth = book.Limited && state.DepthIndex + 1 < state.Depths.Length;
                if (state.NeedsDepth)
                {
                    state.DepthIndex++;
                    state.ShallowerSamples = 0;
                }
                else if (!book.Limited && state.DepthIndex > 0 &&
                         book.ReadDepth < state.Depths[state.DepthIndex - 1])
                {
                    if (++state.ShallowerSamples >= 3)
                    {
                        state.DepthIndex--;
                        state.ShallowerSamples = 0;
                    }
                }
                else state.ShallowerSamples = 0;
                long now = Now;
                var filters = Filters;
                List<DensityLevel>? appeared = filters.Notify ? [] : null;
                var levels = state.Tracker.Apply(book, now, maxGap, filters.IgnoreFlicker, filters.IgnoreMirrored,
                    level => appeared?.Add(level));
                if (levels.Length == 0) active.TryRemove(state.Market.Key, out _);
                else active[state.Market.Key] = new(levels, now + maxGap);
                if (appeared != null)
                    foreach (var level in appeared)
                        if (!disposed) notices.TryWrite(level);
                state.Sampled = true;
                state.Failed = false;
                return (null, TimeSpan.Zero);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                DensityRateLimit or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or
                FormatException or OverflowException or IndexOutOfRangeException or OperationCanceledException)
            {
                active.TryRemove(state.Market.Key, out _);
                state.Tracker.Reset();
                state.Failed = true;
                state.Limited = false;
                string description = ExchangeApi.ConnectionProblem(ex) ?? ex.Message;
                var delay = ex is DensityRateLimit limited ? Max(limited.Delay, TimeSpan.FromSeconds(1)) : TimeSpan.Zero;
                return ($"{state.Market.Label} · {description}", delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay);
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }
}
