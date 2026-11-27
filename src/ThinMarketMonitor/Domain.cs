using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ThinMarketMonitor;

internal enum Venue { Binance, Bybit, Okx, Bitget, Mexc, KuCoin, Gate, Alpha, Htx, Kraken, Weex, Lighter, Hyperliquid }
internal enum Segment { Spot, Futures }
internal readonly record struct MarketSource(Venue Venue, Segment Segment = Segment.Spot)
{
    public static MarketSource[] All { get; } = Enum.GetValues<Venue>().SelectMany(v =>
        v == Venue.Alpha ? new[] { new MarketSource(v) } : [new(v), new(v, Segment.Futures)]).ToArray();
    public string Label => Venue == Venue.Alpha ? Venue.Label() : $"{Venue.Label()} · {Segment}";
    public override string ToString() => Label;
}
internal enum QuantityUnit { Base, Quote }
internal sealed record Contract(string Product, double Multiplier = 1, QuantityUnit Unit = QuantityUnit.Base, bool Streaming = true,
    bool Perpetual = false, string? NativeBase = null);
internal enum ListingRule { Ignore, Require, Exclude }
internal static class Venues
{
    public static string Label(this Venue venue) => venue switch
    {
        Venue.Okx => "OKX", Venue.Bitget => "Bitget", Venue.Mexc => "MEXC",
        Venue.Alpha => "Binance Alpha", Venue.Htx => "HTX", Venue.Weex => "WEEX", _ => venue.ToString()
    };
    public static bool Extended(this Venue venue) => venue is Venue.Htx or Venue.Kraken or Venue.Weex or Venue.Lighter or Venue.Hyperliquid;
}

internal sealed record MarketIdentifiers(string? Rest = null, string? Stream = null, string? ListingAsset = null,
    string? Collateral = null, string? Display = null, bool TrustedUsdQuote = true, bool LeveragedToken = false,
    string? ColibriSymbol = null, bool StockOrFund = false);

internal record Market(Venue Venue, string Symbol, string Base, string Quote, Contract? Contract = null, MarketIdentifiers? Identifiers = null)
{
    public MarketSource Source => new(Venue, Contract == null ? Segment.Spot : Segment.Futures);
    public string Key => Contract == null ? $"{Venue}:{Symbol}" : $"{Venue}:Futures:{Contract.Product}:{Symbol}";
    public string Ticker => Identifiers?.Display ?? (Contract == null ? $"{Base}/{Quote}" : Symbol);
    public string ListingAsset => Identifiers?.ListingAsset ?? Base;
    public string RestSymbol => Identifiers?.Rest ?? Symbol;
    public string StreamSymbol => Identifiers?.Stream ?? Symbol;
    public bool IsLeveragedToken => Contract == null && Identifiers?.LeveragedToken == true;
    public bool IsStockOrFund => Identifiers?.StockOrFund == true || ListingAsset.StartsWith("equity:", StringComparison.Ordinal);
    public string Label => $"{Ticker} · {Source.Label}" + (Venue == Venue.Alpha ? $" · {Symbol}" : "");
    public bool Streaming => Contract?.Streaming != false;
    public double Turnover(double quantity, double price) => Contract?.Unit == QuantityUnit.Quote
        ? quantity * Contract.Multiplier : price * quantity * (Contract?.Multiplier ?? 1);
}

internal record Candle(long Time, double Open, double High, double Low, double Close, double? QuoteVolume = null,
    double? BaseVolume = null, bool ObservedVolume = false);
internal record Trade(string Symbol, long Time, double Price, double Size = 0, string? Id = null, bool Liquidated = false);
internal record Threshold(int Seconds, bool Enabled, double Percent);
internal record VolumeRule(int Seconds, bool Enabled, double Usd);
internal enum SignalModule { Price, Volume }
internal enum SignalTone { System, Bell, Double, Soft }
internal record Signal(Market Market, long Time, int Seconds, double Value, SignalModule Module = SignalModule.Price,
    double? CurrentTurnover = null, double? AverageTurnover = null, int? ReferenceSeconds = null, double? LastPrice = null);
