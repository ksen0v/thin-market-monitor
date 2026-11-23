using System.Collections.Concurrent;
using System.Net.Http;

namespace ThinMarketMonitor;

internal sealed class ChartFeatures(Settings settings, ExchangeApi api, Func<MarketSource, Catalog?> currentCatalog, Action save)
{
    private sealed class Cache<T>
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long AsOf;
        public T? Value;
    }
    private readonly ConcurrentDictionary<Venue, Cache<Catalog>> catalogs = new();
    private readonly ConcurrentDictionary<string, Cache<Trade[]>> recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Cache<ChartExecution[]>> gateLiquidations = new(StringComparer.Ordinal);
    public BinanceLiquidations Liquidations { get; } = new();
    public System.Windows.Input.ModifierKeys Magnet => settings.ChartMagnet;
    public event Action<string>? DrawingsChanged;
    public event Action<string>? IndicatorsChanged;
    public ChartDrawing[] Drawings(string key) => settings.ChartDrawings.GetValueOrDefault(key) ?? [];
    public ChartIndicatorOptions Options(string key) => settings.ChartIndicators.GetValueOrDefault(key) ?? ChartIndicatorOptions.Default;

    public void SetDrawings(string key, ChartDrawing[] drawings)
    {
        if (drawings.Length == 0) settings.ChartDrawings.Remove(key);
        else settings.ChartDrawings[key] = drawings;
        save();
        DrawingsChanged?.Invoke(key);
    }

    public void SetOptions(string key, ChartIndicatorOptions options)
    {
        if (options.Volume && !options.HasDetails) settings.ChartIndicators.Remove(key);
        else settings.ChartIndicators[key] = options;
        save();
        IndicatorsChanged?.Invoke(key);
    }

    public async Task<Catalog> Futures(Venue venue, CancellationToken ct)
    {
        var source = new MarketSource(venue, Segment.Futures);
        if (currentCatalog(source) is { Error: null } known) return known;
        var cache = catalogs.GetOrAdd(venue, _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            long age = Environment.TickCount64 - cache.AsOf;
            if (cache.Value != null && age < (cache.Value.Error == null ? 900_000 : 60_000)) return cache.Value;
            cache.Value = await api.LoadCatalog(source, ct);
            cache.AsOf = Environment.TickCount64;
            return cache.Value;
        }
        finally { cache.Gate.Release(); }
    }

    public Task<Trade[]> Recent(Market market, CancellationToken ct) =>
        ReadShared(recent, market.Key, 12_000, token => api.RecentTrades(market, token), ct);

    public Task<ChartExecution[]> GateLiquidations(Market market, CancellationToken ct) =>
        ReadShared(gateLiquidations, market.Key, 22_000, token => api.GateExecutedLiquidations(market, token), ct);

    private static async Task<T> ReadShared<T>(ConcurrentDictionary<string, Cache<T>> source, string key, int expiry,
        Func<CancellationToken, Task<T>> read, CancellationToken ct) where T : class
    {
        long now = Environment.TickCount64;
        if (source.Count > 64)
            foreach (var item in source.Where(item => now - Volatile.Read(ref item.Value.AsOf) > 120_000))
                source.TryRemove(item);
        var cache = source.GetOrAdd(key, _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (cache.Value != null && Environment.TickCount64 - cache.AsOf < expiry) return cache.Value;
            cache.Value = await read(ct);
            Volatile.Write(ref cache.AsOf, Environment.TickCount64);
            return cache.Value;
        }
        finally { cache.Gate.Release(); }
    }

    public static Market[] ComparableContracts(Market chart, Venue venue, IEnumerable<Market> catalog)
    {
        if (chart.Contract != null && chart.Venue == venue)
            return ExchangeApi.SupportsOpenInterest(chart) ? [chart] : [];
        if (chart.Contract is { Perpetual: false }) return [];
        return catalog.Where(market => market.Venue == venue && market.Contract is { Perpetual: true } &&
                market.ListingAsset == chart.ListingAsset && market.Quote == chart.Quote &&
                Inverse(market) == Inverse(chart) && ExchangeApi.SupportsOpenInterest(market))
            .DistinctBy(market => market.Key).OrderBy(market => market.Symbol, StringComparer.Ordinal).ToArray();
    }

    private static bool Inverse(Market market) => market.Contract?.Unit == QuantityUnit.Quote ||
        market is { Venue: Venue.Bitget, Contract.Product: "COIN-FUTURES" };

    public static bool ExpectedNetworkError(Exception error) => error is HttpRequestException or System.IO.IOException
        or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException;
}
