namespace ThinMarketMonitor;

internal sealed class SignalCooldown(TimeProvider clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(Settings.MaxSignalCooldownMinutes);
    private readonly Dictionary<string, long> lastAccepted = new(StringComparer.Ordinal);
    private long? lastPruned;

    // Only called on the UI dispatcher, after the existing signal filters.
    public bool TryAccept(Market market, int minutes)
    {
        long now = clock.GetTimestamp();
        if (lastPruned == null || clock.GetElapsedTime(lastPruned.Value, now) >= TimeSpan.FromMinutes(1))
        {
            foreach (var entry in lastAccepted.Where(item => clock.GetElapsedTime(item.Value, now) >= Retention).ToArray())
                lastAccepted.Remove(entry.Key);
            lastPruned = now;
        }

        string asset = market.ListingAsset;
        if (minutes > 0 && lastAccepted.TryGetValue(asset, out long previous) &&
            clock.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(minutes)) return false;
        lastAccepted[asset] = now;
        return true;
    }
}