internal record ChartGesture(Key Key, MouseButton? Mouse = null)
{
    public string Label => Mouse switch
    {
        MouseButton.Left => "ЛКМ", MouseButton.Middle => "Средняя кнопка", MouseButton.Right => "ПКМ",
        MouseButton.XButton1 => "Кнопка мыши 4", MouseButton.XButton2 => "Кнопка мыши 5", _ => Key.ToString()
    };

    public static bool Bindable(Key key) => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
        or >= Key.F1 and <= Key.F24 or >= Key.NumPad0 and <= Key.NumPad9 or Key.Space;
}
internal record Catalog(IReadOnlyList<Market> Markets, string? Error = null, bool ConnectionFailed = false);
internal record CellChoice(string Key, bool Pinned, long Order = 0, int Frame = 60);
internal record FilterResult(IReadOnlyList<Market> Markets, IReadOnlyList<MarketSource> Missing);
internal static class Frames
{
    public static readonly int[] All = [1, 5, 15, 60, 300, 900, 3600, 14400, 86400];
    public static string Label(int s) => s >= 86400 ? "1д" : s >= 3600 ? $"{s / 3600}ч" : s >= 60 ? $"{s / 60}м" : $"{s}с";
}
internal enum MetricKind { Turnover, Change, Atr, Spread }
internal record OverviewColumn(MetricKind Kind, int Minutes)
{
    internal string WidthKey => FormattableString.Invariant($"{Kind}:{Minutes}");
}
internal enum BookmarkColor { Gold, Green, Blue, Pink, Violet }

internal sealed class Settings
{
    private const int EditableSignalPeriodsVersion = 1;
    private const int DefaultChartVolumeVersion = 2;
    internal const int MaxModuleSeconds = 3600;
    internal const int MaxFilterSeconds = 86400;
    internal const int MaxRules = 24;
    internal const int MaxVolumeReferenceSeconds = 3600;
    internal const int MaxSignalCooldownMinutes = 1440;
    internal static readonly Threshold[] Defaults = [new(15, true, 4), new(60, true, 5), new(300, true, 12), new(900, true, 20)];
    internal static readonly Threshold[] DefaultVolumeGrowth = [new(15, true, 100), new(60, true, 100), new(300, true, 100), new(900, true, 100)];
    public int SchemaVersion { get; set; } = DefaultChartVolumeVersion;
    public bool DebugMode { get; set; }
    public Dictionary<Venue, ListingRule> Rules { get; set; } = new() { [Venue.KuCoin] = ListingRule.Require };
    public Dictionary<Venue, ListingRule> FuturesRules { get; set; } = [];
    public ListingRule Rule(MarketSource source) => ListingRules(source.Segment).GetValueOrDefault(source.Venue);
    public int[] FrameButtons { get; set; } = [15, 60, 300];
    public Key FrameUp { get; set; } = Key.Up;
    public Key FrameDown { get; set; } = Key.Down;
    public bool Overview { get; set; }
    public bool DensityWorkspace { get; set; }
    public DensityOptions Densities { get; set; } = new();
    public bool OverviewDrawer { get; set; } = true;
    public double OverviewDrawerWidth { get; set; } = 390;
    public MarketSource OverviewSource { get; set; } = new(Venue.KuCoin);
    public MarketSource[]? OverviewSources { get; set; }
    public HashSet<Venue> OverviewDisabledVenues { get; set; } = [];
    public Dictionary<Venue, ListingRule> OverviewSpotRules { get; set; } = [];
    public Dictionary<Venue, ListingRule> OverviewFuturesRules { get; set; } = [];
    public ListingRule OverviewRule(MarketSource source) => ListingRules(source.Segment, overview: true).GetValueOrDefault(source.Venue);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? OverviewQuotes { get; set; }
    public bool HideOverviewLeveragedTokens { get; set; }
    public bool HideSignalLeveragedTokens { get; set; }
    public bool ShowsOverviewQuote(string quote) => OverviewQuotes == null || OverviewQuotes.Contains(quote, StringComparer.Ordinal);
    public bool ShowsOverviewMarket(Market market) => ShowsOverviewQuote(market.Quote) &&
        (!HideOverviewLeveragedTokens || !market.IsLeveragedToken);
    public CellChoice? OverviewChoice { get; set; }
    private MarketSource[] ConfiguredOverviewSources() => OverviewSources ??
        MarketSource.All.Where(source => source == OverviewSource || OverviewRule(source) == ListingRule.Require).ToArray();

