namespace ThinMarketMonitor;

internal static class UsdQuotes
{
    public static bool Supported(string quote) => quote is "USD" or "USDT" or "USDC" or "FDUSD" or "TUSD" or "USDP" or "PYUSD" or "USD1";
    public static bool Supported(Market market) => Supported(market.Quote) && (market.Contract?.Multiplier ?? 1) > 0;
}

internal sealed class VolumeImpulse(VolumeRule[] rules)
{
    private readonly RollingVolume volume = new(rules.Select(r => r.Seconds).DefaultIfEmpty(15).Max());
    private readonly long warmup = (rules.Select(r => r.Seconds).DefaultIfEmpty(15).Min() + 1L) * 1000;
    private readonly bool[] latched = new bool[rules.Length];
    private readonly HashSet<string> recentIds = new(StringComparer.Ordinal);
    private readonly Queue<string> idOrder = new();
    private long coveredSince = long.MaxValue;
    private long lastAlert;
    private long evaluatedSecond = -1;

    public void ResetCoverage(long? since)
    {
        volume.Clear();
        Array.Clear(latched);
        evaluatedSecond = -1;
        Volatile.Write(ref coveredSince, since ?? long.MaxValue);
    }

    public bool HasCoverage(long now) => Volatile.Read(ref coveredSince) <= now - warmup;

    public Signal? Observe(Market market, Trade trade, long now, bool eligible)
    {
        long since = coveredSince;
        if (rules.Length == 0 || !UsdQuotes.Supported(market) || trade.Time < since)
            return null;
        double amount = market.Turnover(trade.Size, trade.Price);
        if (!double.IsFinite(amount) || amount <= 0 || string.IsNullOrEmpty(trade.Id) ||
            trade.Time < now - 5000 || trade.Time > now + 3000)
        {
            // An uncountable trade makes this connection's current window incomplete.
            ResetCoverage(Math.Max(now, since));
            return null;
        }

        if (!recentIds.Add(trade.Id)) return null;
        idOrder.Enqueue(trade.Id);
        if (idOrder.Count > 2048) recentIds.Remove(idOrder.Dequeue());
        long second = trade.Time / 1000;
        volume.Add(second, amount);
        long end = second - 1;
        if (!eligible || end <= evaluatedSecond) return null;

        evaluatedSecond = end;
        Signal? best = null;
        for (int i = 0; i < rules.Length; i++)
        {
            var rule = rules[i];
            if (!rule.Enabled || (end - rule.Seconds) * 1000 < since) continue;
            // Leave a one-second buffer for trades delivered out of order.
            double sum = volume.Sum(end, rule.Seconds);
            if (sum < rule.Usd * 0.75) latched[i] = false;
            if (sum < rule.Usd || latched[i]) continue;
            latched[i] = true;
            best ??= new(market, trade.Time, rule.Seconds, sum, SignalModule.Volume);
        }

        if (best == null || (lastAlert != 0 && trade.Time - lastAlert < 30_000)) return null;
        lastAlert = trade.Time;
        return best;
    }
}

internal sealed class RollingVolume(int maxSeconds)
{
    private const int LateSeconds = 8;
    private readonly int capacity = maxSeconds + LateSeconds + 2;
    private double[]? sums;
    private int leaves;
    private long newest = -1;

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
