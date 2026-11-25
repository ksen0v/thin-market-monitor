namespace ThinMarketMonitor;

internal sealed class LiveCandles(int frame, long offset = 0, bool volumeInBase = false)
{
    public const int MaxBars = 12_000;
    private readonly long interval = frame * 1000L;
    private readonly SortedDictionary<long, Bar> bars = new();
    private readonly HashSet<long> changed = [];
    private Candle[] display = [];
    private bool structureChanged;
    private HashSet<string>? volumeIds;
    private Queue<string>? volumeIdOrder;

    public int Count => bars.Count;

    public void Add(Trade trade, double volume = double.NaN)
    {
        if (trade.Time <= 0 || trade.Price <= 0 || !double.IsFinite(trade.Price)) return;
        long time = ExchangeApi.CandleStart(trade.Time, interval, offset);
        var bar = GetBar(time);
        if (!bar.Closed)
        {
            bar.Add(trade, NewVolume(trade.Id, volume));
            changed.Add(time);
        }
        Trim();
    }

    private double NewVolume(string? id, double volume)
    {
        if (id is not { Length: > 0 } || !double.IsFinite(volume) || volume <= 0) return volume;
        if (!(volumeIds ??= new(StringComparer.Ordinal)).Add(id)) return double.NaN;
        (volumeIdOrder ??= new()).Enqueue(id);
        if (volumeIdOrder.Count > 16_384) volumeIds.Remove(volumeIdOrder.Dequeue());
        return volume;
    }

    public void AddHistory(IEnumerable<Candle> candles, long requestedAt)
    {
        foreach (var candle in candles)
        {
            var bar = GetBar(candle.Time);
            bar.History = candle;
            bar.HistoryRequestedAt = requestedAt;
            // Give REST time to publish the final bar. An earlier response may still describe an open bar.
            bar.Closed = candle.Time + interval + 2000 <= requestedAt;
            changed.Add(candle.Time);
        }
        Trim();
    }

    public Candle[] Snapshot() => (Candle[])DisplaySnapshot().Clone();

    // Chart loading, updates and drawing all run on the same dispatcher.
    internal Candle[] DisplaySnapshot()
    {
        if (structureChanged)
        {
            display = new Candle[bars.Count];
            int index = 0;
            foreach (var (time, bar) in bars) display[index++] = bar.CandleAt(time, volumeInBase);
            structureChanged = false;
        }
        else
        {
            foreach (long time in changed)
            {
                int first = 0, last = display.Length - 1;
                while (first <= last)
                {
                    int middle = first + (last - first) / 2;
                    long candidate = display[middle].Time;
                    if (candidate < time) first = middle + 1;
                    else if (candidate > time) last = middle - 1;
                    else
                    {
                        display[middle] = bars[time].CandleAt(time, volumeInBase);
                        break;
                    }
                }
            }
        }
        changed.Clear();
        return display;
    }

    private Bar GetBar(long time)
    {
        if (!bars.TryGetValue(time, out var bar))
        {
            bars[time] = bar = new();
            structureChanged = true;
        }
        return bar;
    }

    private void Trim()
    {
        while (bars.Count > MaxBars)
        {
            bars.Remove(bars.First().Key);
            structureChanged = true;
        }
    }

    private sealed class Bar
    {
        public Candle? History { get; set; }
        public long HistoryRequestedAt { get; set; }
        public bool Closed { get; set; }
        private Trade? first;
        private Trade? last;
        private double high;
        private double low = double.PositiveInfinity;
        private double observed;

        public void Add(Trade trade, double volume)
        {
            if (first == null || trade.Time < first.Time) first = trade;
            if (last == null || trade.Time >= last.Time) last = trade;
            high = Math.Max(high, trade.Price);
            low = Math.Min(low, trade.Price);
            if (double.IsFinite(volume) && volume > 0) observed += volume;
        }

        public Candle CandleAt(long time, bool volumeInBase)
        {
            if (first == null || Closed) return History!;
            var latest = last!;
            double close = History != null && latest.Time < HistoryRequestedAt - 2000 ? History.Close : latest.Price;
            double? quote = History?.QuoteVolume, basis = History?.BaseVolume;
            // REST and live trades can describe the same executions. Use one total, never add overlapping totals.
            bool observedVolume = double.IsFinite(observed) && observed > (volumeInBase ? basis : quote).GetValueOrDefault();
            if (observedVolume && volumeInBase) basis = observed;
            else if (observedVolume) quote = observed;
            return new(time, History?.Open ?? first.Price, Math.Max(History?.High ?? 0, high),
                Math.Min(History?.Low ?? double.PositiveInfinity, low), close, quote, basis, observedVolume);
        }
    }
}