    internal bool ScansOverview(MarketSource source) => !OverviewDisabledVenues.Contains(source.Venue) &&
        (OverviewSources?.Contains(source) ?? (source == OverviewSource || OverviewRule(source) == ListingRule.Require));

    internal MarketSource[] EnabledOverviewSources() => ConfiguredOverviewSources()
        .Where(source => !OverviewDisabledVenues.Contains(source.Venue)).ToArray();

    internal void ToggleOverviewVenue(Venue venue)
    {
        var configured = ConfiguredOverviewSources();
        if (!configured.Any(source => source.Venue == venue))
        {
            var preferred = new MarketSource(venue, OverviewSource.Segment);
            OverviewSources = [.. configured, MarketSource.All.Contains(preferred) ? preferred : new(venue)];
            OverviewDisabledVenues.Remove(venue);
        }
        else if (!OverviewDisabledVenues.Remove(venue)) OverviewDisabledVenues.Add(venue);
    }

    internal void ToggleOverviewSegment(MarketSource source)
    {
        var configured = ConfiguredOverviewSources();
        if (OverviewDisabledVenues.Remove(source.Venue))
            configured = configured.Where(item => item.Venue != source.Venue).ToArray();
        OverviewSources = configured.Contains(source) ? configured.Where(item => item != source).ToArray() : [.. configured, source];
    }
    public int OverviewFrame { get; set; } = 60;
    public OverviewColumn[] OverviewColumns { get; set; } = [new(MetricKind.Turnover, 1440), new(MetricKind.Change, 1440)];
    public Dictionary<string, double> OverviewColumnWidths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, BookmarkColor> OverviewBookmarks { get; set; } = new(StringComparer.Ordinal);
    public BookmarkColor? OverviewBookmarkPriority { get; set; }
    public int OverviewSort { get; set; } = -1;
    public bool OverviewDescending { get; set; }
    public Threshold[] Thresholds { get; set; } = Defaults.ToArray();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VolumeRule[]? VolumeThresholds { get; set; }
    public Threshold[] VolumeGrowthThresholds { get; set; } = DefaultVolumeGrowth.ToArray();
    public int VolumeReferenceSeconds { get; set; } = 3600;
    public bool VolumePercentMigrationPending { get; set; }
    public VolumeRule[] VolumeFilters { get; set; } = [];
    public SpreadRule SpreadFilter { get; set; } = new();
    public string[] SignalBlacklist { get; set; } = [];
    public bool SignalBlocked(Market market) => SignalBlacklist.Contains(CoinKey(market.Base), StringComparer.Ordinal);
    public bool ShowsSignalMarket(Market market) => !SignalBlocked(market) &&
        (!HideSignalLeveragedTokens || !market.IsLeveragedToken);
    internal static string CoinKey(string coin) => coin.Trim().ToUpperInvariant();
    public bool BlockSignals(Market market)
    {
        string coin = CoinKey(market.Base);
        if (coin.Length == 0 || SignalBlacklist.Contains(coin, StringComparer.Ordinal)) return false;
        SignalBlacklist = SignalBlacklist.Append(coin).Order(StringComparer.Ordinal).ToArray();
        return true;
    }
    public bool PriceModule { get; set; } = true;
    public bool VolumeModule { get; set; }
    public int SignalCooldownMinutes { get; set; }
    public bool SignalPopup { get; set; }
    public bool SignalSound { get; set; }
    public bool SignalVoice { get; set; }
    public SignalTone PriceSignalTone { get; set; }
    public SignalTone VolumeSignalTone { get; set; }
    public SignalTone DensityTone { get; set; }
    public SoundMix Audio { get; set; } = new();
    public PriceAlert[] PriceAlerts { get; set; } = [];
    public bool PriceAlertPopup { get; set; } = true;
    public bool PriceAlertSound { get; set; }
    public SignalTone PriceAlertTone { get; set; }
    public SignalSpeechOptions PriceSpeech { get; set; } = SignalSpeechOptions.Default;
    public SignalSpeechOptions VolumeSpeech { get; set; } = SignalSpeechOptions.Default;
    internal SignalSpeechOptions SpeechFor(SignalModule module) => module == SignalModule.Volume ? VolumeSpeech : PriceSpeech;

