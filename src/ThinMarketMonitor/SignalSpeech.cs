using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThinMarketMonitor;

internal sealed record SignalSpeechOptions(string? Template = null, int PercentDecimals = 0)
{
    internal static SignalSpeechOptions Default { get; } = new();
    internal string Text => Template ?? SignalSpeech.DefaultTemplate;
}

internal static class SignalSpeech
{
    internal const string DefaultTemplate = "{тикер} на {площадка}. {тип} {процент} за {период}.";
    internal const int MaxPercentDecimals = 2;
    private const string Missing = "нет данных";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] Letters =
    [
        "эй", "би", "си", "ди", "и", "эф", "джи", "эйч", "ай", "джей", "кей", "эл", "эм",
        "эн", "оу", "пи", "кью", "ар", "эс", "ти", "ю", "ви", "дабл ю", "икс", "уай", "зи"
    ];
    private static readonly Regex TickerSeparators = new(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);
    private static readonly string[] PercentFormats = ["0", "0.#", "0.##"];

    internal sealed record Field(string Name, string Label, Func<Signal, int, string> Read, SignalModule? Module = null)
    {
        public string Token => $"{{{Name}}}";
        public string Option => $"{Label} · {Token}";
        public bool AppliesTo(SignalModule module) => Module == null || Module == module;
    }

    private static readonly Field[] Fields =
    [
        new("тикер", "Торговая пара", (s, _) => $"{PronounceAsset(s.Market.Base)} {PronounceAsset(s.Market.Quote)}".Trim()),
        new("процент", "Изменение в процентах", (s, digits) => Percent(s.Value, digits, withUnits: true)),
        new("тип", "Тип сигнала", (s, _) => s.Module == SignalModule.Volume ? "Рост объёма" : "Импульс"),
        new("площадка", "Биржа и рынок", (s, _) => s.Market.Venue == Venue.Alpha
            ? Exchange(s.Market.Venue) : $"{Exchange(s.Market.Venue)} {MarketSegment(s.Market)}"),
        new("период", "Период сигнала", (s, _) => Duration(s.Seconds)),
        new("монета", "Монета", (s, _) => PronounceAsset(s.Market.Base)),
        new("котировка", "Валюта котировки", (s, _) => PronounceAsset(s.Market.Quote)),
        new("биржа", "Биржа", (s, _) => Exchange(s.Market.Venue)),
        new("рынок", "Рынок", (s, _) => MarketSegment(s.Market)),
        new("число", "Процент числом", (s, digits) => Percent(s.Value, digits, withUnits: false)),
        new("секунды", "Период числом в секундах", (s, _) => s.Seconds.ToString(Russian)),
        new("цена", "Цена сделки", (s, _) => Price(s), SignalModule.Price),
        new("оборот", "Оборот за период", (s, _) => Dollars(s.CurrentTurnover), SignalModule.Volume),
        new("средний_оборот", "Средний оборот", (s, _) => Dollars(s.AverageTurnover), SignalModule.Volume),
        new("период_среднего", "Период среднего", (s, _) => s.ReferenceSeconds is > 0
            ? Duration(s.ReferenceSeconds.Value) : Missing, SignalModule.Volume)
    ];
    private static readonly Dictionary<string, Field> FieldsByName = Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<Field> FieldsFor(SignalModule module) => Fields.Where(field => field.AppliesTo(module));

    public static Signal Example(SignalModule module) => module == SignalModule.Volume
        ? new(new(Venue.Bybit, "ETHUSDT", "ETH", "USDT", new("linear")), 0, 60, 215.37, module, 25_230, 8_000, 3600)
        : new(new(Venue.Gate, "BTC_USDT", "BTC", "USDT"), 0, 30, 15.94, LastPrice: 64_200);

    public static string Format(Signal signal, SignalSpeechOptions? options = null)
    {
        options ??= SignalSpeechOptions.Default;
        if (TryFormat(signal, options, out string phrase, out _)) return phrase;
        TryFormat(signal, options with { Template = null }, out phrase, out _);
        return phrase;
    }

    public static bool TryFormat(Signal signal, SignalSpeechOptions options, out string phrase, out string? error)
    {
        phrase = "";
        string template = options.Text.Trim();
        if (template.Length == 0)
        {
            error = "Введите текст озвучки.";
            return false;
        }
        int digits = Math.Clamp(options.PercentDecimals, 0, MaxPercentDecimals);
        var result = new StringBuilder(template.Length);
        int position = 0;
        while (position < template.Length)
        {
            int offset = template.AsSpan(position).IndexOfAny('{', '}');
            if (offset < 0)
            {
                result.Append(template, position, template.Length - position);
                break;
            }
            int open = position + offset;
            int close = template.IndexOf('}', open + 1);
            if (template[open] != '{' || close < 0 || template.AsSpan(open + 1, close - open - 1).Contains('{'))
            {
                error = "Проверьте фигурные скобки. Пример поля: {тикер}.";
                return false;
            }
            string name = template[(open + 1)..close].Trim();
            if (!FieldsByName.TryGetValue(name, out var field))
            {
                error = $"Неизвестное поле {{{name}}}. Выберите поле из списка.";
                return false;
            }
            if (!field.AppliesTo(signal.Module))
            {
                error = $"Поле {field.Token} недоступно для этого типа сигнала.";
                return false;
            }
            result.Append(template, position, offset);
            result.Append(field.Read(signal, digits));
            position = close + 1;
        }
        phrase = result.ToString();
        error = null;
        return true;
    }

    private static string Exchange(Venue venue) => venue switch
    {
        Venue.Binance => "Бинанс", Venue.Bybit => "Байбит", Venue.Okx => "О кей икс", Venue.Bitget => "Битгет",
        Venue.Mexc => "Мекс", Venue.KuCoin => "Кукоин", Venue.Gate => "Гейт", Venue.Alpha => "Бинанс Альфа",
        Venue.Htx => "Эйч ти икс", Venue.Kraken => "Кракен", Venue.Weex => "Уикс", Venue.Lighter => "Лайтер",
        Venue.Hyperliquid => "Хайперликвид", _ => venue.Label()
    };

    private static string MarketSegment(Market market) => market.Contract == null ? "спот" : "фьючерсы";

    private static string Percent(double value, int digits, bool withUnits)
    {
        if (!double.IsFinite(value)) return Missing;
        double rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        string number = rounded.ToString(PercentFormats[digits], Russian);
        if (!withUnits) return number;
        if (digits == 0 && value > 0 && rounded == 0) return "меньше одного процента";
        return $"{number} {Units(rounded, "процент", "процента", "процентов")}";
    }

    private static string Price(Signal signal)
    {
        if (signal.LastPrice is not { } price || !double.IsFinite(price)) return Missing;
        string number = price.ToString("0.##################", Russian);
        if (number == "0" && price != 0) number = price.ToString("G12", Russian);
        return $"{number} {PronounceAsset(signal.Market.Quote)}";
    }

    private static string Dollars(double? value)
    {
        if (value is not { } amount || !double.IsFinite(amount)) return Missing;
        double rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return $"{rounded.ToString("0.##", Russian)} {Units(rounded, "доллар", "доллара", "долларов")}";
    }

    private static string Units(double amount, string one, string few, string many) =>
        amount % 1 == 0 ? Inflect((int)(Math.Abs(amount) % 100), one, few, many) : few;

    private static string PronounceAsset(string asset)
    {
        string name = asset.ToUpperInvariant();
        return name switch
        {
            "ETH" => "эфир",
            "SOL" => "сол",
            "DOGE" => "додж",
            _ when name.Length is > 0 and <= 4 && name.All(c => c is >= 'A' and <= 'Z') =>
                string.Join(' ', name.Select(c => Letters[c - 'A'])),
            _ => TickerSeparators.Replace(asset, " ").Trim()
        };
    }

    private static string Duration(int seconds)
    {
        if (seconds % 3600 == 0) return $"{seconds / 3600} {Inflect(seconds / 3600, "час", "часа", "часов")}";
        if (seconds % 60 == 0) return $"{seconds / 60} {Inflect(seconds / 60, "минуту", "минуты", "минут")}";
        return $"{seconds} {Inflect(seconds, "секунду", "секунды", "секунд")}";
    }

    private static string Inflect(int value, string one, string few, string many) =>
        value % 100 is >= 11 and <= 14 ? many : (value % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
}
