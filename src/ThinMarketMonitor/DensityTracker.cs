namespace ThinMarketMonitor;

internal record DensityRule(double SmallUsd = 10_000, double LargeUsd = 50_000,
    double MinDistance = 0, double MaxDistance = 5, int MinSeconds = 15,
    string SmallColor = "#61BBDB", string LargeColor = "#EFB863")
{
    public bool Valid => SmallUsd is > 0 and <= 1e12 && LargeUsd > SmallUsd && LargeUsd <= 1e12 &&
        MinDistance is >= 0 and <= 100 && MaxDistance >= MinDistance && MaxDistance <= 100 &&
        MinSeconds is >= 0 and <= 86_400 && IsColor(SmallColor) && IsColor(LargeColor);

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.Skip(1).All(Uri.IsHexDigit);
}

internal sealed record DensityVenueRule(bool Spot = false, bool Futures = false, double SmallUsd = 10_000,
    double LargeUsd = 50_000, double MinDistance = 0, double MaxDistance = 5, int MinSeconds = 15,
    string SmallColor = "#61BBDB", string LargeColor = "#EFB863") :
    DensityRule(SmallUsd, LargeUsd, MinDistance, MaxDistance, MinSeconds, SmallColor, LargeColor)
{
    public bool Enabled(Segment segment) => segment == Segment.Spot ? Spot : Futures;

    internal DensityRule Parameters() => new(SmallUsd, LargeUsd, MinDistance, MaxDistance, MinSeconds, SmallColor, LargeColor);
}

internal sealed class DensityOptions
{
    public Dictionary<Venue, DensityVenueRule> Venues { get; set; } = [];
    public Dictionary<Venue, ListingRule> SpotRules { get; set; } = [];
    public Dictionary<Venue, ListingRule> FuturesRules { get; set; } = [];
    public string[] Blacklist { get; set; } = [];
    public Dictionary<string, DensityRule> CoinRules { get; set; } = new(StringComparer.Ordinal);
    public bool OverviewOverlay { get; set; }
    public bool SignalsOverlay { get; set; }
    public bool Popup { get; set; }
    public bool Sound { get; set; }
    public bool IgnoreFlicker { get; set; }
    public bool IgnoreMirrored { get; set; }

    internal bool Scans(MarketSource source) => Venues.GetValueOrDefault(source.Venue)?.Enabled(source.Segment) == true;

    internal bool NeedsCatalog(MarketSource source) =>
        Scans(source) || (Rule(source) != ListingRule.Ignore && MarketSource.All.Any(Scans));

    internal void ToggleSource(MarketSource source)
    {
        var current = Venues.GetValueOrDefault(source.Venue) ?? new();
        Venues[source.Venue] = source.Segment == Segment.Spot ? current with { Spot = !current.Spot } :
            current with { Futures = !current.Futures };
    }

    private Dictionary<Venue, ListingRule> ListingRules(Segment segment) => segment == Segment.Spot ? SpotRules : FuturesRules;

    internal ListingRule Rule(MarketSource source) => ListingRules(source.Segment).GetValueOrDefault(source.Venue);
    internal void CycleListingRule(MarketSource source) => ListingFilter.Cycle(ListingRules(source.Segment), source.Venue);