    internal void SetSpeech(SignalModule module, SignalSpeechOptions options)
    {
        if (module == SignalModule.Volume) VolumeSpeech = options;
        else PriceSpeech = options;
    }

    public bool ColibriEnabled { get; set; }
    public bool ColibriOverviewSync { get; set; }
    public string ColibriSlot { get; set; } = "";
    public ChartGesture Ruler { get; set; } = new(Key.None, MouseButton.Middle);
    public ModifierKeys ChartMagnet { get; set; } = ModifierKeys.Shift;
    public Dictionary<string, ChartDrawing[]> ChartDrawings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ChartIndicatorOptions> ChartIndicators { get; set; } = new(StringComparer.Ordinal);
    public ChartIndicatorOptions OverviewIndicators { get; set; } = ChartIndicatorOptions.Default;
    public Dictionary<string, Dictionary<Venue, string>> OverviewInterestContracts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ChartPaneWeights> ChartPaneWeights { get; set; } = new(StringComparer.Ordinal);
    public Key Fullscreen { get; set; } = Key.F;
    public int Grid { get; set; } = 2;
    public CellChoice?[] Cells { get; set; } = new CellChoice?[9];

    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThinMarketMonitor", "settings.json");

    public Dictionary<Venue, ListingRule> ListingRules(Segment segment, bool overview = false) =>
        overview ? segment == Segment.Spot ? OverviewSpotRules : OverviewFuturesRules :
        segment == Segment.Spot ? Rules : FuturesRules;

    public void CycleListingRule(MarketSource source, bool overview) =>
        ListingFilter.Cycle(ListingRules(source.Segment, overview), source.Venue);

