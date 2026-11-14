namespace ThinMarketMonitor;

internal sealed class LiveCandles(int frame)
{
    public const int MaxBars = 12_000;
    private readonly long interval = frame * 1000L;
    private readonly SortedDictionary<long, Bar> bars = new();

    public int Count => bars.Count;

    public void Add(Trade trade)
    {
        if (trade.Time <= 0 || trade.Price <= 0 || !double.IsFinite(trade.Price)) return;
        long time = trade.Time / interval * interval;
        var bar = GetBar(time);
        if (!bar.Closed) bar.Add(trade);
        Trim();
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
        }
        Trim();
    }

    public Candle[] Snapshot() => bars.Select(entry => entry.Value.CandleAt(entry.Key)).ToArray();

    private Bar GetBar(long time)
    {
        if (!bars.TryGetValue(time, out var bar)) bars[time] = bar = new();
        return bar;
    }

    private void Trim()
    {
        while (bars.Count > MaxBars) bars.Remove(bars.First().Key);
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

        public void Add(Trade trade)
        {
            if (first == null || trade.Time < first.Time) first = trade;
            if (last == null || trade.Time >= last.Time) last = trade;
            high = Math.Max(high, trade.Price);
            low = Math.Min(low, trade.Price);
        }

        public Candle CandleAt(long time)
        {
            if (first == null || Closed) return History!;
            var latest = last!;
            double close = History != null && latest.Time < HistoryRequestedAt - 2000 ? History.Close : latest.Price;
            return new(time, History?.Open ?? first.Price, Math.Max(History?.High ?? 0, high),
                Math.Min(History?.Low ?? double.PositiveInfinity, low), close, History?.QuoteVolume);
        }
    }
}
