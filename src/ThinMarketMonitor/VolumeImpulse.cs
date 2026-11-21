namespace ThinMarketMonitor;

internal static class UsdQuotes
{
    public static bool Supported(string quote) => quote is "USD" or "USDT" or "USDC" or "FDUSD" or "TUSD" or "USDP" or "PYUSD" or "USD1";
    public static bool Supported(Market market) => Supported(market.Quote) &&
        market.Identifiers?.TrustedUsdQuote != false && (market.Contract?.Multiplier ?? 1) > 0;
}

internal enum VolumePhase { Waiting, ZeroBaseline, Ready }
internal readonly record struct VolumeReading(VolumePhase Phase, double? Current = null, double? Average = null, double? Growth = null);

internal sealed class VolumeImpulse
{
    internal const int SettlementSeconds = 6;
    internal const int MaxRecentIds = 65_536;
    private readonly object gate = new();
    private readonly Threshold[] rules;
    private readonly int referenceSeconds;
    private readonly int shortestWindow;
    private readonly RollingVolume volume;
    private readonly bool[] latched;
    private readonly HashSet<string> recentIds = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, long> idTimes = new();
    private long coveredSince = long.MaxValue;
    private long lastAlert;
    private long evaluatedSecond = -1;

    public VolumeImpulse(Threshold[] rules, int referenceSeconds = 3600)
    {
        this.rules = rules;
        this.referenceSeconds = referenceSeconds;
        shortestWindow = rules.Where(r => r.Enabled).Select(r => r.Seconds).DefaultIfEmpty(15).Min();
        volume = new(referenceSeconds + rules.Select(r => r.Seconds).DefaultIfEmpty(15).Max());
        latched = new bool[rules.Length];
    }

    public void ResetCoverage(long? since)
    {
        lock (gate)
        {
            volume.Clear();
            Array.Clear(latched);
            recentIds.Clear();
            idTimes.Clear();
            evaluatedSecond = -1;
            coveredSince = since ?? long.MaxValue;
        }
    }

    public void Observe(Market market, Trade trade, long now)
    {
        lock (gate)
        {
            if (rules.Length == 0 || !UsdQuotes.Supported(market) || coveredSince == long.MaxValue) return;
            // Replayed trades from before the current subscription cannot fill a coverage gap.
            if (trade.Time < coveredSince) return;
            if (!string.IsNullOrEmpty(trade.Id) && recentIds.Contains(trade.Id)) return;
            double amount = market.Turnover(trade.Size, trade.Price);
            if (!double.IsFinite(amount) || amount <= 0 || !double.IsFinite(trade.Price) || trade.Price <= 0 ||
                string.IsNullOrEmpty(trade.Id) || trade.Time < now - 5000 || trade.Time > now + 3000)
            {
                ResetCoverage(Math.Max(now, coveredSince));
                return;
            }
            while (idTimes.TryPeek(out var id, out long time) && time < now - 5000)
            {
                idTimes.Dequeue();
                recentIds.Remove(id);
            }
            if (recentIds.Contains(trade.Id)) return;
            // Bound memory without dropping IDs and silently double-counting a busy market.
            if (recentIds.Count >= MaxRecentIds)
            {
                ResetCoverage(Math.Max(now, coveredSince));
                return;
            }
            recentIds.Add(trade.Id);
            idTimes.Enqueue(trade.Id, trade.Time);
            // Bucket k contains (k*1000, (k+1)*1000], preserving both window boundaries.
            volume.Add((trade.Time - 1) / 1000, amount);
            if (!volume.Finite) ResetCoverage(Math.Max(now, coveredSince));
        }
    }

    public void ProcessingGap(long now)
    {
        lock (gate)
            if (coveredSince != long.MaxValue) ResetCoverage(Math.Max(now, coveredSince));
    }

    public VolumeReading Reading(int seconds, long now)
    {
        lock (gate) return Read(seconds, now / 1000 - SettlementSeconds);
    }

    public VolumePhase Readiness(long now) => Reading(shortestWindow, now).Phase;
    public bool HasCoverage(long now) => Readiness(now) != VolumePhase.Waiting;

