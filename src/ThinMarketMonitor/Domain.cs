using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace ThinMarketMonitor;

internal enum Venue { Binance, Bybit, Okx, Bitget, Mexc, KuCoin, Gate, Alpha }
internal enum ListingRule { Ignore, Require, Exclude }
internal static class Venues
{
    public static string Label(this Venue venue) => venue switch
    {
        Venue.Okx => "OKX", Venue.Bitget => "Bitget", Venue.Mexc => "MEXC",
        Venue.Alpha => "Binance Alpha", _ => venue.ToString()
    };
}

internal record Market(Venue Venue, string Symbol, string Base, string Quote)
{
    public string Key => $"{Venue}:{Symbol}";
    public string Label => $"{Base}/{Quote} · {Venue.Label()}" + (Venue == Venue.Alpha ? $" · {Symbol}" : "");
}

internal record Candle(long Time, double Open, double High, double Low, double Close);
internal record Trade(string Symbol, long Time, double Price, double Size = 0, string? Id = null);
internal record Threshold(int Seconds, bool Enabled, double Percent);
internal record VolumeRule(int Seconds, bool Enabled, double Usd);
internal enum SignalModule { Price, Volume }
internal record Signal(Market Market, long Time, int Seconds, double Value, SignalModule Module = SignalModule.Price);
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
internal record Catalog(IReadOnlyList<Market> Markets, string? Error = null);
internal record CellChoice(string Key, bool Pinned, long Order = 0);
internal record FilterResult(IReadOnlyList<Market> Markets, IReadOnlyList<Venue> Missing);

internal sealed class Settings
{
    internal const int MaxModuleSeconds = 3600;
    internal const int MaxFilterSeconds = 86400;
    internal const int MaxRules = 24;
    internal static readonly Threshold[] Defaults = [new(15, true, 4), new(60, true, 5), new(300, true, 12), new(900, true, 20)];
    internal static readonly VolumeRule[] DefaultVolumes = [new(15, true, 100), new(60, true, 400), new(300, true, 2000), new(900, true, 6000)];
    public Dictionary<Venue, ListingRule> Rules { get; set; } = new() { [Venue.KuCoin] = ListingRule.Require };
    public Threshold[] Thresholds { get; set; } = Defaults.ToArray();
    public VolumeRule[] VolumeThresholds { get; set; } = DefaultVolumes.ToArray();
    public VolumeRule[] VolumeFilters { get; set; } = [];
    public bool PriceModule { get; set; } = true;
    public bool VolumeModule { get; set; }
    public ChartGesture Ruler { get; set; } = new(Key.None, MouseButton.Middle);
    public Key Fullscreen { get; set; } = Key.F;
    public int Grid { get; set; } = 2;
    public CellChoice?[] Cells { get; set; } = new CellChoice?[9];

    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThinMarketMonitor", "settings.json");

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
        settings.Rules ??= new();
        var price = (settings.Thresholds ?? []).Where(r => r is { Seconds: >= 1 and <= MaxModuleSeconds, Percent: > 0 and <= 10000 }).ToArray();
        var defaults = Defaults.Select(r => r.Seconds == 300 && price.Length > 0 && price.All(p => !p.Enabled)
            ? r with { Enabled = false } : r);
        settings.Thresholds = defaults.Select(r => price.FirstOrDefault(p => p.Seconds == r.Seconds) ?? r)
            .Concat(price).DistinctBy(r => r.Seconds).Take(MaxRules).OrderBy(r => r.Seconds).ToArray();
        var volume = (settings.VolumeThresholds ?? []).Where(r => r is { Seconds: >= 1 and <= MaxModuleSeconds, Usd: > 0 and <= 1e12 });
        var volumes = volume.ToArray();
        settings.VolumeThresholds = DefaultVolumes.Select(r => volumes.FirstOrDefault(p => p.Seconds == r.Seconds) ?? r)
            .Concat(volumes).DistinctBy(r => r.Seconds).Take(MaxRules).OrderBy(r => r.Seconds).ToArray();
        settings.VolumeFilters = (settings.VolumeFilters ?? []).Where(r => r is { Seconds: >= 60 and <= MaxFilterSeconds, Usd: > 0 and <= 1e12 }
            && r.Seconds % 60 == 0).Take(MaxRules).ToArray();
        settings.Cells = (settings.Cells ?? []).Take(9).Concat(new CellChoice?[9]).Take(9).ToArray();
        settings.Grid = settings.Grid == 3 ? 3 : 2;
        if (!ChartGesture.Bindable(settings.Fullscreen)) settings.Fullscreen = Key.F;
        if (settings.Ruler == null || (settings.Ruler.Mouse is { } mouse ? !Enum.IsDefined(mouse)
                : !ChartGesture.Bindable(settings.Ruler.Key) || settings.Ruler.Key == settings.Fullscreen))
            settings.Ruler = new(Key.None, MouseButton.Middle);
        return settings;
    }

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
    public static FilterResult Apply(IReadOnlyDictionary<Venue, Catalog> catalogs, IReadOnlyDictionary<Venue, ListingRule> rules)
    {
        var marked = Enum.GetValues<Venue>().Where(v => rules.GetValueOrDefault(v) != ListingRule.Ignore).ToArray();
        var missing = marked.Where(v => !catalogs.TryGetValue(v, out var c) || c.Error != null).ToArray();
        if (missing.Length > 0) return new([], missing);

        var required = marked.Where(v => rules.GetValueOrDefault(v) == ListingRule.Require).ToArray();
        var excluded = marked.Where(v => rules.GetValueOrDefault(v) == ListingRule.Exclude).ToArray();
        var sources = required.Length > 0 ? required : catalogs.Keys.Where(v => !excluded.Contains(v)).ToArray();
        var bases = marked.ToDictionary(v => v, v => catalogs[v].Markets.Select(m => m.Base).ToHashSet());
        return new(sources.Where(v => catalogs[v].Error == null).SelectMany(v => catalogs[v].Markets)
            .Where(m => required.All(v => bases[v].Contains(m.Base)) && excluded.All(v => !bases[v].Contains(m.Base)))
            .GroupBy(m => m.Base)
            .Select(g => g.OrderBy(m => QuoteRank(m.Quote)).ThenBy(m => m.Venue)
                .ThenBy(m => m.Symbol, StringComparer.Ordinal).First())
            .OrderBy(m => m.Base, StringComparer.Ordinal).ToArray(), []);
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
            best ??= new(market, trade.Time, rule.Seconds, change);
        }

        if (best == null || (lastAlert != 0 && trade.Time - lastAlert < 30_000)) return null;
        lastAlert = trade.Time;
        return best;
    }
}