    public static Settings Load()
    {
        Settings settings;
        try { return Deserialize(File.ReadAllText(PathName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { settings = new(); }
        return settings;
    }

    internal static Settings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json) ?? new();
        (settings.Densities ??= new()).Normalize();
        settings.Rules ??= new();
        settings.FuturesRules ??= [];
        settings.OverviewSpotRules ??= [];
        settings.OverviewFuturesRules ??= [];
        if (!Enum.IsDefined(settings.PriceSignalTone)) settings.PriceSignalTone = SignalTone.System;
        if (!Enum.IsDefined(settings.VolumeSignalTone)) settings.VolumeSignalTone = SignalTone.System;
        if (!Enum.IsDefined(settings.DensityTone)) settings.DensityTone = SignalTone.System;
        settings.Audio ??= new();
        settings.Audio.Normalize();
        if (!Enum.IsDefined(settings.PriceAlertTone)) settings.PriceAlertTone = SignalTone.System;
        settings.PriceAlerts = (settings.PriceAlerts ?? []).OfType<PriceAlert>().Where(alert => alert.Valid)
            .DistinctBy(alert => alert.Id).ToArray();
        if (settings.SignalCooldownMinutes is < 0 or > MaxSignalCooldownMinutes) settings.SignalCooldownMinutes = 0;
        settings.PriceSpeech = SpeechOptions(settings.PriceSpeech);
        settings.VolumeSpeech = SpeechOptions(settings.VolumeSpeech);
        settings.OverviewQuotes = settings.OverviewQuotes?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(CoinKey).Distinct(StringComparer.Ordinal).ToArray();
        settings.ColibriSlot = settings.ColibriSlot?.Trim() ?? "";
        if (settings.SpreadFilter is not { Valid: true }) settings.SpreadFilter = new();
        settings.SignalBlacklist = (settings.SignalBlacklist ?? []).Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(CoinKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        settings.FrameButtons = (settings.FrameButtons ?? [15, 60, 300]).Where(Frames.All.Contains).Distinct().Order().ToArray();
        if (!MarketSource.All.Contains(settings.OverviewSource)) settings.OverviewSource = new(Venue.KuCoin);
        settings.OverviewSources = settings.OverviewSources?.Where(MarketSource.All.Contains).Distinct().ToArray();
        settings.OverviewDisabledVenues = (settings.OverviewDisabledVenues ?? []).Where(Enum.IsDefined).ToHashSet();
        if (!Frames.All.Contains(settings.OverviewFrame)) settings.OverviewFrame = 60;
        if (settings.OverviewDrawerWidth is < 230 or > 1600) settings.OverviewDrawerWidth = 390;
        settings.OverviewColumnWidths = (settings.OverviewColumnWidths ?? [])
            .Where(x => x.Value is >= 56 and <= 1200).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        settings.OverviewBookmarks = (settings.OverviewBookmarks ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && Enum.IsDefined(x.Value))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        if (settings.OverviewBookmarkPriority is { } priority && !Enum.IsDefined(priority)) settings.OverviewBookmarkPriority = null;
        settings.OverviewColumns = (settings.OverviewColumns ?? [])
            .Select(x => x?.Kind == MetricKind.Spread ? new OverviewColumn(MetricKind.Spread, 0) : x)
            .Where(x => x != null && Enum.IsDefined(x.Kind) && (x.Kind == MetricKind.Spread || x.Minutes is >= 1 and <= 1440))
            .Distinct().Take(5).ToArray()!;
        if (settings.OverviewSort < -1 || settings.OverviewSort >= settings.OverviewColumns.Length) settings.OverviewSort = -1;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        bool versioned = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(SchemaVersion), out _);
        bool exactPeriods = versioned && settings.SchemaVersion >= EditableSignalPeriodsVersion;
        bool defaultVolume = versioned && settings.SchemaVersion >= DefaultChartVolumeVersion;
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, DefaultChartVolumeVersion);
        var price = ValidThresholds(settings.Thresholds ?? Defaults, 10_000).ToArray();
        if (!exactPeriods)
        {
            bool allDisabled = price.Length > 0 && price.All(p => !p.Enabled);
            var defaults = Defaults.Select(r => r.Seconds == 300 && allDisabled ? r with { Enabled = false } : r);
            settings.Thresholds = OrderThresholds(defaults.Select(r => price.FirstOrDefault(p => p.Seconds == r.Seconds) ?? r)
                .Concat(price));
        }
        else settings.Thresholds = OrderThresholds(price);
        bool percentSettings = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(VolumeGrowthThresholds), out _);
        if (!percentSettings && (settings.VolumeThresholds != null || settings.VolumeModule))
        {
            var legacy = (settings.VolumeThresholds ?? []).Where(r => r is { Seconds: >= 1 and <= MaxModuleSeconds })
                .Select(r => new Threshold(r.Seconds, r.Enabled, 100)).ToArray();
            settings.VolumeGrowthThresholds = OrderThresholds(legacy.Concat(DefaultVolumeGrowth));
            settings.VolumeModule = false;
            settings.VolumePercentMigrationPending = true;
        }
        settings.VolumeThresholds = null;
        var growth = ValidThresholds(settings.VolumeGrowthThresholds ?? DefaultVolumeGrowth, 1e12);
        settings.VolumeGrowthThresholds = OrderThresholds(exactPeriods ? growth : growth.Concat(DefaultVolumeGrowth));
        if (settings.VolumeReferenceSeconds is < 60 or > MaxVolumeReferenceSeconds || settings.VolumeReferenceSeconds % 60 != 0)
            settings.VolumeReferenceSeconds = 3600;
        settings.VolumeFilters = (settings.VolumeFilters ?? []).Where(r => r is { Seconds: >= 60 and <= MaxFilterSeconds, Usd: > 0 and <= 1e12 }
            && r.Seconds % 60 == 0).Take(MaxRules).ToArray();
        settings.Cells = (settings.Cells ?? []).Take(9).Concat(new CellChoice?[9]).Take(9).ToArray();
        settings.Grid = settings.Grid == 3 ? 3 : 2;
        if (!ChartGesture.Bindable(settings.Fullscreen)) settings.Fullscreen = Key.F;
        if (settings.ChartMagnet is not (ModifierKeys.None or ModifierKeys.Shift or ModifierKeys.Control or ModifierKeys.Alt))
            settings.ChartMagnet = ModifierKeys.Shift;
        settings.ChartDrawings = (settings.ChartDrawings ?? []).Where(pair => pair.Value != null && !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Where(drawing => drawing is { Valid: true })
                .DistinctBy(drawing => drawing.Id).ToArray(), StringComparer.Ordinal);
        settings.ChartIndicators = (settings.ChartIndicators ?? []).Where(pair => pair.Value != null && !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Normalized() with { Volume = !defaultVolume || pair.Value.Volume },
                StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty(nameof(OverviewIndicators), out _))
        {
            if (settings.OverviewChoice?.Key is { } market)
                settings.OverviewIndicators = settings.ChartIndicators.GetValueOrDefault(market) ?? ChartIndicatorOptions.Default;
            settings.OverviewInterestContracts = settings.ChartIndicators.Where(pair => pair.Value.HasInterest)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Interest!, StringComparer.Ordinal);
        }
        settings.OverviewIndicators = (settings.OverviewIndicators ?? ChartIndicatorOptions.Default).Normalized(keepContracts: false);
        settings.OverviewInterestContracts = (settings.OverviewInterestContracts ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
            .Select(pair => (pair.Key, Contracts: pair.Value
                .Where(choice => ChartIndicatorOptions.IsInterestSource(choice.Key) && !string.IsNullOrWhiteSpace(choice.Value))
                .ToDictionary(choice => choice.Key, choice => choice.Value)))
            .Where(pair => pair.Contracts.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Contracts, StringComparer.Ordinal);
        settings.ChartPaneWeights = (settings.ChartPaneWeights ?? []).Where(pair => pair.Value is { Valid: true } && !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (settings.Ruler == null || (settings.Ruler.Mouse is { } mouse ? !Enum.IsDefined(mouse)
                : !ChartGesture.Bindable(settings.Ruler.Key) || settings.Ruler.Key == settings.Fullscreen))
            settings.Ruler = new(Key.None, MouseButton.Middle);
        bool ValidFrameKey(Key key) => (ChartGesture.Bindable(key) || key is Key.Up or Key.Down) && key != settings.Fullscreen &&
            (settings.Ruler.Mouse != null || key != settings.Ruler.Key);
        if (!ValidFrameKey(settings.FrameUp)) settings.FrameUp = Key.Up;
        if (!ValidFrameKey(settings.FrameDown) || settings.FrameUp == settings.FrameDown)
            settings.FrameDown = settings.FrameUp == Key.Down ? Key.Up : Key.Down;
        return settings;
    }

    private static IEnumerable<Threshold> ValidThresholds(Threshold[] rules, double maxPercent) =>
        rules.Where(r => r is { Seconds: >= 1 and <= MaxModuleSeconds, Percent: > 0 } && r.Percent <= maxPercent);

    private static Threshold[] OrderThresholds(IEnumerable<Threshold> rules) =>
        rules.DistinctBy(r => r.Seconds).Take(MaxRules).OrderBy(r => r.Seconds).ToArray();

    private static SignalSpeechOptions SpeechOptions(SignalSpeechOptions? options) =>
        options == null ? SignalSpeechOptions.Default :
        options with { PercentDecimals = Math.Clamp(options.PercentDecimals, 0, SignalSpeech.MaxPercentDecimals) };

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        string temporary = PathName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, PathName, overwrite: true);
    }
}