    private VolumeReading Read(int seconds, long end)
    {
        if ((end - seconds - referenceSeconds) * 1000 < coveredSince) return new(VolumePhase.Waiting);
        double current = volume.Sum(end, seconds);
        double prior = volume.Sum(end - seconds, referenceSeconds);
        double average = prior * ((double)seconds / referenceSeconds);
        if (!double.IsFinite(current) || !double.IsFinite(average)) return new(VolumePhase.Waiting);
        if (prior == 0) return new(VolumePhase.ZeroBaseline, current, 0);
        if (average <= 0) return new(VolumePhase.Waiting);
        double growth = (current / average - 1) * 100;
        return double.IsFinite(growth) ? new(VolumePhase.Ready, current, average, growth) : new(VolumePhase.Waiting);
    }

    public Signal? Evaluate(Market market, long now, bool eligible)
    {
        lock (gate)
        {
            long end = now / 1000 - SettlementSeconds;
            if (end <= evaluatedSecond || !UsdQuotes.Supported(market)) return null;
            evaluatedSecond = end;
            Signal? best = null;
            for (int i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                if (!rule.Enabled) continue;
                var reading = Read(rule.Seconds, end);
                if (reading.Phase == VolumePhase.ZeroBaseline) latched[i] = false;
                if (reading.Phase != VolumePhase.Ready || reading.Growth is not { } growth ||
                    reading.Current is not { } current || reading.Average is not { } average) continue;
                if (growth < rule.Percent * 0.75) latched[i] = false;
                if (!eligible || growth < rule.Percent || latched[i]) continue;
                latched[i] = true;
                best ??= new(market, end * 1000, rule.Seconds, growth, SignalModule.Volume,
                    current, average, referenceSeconds);
            }
            if (best == null || lastAlert != 0 && best.Time - lastAlert < 30_000) return null;
            lastAlert = best.Time;
            return best;
        }
    }
}

internal sealed class RollingVolume(int maxSeconds)
{
    private const int LateSeconds = 8;
    private readonly int capacity = maxSeconds + LateSeconds + 2;
    private double[]? sums;
    private int leaves;
    private long newest = -1;
    internal bool Finite => sums == null || double.IsFinite(sums[1]);
    internal int AllocatedBytes => (sums?.Length ?? 0) * sizeof(double);

    public void Clear()
    {
        if (sums != null) Array.Clear(sums);
        newest = -1;
    }

    public void Add(long second, double amount)
    {
        if (second < 0 || (newest >= 0 && second < newest - LateSeconds)) return;
        if (sums == null)
        {
            leaves = 1;
            while (leaves < capacity) leaves *= 2;
            sums = new double[leaves * 2];
        }
        if (newest >= 0 && second > newest)
        {
            long gap = second - newest;
            if (gap >= capacity) Array.Clear(sums);
            else
                for (long expired = newest + 1; expired <= second; expired++)
                    Set((int)(expired % capacity), 0);
        }
        newest = Math.Max(newest, second);
        int slot = (int)(second % capacity);
        Set(slot, sums[leaves + slot] + amount);
    }

    public double Sum(long endExclusive, int seconds)
    {
        if (sums == null || newest < 0 || endExclusive < 0) return 0;
        long first = Math.Max(endExclusive - seconds, Math.Max(0, newest - capacity + 1));
        long end = Math.Min(endExclusive, newest + 1);
        if (end <= first) return 0;
        int left = (int)(first % capacity), right = (int)(end % capacity);
        return left < right ? Range(left, right) : Range(left, capacity) + Range(0, right);
    }

    private void Set(int slot, double value)
    {
        int node = leaves + slot;
        sums![node] = value;
        while ((node /= 2) > 0) sums[node] = sums[node * 2] + sums[node * 2 + 1];
    }

    private double Range(int first, int end)
    {
        double total = 0;
        for (int left = first + leaves, right = end + leaves; left < right; left /= 2, right /= 2)
        {
            if ((left & 1) != 0) total += sums![left++];
            if ((right & 1) != 0) total += sums![--right];
        }
        return total;
    }
}