    internal bool Block(Market market)
    {
        string asset = market.ListingAsset;
        if (string.IsNullOrWhiteSpace(asset) || Blacklist.Contains(asset, StringComparer.Ordinal)) return false;
        Blacklist = Blacklist.Append(asset).Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    public void Normalize()
    {
        Venues = (Venues ?? []).Where(pair => Enum.IsDefined(pair.Key) && pair.Value is { Valid: true })
            .ToDictionary();
        SpotRules = ActiveRules(SpotRules, Segment.Spot);
        FuturesRules = ActiveRules(FuturesRules, Segment.Futures);
        Blacklist = (Blacklist ?? []).Where(asset => !string.IsNullOrWhiteSpace(asset)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        CoinRules = (CoinRules ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { Valid: true })
            .ToDictionary(StringComparer.Ordinal);

        static Dictionary<Venue, ListingRule> ActiveRules(Dictionary<Venue, ListingRule>? rules, Segment segment) =>
            (rules ?? []).Where(pair => MarketSource.All.Contains(new MarketSource(pair.Key, segment)) &&
                pair.Value is ListingRule.Require or ListingRule.Exclude).ToDictionary();
    }
}

internal enum BookSide { Bid, Ask }
internal readonly record struct BookLevel(double Price, double Quantity);
internal sealed record DensityBook(BookLevel[] Bids, BookLevel[] Asks, int ReadDepth = 0,
    bool BidLimited = false, bool AskLimited = false)
{
    public bool Limited => BidLimited || AskLimited;
}
internal readonly record struct DensityKey(BookSide Side, double Price);
internal sealed record DensityLevel(Market Market, DensityKey Key, double Usd, double Distance, long FirstSeen, long Seen,
    bool Large, string Color)
{
    public string SideLabel => Key.Side == BookSide.Bid ? "Покупка" : "Продажа";
}

internal sealed class DensityTracker(Market market, DensityRule rule)
{
    private const long FlickerWindow = 120_000;
    private const long QuickDisappearance = 20_000;
    private const double MirrorRatio = 1.5;
    private const int MirrorPairs = 3;
    private sealed class Observation(long time)
    {
        public long FirstSeen = time;
        public long MissingSince;
        public long LastFlicker;
        public int Flickers;
        public int Samples;
        public bool Published;
    }

    private readonly Dictionary<DensityKey, Observation> observations = [];
    private long lastSample;
    public bool HasCandidates { get; private set; }

    public void Reset()
    {
        observations.Clear();
        HasCandidates = false;
    }

    // Age advances only on successful observations of the same price level.
    public DensityLevel[] Apply(DensityBook book, long now, long maxGap, bool ignoreFlicker, bool ignoreMirrored,
        Action<DensityLevel> appeared)
    {
        if (now <= lastSample) return [];
        if (now - lastSample > maxGap) Reset();
        lastSample = now;
        if (book.Bids.Length == 0 || book.Asks.Length == 0 || book.Bids[0].Price >= book.Asks[0].Price)
        {
            Reset();
            return [];
        }
        double mid = book.Bids[0].Price / 2 + book.Asks[0].Price / 2;
        var candidates = new Dictionary<DensityKey, DensityLevel>();
        Collect(book.Bids, BookSide.Bid);
        Collect(book.Asks, BookSide.Ask);

        foreach (var (key, observation) in observations)
        {
            if (candidates.ContainsKey(key) || observation.MissingSince != 0) continue;
            // At a truncated side's boundary a level may simply have fallen out of view.
            bool observable = Math.Abs((key.Price - mid) / mid) * 100 <= rule.MaxDistance &&
                (key.Side == BookSide.Bid ? !book.BidLimited || key.Price > book.Bids[^1].Price :
                    !book.AskLimited || key.Price < book.Asks[^1].Price);
            bool quick = observable && now - observation.FirstSeen <= QuickDisappearance &&
                (key.Side == BookSide.Bid ? key.Price < book.Asks[0].Price : key.Price > book.Bids[0].Price);
            if (quick)
            {
                if (now - observation.LastFlicker > FlickerWindow) observation.Flickers = 0;
                observation.LastFlicker = now;
                observation.Flickers++;
            }
            observation.MissingSince = now;
        }

        var mirrored = ignoreMirrored ? MirroredLevels(candidates.Values) : [];
        var visible = new List<DensityLevel>();
        foreach (var (key, level) in candidates)
        {
            if (!observations.TryGetValue(key, out var observation))
                observations[key] = observation = new(now);
            else if (observation.MissingSince != 0)
            {
                observation.FirstSeen = now;
                observation.Samples = 0;
                observation.MissingSince = 0;
                // Suppress repeat notifications for the same blinking level.
                if (!ignoreFlicker || now - observation.LastFlicker > FlickerWindow) observation.Published = false;
            }
            observation.Samples++;
            long age = now - observation.FirstSeen;
            bool flickers = ignoreFlicker && observation.Flickers >= 2 && now - observation.LastFlicker <= FlickerWindow;
            if (observation.Samples < 2 || age < rule.MinSeconds * 1000L || flickers || mirrored.Contains(key) ||
                level.Distance < rule.MinDistance || level.Distance > rule.MaxDistance) continue;
            var result = level with { FirstSeen = observation.FirstSeen };
            visible.Add(result);
            if (!observation.Published)
            {
                observation.Published = true;
                appeared(result);
            }
        }
        foreach (var (key, observation) in observations)
            if (observation.MissingSince > 0 && now - observation.MissingSince > FlickerWindow) observations.Remove(key);

        HasCandidates = candidates.Count > 0 || ignoreFlicker &&
            observations.Values.Any(o => o.MissingSince > 0 && now - o.LastFlicker <= QuickDisappearance);
        return visible.ToArray();

        void Collect(BookLevel[] side, BookSide bookSide)
        {
            foreach (var entry in side)
            {
                double distance = Math.Abs((entry.Price - mid) / mid) * 100;
                if (distance > rule.MaxDistance) break;
                double turnover = market.Venue == Venue.Lighter ? entry.Quantity * entry.Price :
                    market.Turnover(entry.Quantity, entry.Price);
                if (!double.IsFinite(turnover) || turnover < rule.SmallUsd) continue;
                var key = new DensityKey(bookSide, entry.Price);
                bool large = turnover >= rule.LargeUsd;
                candidates[key] = new(market, key, turnover, distance, now, now, large,
                    large ? rule.LargeColor : rule.SmallColor);
            }
        }
    }

    private static HashSet<DensityKey> MirroredLevels(IEnumerable<DensityLevel> candidates)
    {
        var bids = candidates.Where(x => x.Key.Side == BookSide.Bid).OrderBy(x => x.Distance).ToArray();
        var asks = candidates.Where(x => x.Key.Side == BookSide.Ask).OrderBy(x => x.Distance).ToArray();
        if (bids.Length < MirrorPairs || asks.Length < MirrorPairs) return [];
        var matches = new HashSet<DensityKey>();
        var used = new bool[asks.Length];
        int pairs = 0, firstAsk = 0;
        foreach (var bid in bids)
        {
            double tolerance = Math.Max(0.015, bid.Distance * 0.15);
            while (firstAsk < asks.Length && asks[firstAsk].Distance < bid.Distance - tolerance) firstAsk++;
            int best = -1;
            double closest = double.PositiveInfinity;
            for (int i = firstAsk; i < asks.Length && asks[i].Distance <= bid.Distance + tolerance; i++)
            {
                double delta = Math.Abs(asks[i].Distance - bid.Distance);
                if (used[i] || delta >= closest || asks[i].Usd < bid.Usd / MirrorRatio || asks[i].Usd > bid.Usd * MirrorRatio)
                    continue;
                best = i;
                closest = delta;
            }
            if (best < 0) continue;
            matches.Add(bid.Key);
            matches.Add(asks[best].Key);
            used[best] = true;
            pairs++;
        }
        return pairs >= MirrorPairs ? matches : [];
    }
}