internal static class ListingFilter
{
    internal static void Cycle(Dictionary<Venue, ListingRule> rules, Venue venue)
    {
        switch (rules.GetValueOrDefault(venue))
        {
            case ListingRule.Ignore: rules[venue] = ListingRule.Require; break;
            case ListingRule.Require: rules[venue] = ListingRule.Exclude; break;
            default: rules.Remove(venue); break;
        }
    }

    public static FilterResult Apply(IReadOnlyDictionary<Venue, Catalog> catalogs, IReadOnlyDictionary<Venue, ListingRule> rules)
        => Apply(catalogs.ToDictionary(x => new MarketSource(x.Key), x => x.Value),
            source => source.Segment == Segment.Spot ? rules.GetValueOrDefault(source.Venue) : ListingRule.Ignore);

    public static FilterResult Apply(IReadOnlyDictionary<MarketSource, Catalog> catalogs, Func<MarketSource, ListingRule> rule,
        MarketSource? source = null)
    {
        ListingRule EffectiveRule(MarketSource market) => market == source ? ListingRule.Require : rule(market);
        var marked = MarketSource.All.Where(v => EffectiveRule(v) != ListingRule.Ignore).ToArray();
        var missing = marked.Where(v => !Available(catalogs, v)).ToArray();
        if (missing.Length > 0) return new([], missing);

        var required = marked.Where(v => EffectiveRule(v) == ListingRule.Require).ToArray();
        var excluded = marked.Where(v => EffectiveRule(v) == ListingRule.Exclude).ToArray();
        var sources = source is { } selected ? [selected] : required.Length > 0 ? required :
            catalogs.Keys.Where(v => v.Segment == Segment.Spot && !excluded.Contains(v)).ToArray();
        var matching = Matching(catalogs, sources, required, excluded);
        if (source.HasValue) return new(matching.ToArray(), []);
        return new(matching
            .GroupBy(m => (m.ListingAsset, m.Source.Segment))
            .SelectMany(group =>
            {
                var ranked = group.OrderByDescending(m => m.Streaming).ThenBy(m => QuoteRank(m.Quote))
                    .ThenBy(m => m.Venue).ThenBy(m => m.Symbol, StringComparer.Ordinal).ToArray();
                var stablePairs = ranked.Where(m => m.Quote is "USDT" or "USDC" or "USD").DistinctBy(m => m.Quote).ToArray();
                return stablePairs.Length == 0 ? ranked.Take(1) : stablePairs;
            })
            .OrderBy(m => m.Base, StringComparer.Ordinal).ThenBy(m => m.Source.Segment)
            .ThenBy(m => QuoteRank(m.Quote)).ThenBy(m => m.Symbol, StringComparer.Ordinal).ToArray(), []);
    }

