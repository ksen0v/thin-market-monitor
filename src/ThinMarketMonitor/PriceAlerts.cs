using System.Globalization;

namespace ThinMarketMonitor;

internal sealed record PriceAlert(Guid Id, string MarketKey, MarketSource Source, string Ticker, string Quote, double Price,
    long ArmedAt, bool Enabled = true, double? ReferencePrice = null, long? TriggeredAt = null, double? ReachedPrice = null)
{
    internal bool Active => Enabled && TriggeredAt == null;
    internal bool Valid => Id != Guid.Empty && !string.IsNullOrWhiteSpace(MarketKey) && MarketSource.All.Contains(Source) &&
        !string.IsNullOrWhiteSpace(Ticker) && !string.IsNullOrWhiteSpace(Quote) && ArmedAt > 0 && IsPrice(Price) &&
        (ReferencePrice == null || IsPrice(ReferencePrice.Value)) &&
        (TriggeredAt == null ? ReachedPrice == null : TriggeredAt >= ArmedAt && ReachedPrice is { } reached && IsPrice(reached));

    internal static bool IsPrice(double price) => double.IsFinite(price) && price > 0;
    internal static string Format(double price) => price.ToString("G12", CultureInfo.GetCultureInfo("ru-RU"));
}

internal sealed record PriceAlertHit(PriceAlert Alert, Market Market);

internal sealed class PriceAlertBook
{
    private sealed class TradeCursor
    {
        internal long Time;
        internal readonly HashSet<string> Ids = new(StringComparer.Ordinal);

        internal bool Accept(Trade trade)
        {
            if (trade.Time < Time) return false;
            if (trade.Time != Time)
            {
                Time = trade.Time;
                Ids.Clear();
            }
            return string.IsNullOrEmpty(trade.Id) || Ids.Add(trade.Id);
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<Guid, PriceAlert> levels;
    private readonly Dictionary<string, TradeCursor> cursors = new(StringComparer.Ordinal);
    private readonly List<PriceAlertHit> hits = [];
    private volatile IReadOnlyDictionary<string, Guid[]> watched = new Dictionary<string, Guid[]>(StringComparer.Ordinal);
    private bool changed;

    internal PriceAlertBook(IEnumerable<PriceAlert> saved)
    {
        levels = saved.ToDictionary(alert => alert.Id);
        Index();
    }

    internal bool Watches(string key) => watched.ContainsKey(key);

    internal bool Add(Market market, double price, double? reference, long now)
    {
        if (!PriceAlert.IsPrice(price)) return false;
        lock (gate)
        {
            if (levels.Values.Any(alert => alert.Active && alert.MarketKey == market.Key && alert.Price == price)) return false;
            var id = Guid.NewGuid();
            levels.Add(id, new(id, market.Key, market.Source, market.Ticker, market.Quote, price, now,
                ReferencePrice: reference is { } value && PriceAlert.IsPrice(value) ? value : null));
            changed = true;
            Index();
            return true;
        }
    }

    internal void SetEnabled(Guid id, bool enabled, long now)
    {
        lock (gate)
        {
            if (!levels.TryGetValue(id, out var alert) || enabled == alert.Active) return;
            if (enabled && levels.Values.Any(other => other.Active && other.MarketKey == alert.MarketKey && other.Price == alert.Price))
                return;
            levels[id] = enabled
                ? alert with { Enabled = true, ArmedAt = now, TriggeredAt = null, ReachedPrice = null, ReferencePrice = null }
                : alert with { Enabled = false };
            changed = true;
            Index();
        }
    }

    internal void Remove(Guid id)
    {
        lock (gate)
        {
            if (!levels.Remove(id)) return;
            changed = true;
            Index();
        }
    }

    internal void Observe(Market market, Trade trade, long now)
    {
        if (watched.Count == 0) return;
        string key = market.Key;
        if (!Watches(key) || !PriceAlert.IsPrice(trade.Price) || trade.Time < now - 30_000 ||
            trade.Time > now + 3000 || !string.Equals(trade.Symbol, market.Symbol, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (!watched.TryGetValue(key, out var matching)) return;
            if (!cursors.TryGetValue(key, out var cursor)) cursors[key] = cursor = new();
            if (!cursor.Accept(trade)) return;
            bool reached = false;
            foreach (var id in matching)
            {
                var alert = levels[id];
                if (trade.Time < alert.ArmedAt) continue;
                if (trade.Price == alert.Price ||
                    alert.ReferencePrice is { } reference && (reference < alert.Price && trade.Price > alert.Price ||
                        reference > alert.Price && trade.Price < alert.Price))
                {
                    var hit = alert with { Enabled = false, TriggeredAt = trade.Time, ReachedPrice = trade.Price };
                    levels[id] = hit;
                    hits.Add(new(hit, market));
                    reached = changed = true;
                }
                else if (alert.ReferencePrice == null || alert.ReferencePrice == alert.Price)
                {
                    levels[id] = alert with { ReferencePrice = trade.Price };
                    changed = true;
                }
            }
            if (reached) Index();
        }
    }

    internal (PriceAlert[] Levels, PriceAlertHit[] Hits)? Drain()
    {
        lock (gate)
        {
            if (!changed) return null;
            changed = false;
            var batch = (levels.Values.ToArray(), hits.Where(hit => levels.GetValueOrDefault(hit.Alert.Id) == hit.Alert).ToArray());
            hits.Clear();
            return batch;
        }
    }

    private void Index()
    {
        watched = levels.Values.Where(alert => alert.Active).GroupBy(alert => alert.MarketKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(alert => alert.Id).ToArray(), StringComparer.Ordinal);
        foreach (string key in cursors.Keys.Where(key => !watched.ContainsKey(key)).ToArray()) cursors.Remove(key);
    }
}
