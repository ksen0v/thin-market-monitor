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
    public IReadOnlyList<MarketSource> PendingCatalogs { get; private set; } = [];

    public void Configure(DensityOptions options, IReadOnlyDictionary<MarketSource, Catalog> catalogs)
    {
        if (disposed) return;
        var sources = MarketSource.All.Where(options.Scans).ToArray();
        var eligible = ListingFilter.ApplySources(catalogs, options.Rule, sources);
        PendingCatalogs = eligible.Missing;
        var blocked = options.Blacklist.ToHashSet(StringComparer.Ordinal);
        var marketsByVenue = eligible.Markets.Where(m => !blocked.Contains(m.ListingAsset) && ExchangeApi.SupportsDensity(m))
            .ToLookup(m => m.Venue);
        var filters = new ScanFilters(options.IgnoreFlicker, options.IgnoreMirrored, options.Popup || options.Sound);
        foreach (var venue in Enum.GetValues<Venue>())
        {
            var rule = options.Venues.GetValueOrDefault(venue);
            if (rule == null || !sources.Any(source => source.Venue == venue))
            {
                if (scans.Remove(venue, out var disabled)) disabled.Dispose();
                continue;
            }

            var defaults = rule.Parameters();
            var targets = marketsByVenue[venue].DistinctBy(m => m.Key).OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(market => new ScanTarget(market, options.CoinRules.GetValueOrDefault(market.ListingAsset) ?? defaults)).ToArray();
            if (!scans.TryGetValue(venue, out var scan))
                scans[venue] = new(api, venue, targets, filters, notices.Writer);
            else
            {
                scan.Filters = filters;
                scan.SetTargets(targets);
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
    private sealed record ScanTarget(Market Market, DensityRule Rule);

    private sealed class Scan : IDisposable
    {
        private sealed class State(ScanTarget target)
        {
            public ScanTarget Target { get; } = target;
            public Market Market => Target.Market;
            public DensityTracker Tracker { get; } = new(target.Market, target.Rule);
            public int[] Depths { get; } = ExchangeApi.DensityDepths(target.Market);
            public int DepthIndex;
            public int ShallowerSamples;
            public bool NeedsDepth;
            public bool Limited;
            public long Attempt;
            public bool Sampled;
            public bool Failed;
        }

        private sealed record Active(ScanTarget Target, DensityLevel[] Levels, long Expires);
        private sealed class TargetSet(ScanTarget[] targets)
        {
            public ScanTarget[] Targets { get; } = targets;
            public Dictionary<string, ScanTarget> ByKey { get; } = targets.ToDictionary(target => target.Market.Key, StringComparer.Ordinal);
        }
        private readonly ExchangeApi api;
        private readonly ChannelWriter<DensityLevel> notices;
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<string, Active> active = new(StringComparer.Ordinal);
        private readonly int batchSize;
        private readonly Venue venue;
        private readonly Task worker;
        private volatile TargetSet targetSet;
        private volatile DensityVenueStatus status;
        private volatile bool disposed;
        public volatile ScanFilters Filters;
        public DensityVenueStatus Status => status;

        public Scan(ExchangeApi api, Venue venue, ScanTarget[] targets, ScanFilters filters,
            ChannelWriter<DensityLevel> notices)
        {
            this.api = api;
            this.venue = venue;
            this.notices = notices;
            targetSet = new(targets);
            Filters = filters;
            batchSize = venue switch { Venue.Alpha or Venue.Lighter or Venue.Kraken => 2, Venue.Hyperliquid => 3, _ => 6 };
            status = new(venue, targets.Length, 0, 0, 0, 0, null);
            worker = Task.Run(() => Run(stop.Token));
        }

        public void SetTargets(ScanTarget[] next)
        {
            if (!targetSet.Targets.SequenceEqual(next)) targetSet = new(next);
        }

        public DensityLevel[] Levels(string key, long now) =>
            active.TryGetValue(key, out var entry) && IsCurrent(entry, targetSet, now) ? entry.Levels : [];

        public IEnumerable<DensityLevel> Visible(long now)
        {
            var targets = targetSet;
            return active.Values.Where(entry => IsCurrent(entry, targets, now)).SelectMany(entry => entry.Levels);
        }

        private static bool IsCurrent(Active entry, TargetSet targets, long now) =>
            entry.Expires >= now && targets.ByKey.GetValueOrDefault(entry.Target.Market.Key) == entry.Target;

        private async Task Run(CancellationToken ct)
        {
            var states = new Dictionary<string, State>(StringComparer.Ordinal);
            TargetSet? previous = null;
            State[] ordered = [];
            int cursor = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var current = targetSet;
                    if (!ReferenceEquals(previous, current))
                    {
                        foreach (string key in states.Keys)
                            if (!current.ByKey.ContainsKey(key)) Remove(key);
                        foreach (var target in current.Targets)
                            if (!states.TryGetValue(target.Market.Key, out var existing) || existing.Target != target)
                            {
                                Remove(target.Market.Key);
                                states.Add(target.Market.Key, new(target));
                            }
                        ordered = current.Targets.Select(target => states[target.Market.Key]).ToArray();
                        cursor = ordered.Length == 0 ? 0 : cursor % ordered.Length;
                        previous = current;
                        status = new(venue, ordered.Length, 0, 0, 0, 0, null);
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
                    status = new(venue, ordered.Length, ordered.Count(s => s.Sampled), failed,
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
                var book = await api.ReadDensityBook(state.Market, state.Depths[state.DepthIndex], state.Target.Rule.MaxDistance, ct)
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
                else active[state.Market.Key] = new(state.Target, levels, now + maxGap);
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