    internal static FilterResult CollectSources(IReadOnlyDictionary<MarketSource, Catalog> catalogs,
        IReadOnlyList<MarketSource> enabled, IReadOnlyList<MarketSource>? excluded = null,
        IReadOnlyDictionary<MarketSource, Catalog>? lastCatalogs = null)
    {
        if (enabled.Count == 0) return new([], []);
        var missing = enabled.Concat(excluded ?? []).Where(source => !Available(catalogs, source)).ToArray();
        var excludedAssets = excluded?.SelectMany(source => Available(catalogs, source) ? catalogs[source].Markets :
                lastCatalogs?.GetValueOrDefault(source)?.Markets ?? [])
            .Select(market => market.ListingAsset).ToHashSet(StringComparer.Ordinal);
        return new(enabled.Where(source => Available(catalogs, source)).SelectMany(source => catalogs[source].Markets)
            .Where(market => excludedAssets?.Contains(market.ListingAsset) != true)
            .DistinctBy(market => market.Key, StringComparer.Ordinal).ToArray(), missing);
    }

    private static bool Available(IReadOnlyDictionary<MarketSource, Catalog> catalogs, MarketSource source) =>
        catalogs.TryGetValue(source, out var catalog) && catalog.Error == null;

    private static IEnumerable<Market> Matching(IReadOnlyDictionary<MarketSource, Catalog> catalogs, IEnumerable<MarketSource> sources,
        MarketSource[] required, MarketSource[] excluded)
    {
        var bases = required.Concat(excluded).ToDictionary(source => source,
            source => catalogs[source].Markets.Select(market => market.ListingAsset).ToHashSet(StringComparer.Ordinal));
        return sources.Where(source => Available(catalogs, source)).SelectMany(source => catalogs[source].Markets)
            .Where(market => required.All(source => bases[source].Contains(market.ListingAsset)) &&
                excluded.All(source => !bases[source].Contains(market.ListingAsset)));
    }

