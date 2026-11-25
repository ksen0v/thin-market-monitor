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
    public event Action<string, bool>? IndicatorsChanged;
    public event Action<string, bool>? InterestOrderChanged;
    public event Action<string>? PaneWeightsChanged;
    public ChartDrawing[] Drawings(string key) => settings.ChartDrawings.GetValueOrDefault(key) ?? [];
    public ChartIndicatorOptions Options(string key, bool overview)
    {
        if (!overview) return settings.ChartIndicators.GetValueOrDefault(key) ?? ChartIndicatorOptions.Default;
        var options = settings.OverviewIndicators;
        if (options.Interest is not { Count: > 0 } sources || !settings.OverviewInterestContracts.TryGetValue(key, out var contracts))
            return options;
        return options with { Interest = sources.Keys.ToDictionary(venue => venue, venue => contracts.GetValueOrDefault(venue) ?? "") };
    }

    public ChartPaneWeights? PaneWeights(string key) => settings.ChartPaneWeights.GetValueOrDefault(key);

    public void SetDrawings(string key, ChartDrawing[] drawings)
    {
        if (drawings.Length == 0) settings.ChartDrawings.Remove(key);
        else settings.ChartDrawings[key] = drawings;
        save();
        DrawingsChanged?.Invoke(key);
    }

    public void SetOptions(string key, bool overview, ChartIndicatorOptions options, bool orderOnly = false)
    {
        if (overview) SetOverviewOptions(key, options);
        else if (options.Volume && !options.HasDetails && !options.InterestUsd) settings.ChartIndicators.Remove(key);
        else settings.ChartIndicators[key] = options;
        save();
        (orderOnly ? InterestOrderChanged : IndicatorsChanged)?.Invoke(key, overview);
    }

    private void SetOverviewOptions(string key, ChartIndicatorOptions options)
    {
        settings.OverviewIndicators = options.Normalized(keepContracts: false);
        foreach (var (venue, choice) in options.Interest ?? [])
        {
            if (choice.Length == 0) continue;
            if (!settings.OverviewInterestContracts.TryGetValue(key, out var contracts))
                settings.OverviewInterestContracts[key] = contracts = [];
            contracts[venue] = choice;
        }
    }

    public void SetPaneWeights(string key, ChartPaneWeights? weights)
    {
        if (weights == null)
        {
            if (!settings.ChartPaneWeights.Remove(key)) return;
        }
        else settings.ChartPaneWeights[key] = weights;
        save();
        PaneWeightsChanged?.Invoke(key);
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
                market.ListingAsset == chart.ListingAsset &&
                (UsdDenominated(market) || (market.Quote == chart.Quote && !UsdQuotes.Supported(market.Quote))) &&
                ExchangeApi.SupportsOpenInterest(market))
            .DistinctBy(market => market.Key).OrderBy(market => Inverse(market) != Inverse(chart))
            .ThenBy(market => market.Quote != chart.Quote).ThenBy(market => market.Symbol, StringComparer.Ordinal).ToArray();
    }

    internal static Market? ChooseInterestContract(Market chart, Market[] candidates, string choice)
    {
        if (choice.Length > 0) return candidates.FirstOrDefault(market => market.Key == choice);
        var sameKind = candidates.Where(market => Inverse(market) == Inverse(chart)).ToArray();
        var exactQuote = sameKind.Where(market => market.Quote == chart.Quote).ToArray();
        if (exactQuote.Length > 0) return exactQuote.Length == 1 ? exactQuote[0] : null;
        return sameKind.Length == 1 ? sameKind[0] : null;
    }

    private static bool UsdDenominated(Market market) => UsdQuotes.Supported(market.Quote) &&
        market.Identifiers?.TrustedUsdQuote != false;

    private static bool Inverse(Market market) => market.Contract?.Unit == QuantityUnit.Quote ||
        market is { Venue: Venue.Bitget, Contract.Product: "COIN-FUTURES" };

    public static bool ExpectedNetworkError(Exception error) => error is HttpRequestException or System.IO.IOException
        or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException;
}
