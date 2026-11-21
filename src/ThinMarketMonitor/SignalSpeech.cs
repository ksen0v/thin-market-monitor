using System.Globalization;
using System.Text.RegularExpressions;

namespace ThinMarketMonitor;

internal static class SignalSpeech
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] Letters =
    [
        "эй", "би", "си", "ди", "и", "эф", "джи", "эйч", "ай", "джей", "кей", "эл", "эм",
        "эн", "оу", "пи", "кью", "ар", "эс", "ти", "ю", "ви", "дабл ю", "икс", "уай", "зи"
    ];
    private static readonly Regex TickerSeparators = new(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);

    public static string Format(Signal signal)
    {
        string pair = $"{PronounceAsset(signal.Market.Base)} {PronounceAsset(signal.Market.Quote)}".Trim();
        string exchange = signal.Market.Venue switch
        {
            Venue.Binance => "Бинанс", Venue.Bybit => "Байбит", Venue.Okx => "О кей икс", Venue.Bitget => "Битгет",
            Venue.Mexc => "Мекс", Venue.KuCoin => "Кукоин", Venue.Gate => "Гейт", Venue.Alpha => "Бинанс Альфа",
            Venue.Htx => "Эйч ти икс", Venue.Kraken => "Кракен", Venue.Weex => "Уикс", Venue.Lighter => "Лайтер",
            Venue.Hyperliquid => "Хайперликвид", _ => signal.Market.Venue.Label()
        };
        string segment = signal.Market.Venue == Venue.Alpha ? "" : signal.Market.Contract == null ? " спот" : " фьючерсы";
        string kind = signal.Module == SignalModule.Volume ? "Рост объёма" : "Импульс";
        double value = Math.Round(signal.Value, 2, MidpointRounding.AwayFromZero);
        string number = value.ToString("0.##", Russian);
        string units = value % 1 == 0 ? Inflect((int)(Math.Abs(value) % 100), "процент", "процента", "процентов") : "процента";
        return $"{pair} на {exchange}{segment}. {kind} {number} {units} за {Duration(signal.Seconds)}.";
    }

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