    private static int QuoteRank(string quote) => quote switch
    {
        "USDT" => 0, "USDC" => 1, "USD" => 2, "BTC" => 3, "ETH" => 4, _ => 5
    };
}

internal static class ChartPlacement
{
    public static int Pick(string key, IReadOnlyList<(string? Key, bool Pinned, long Order)> cells)
    {
        if (cells.Any(c => c.Key == key)) return -1;
        for (int i = 0; i < cells.Count; i++)
            if (cells[i].Key == null) return i;
        return Enumerable.Range(0, cells.Count).Where(i => !cells[i].Pinned)
            .OrderBy(i => cells[i].Order).FirstOrDefault(-1);
    }
}

internal sealed class SpikeDetector(Threshold[] rules)
{
    private readonly List<Trade> samples = [];
    private readonly bool[] latched = new bool[rules.Length];
    private long lastAlert;
    private int start;
    private readonly long retention = (rules.Select(x => x.Seconds).DefaultIfEmpty(15).Max() + 40L) * 1000;

    public Signal? Observe(Market market, Trade trade, long now, bool eligible = true)
    {
        if (rules.Length == 0 || !double.IsFinite(trade.Price) || trade.Price <= 0 || trade.Time < now - 5000 ||
            trade.Time > now + 3000 || (samples.Count > 0 && trade.Time <= samples[^1].Time)) return null;

        // Keep the latest real trade per second to bound history per instrument.
        if (samples.Count > 0 && samples[^1].Time / 1000 == trade.Time / 1000)
            samples[^1] = trade;
        else
            samples.Add(trade);

        while (samples[start].Time < trade.Time - retention) start++;
        if (start > 256)
        {
            samples.RemoveRange(0, start);
            start = 0;
        }

        if (!eligible) return null;
        Signal? best = null;
        for (int i = 0; i < rules.Length; i++)
        {
            var rule = rules[i];
            if (!rule.Enabled) continue;
            long target = trade.Time - rule.Seconds * 1000L;
            int low = start, high = samples.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (samples[middle].Time < target) low = middle + 1;
                else high = middle;
            }
            int baseline = low;
            if (low == samples.Count || (low > start && target - samples[low - 1].Time <= samples[low].Time - target))
                baseline = low - 1;
            int tolerance = rule.Seconds == 15 ? 3000 : rule.Seconds == 60 ? 8000 : Math.Clamp(rule.Seconds * 1000 / 7, 500, 30_000);
            if (Math.Abs(samples[baseline].Time - target) > tolerance) continue;
            double change = (trade.Price / samples[baseline].Price - 1) * 100;
            if (change < rule.Percent * 0.75) latched[i] = false;
            if (change + 1e-9 < rule.Percent || latched[i]) continue;
            latched[i] = true;
            best ??= new(market, trade.Time, rule.Seconds, change, LastPrice: trade.Price);
        }

        if (best == null || (lastAlert != 0 && trade.Time - lastAlert < 30_000)) return null;
        lastAlert = trade.Time;
        return best;
    }
}
