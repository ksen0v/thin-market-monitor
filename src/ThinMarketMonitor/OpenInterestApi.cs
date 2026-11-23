using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private sealed record InterestRequest(int Period, string Unit, int PageSize, Func<int, long, string> Latest,
        Func<long, long, string>? History = null);

    public static bool SupportsOpenInterest(Market market) => InterestRoute(market, 60) != null;

    private static InterestRequest? InterestRoute(Market market, int tf)
    {
        if (market.Contract is not { } contract) return null;
        string symbol = Uri.EscapeDataString(market.Symbol), product = contract.Product;
        string basis = contract.NativeBase ?? market.Base;
        int step = tf <= 300 ? 300 : tf <= 900 ? 900 : tf <= 3600 ? 3600 : tf <= 14400 ? 14400 : 86400;
        string interval = step switch { 300 => "5m", 900 => "15m", 3600 => "1h", 14400 => "4h", _ => "1d" };
        string minuteInterval = step switch { 300 => "5min", 900 => "15min", 3600 => "1hour", 14400 => "4hour", _ => "1day" };
        string bybitInterval = step < 3600 ? minuteInterval : interval;
        int htxStep = Math.Max(3600, step);
        string htxInterval = htxStep switch { 3600 => "60min", 14400 => "4hour", _ => "1day" };
        string kuCoinInterest = $"https://api.kucoin.com/api/ua/v2/market/open-interest?symbol={symbol}&interval={minuteInterval}";
        string gateInterest = GateContract + $"{product}/contract_stats?contract={symbol}&interval={interval}";
        string krakenInterest = $"https://futures.kraken.com/api/charts/v1/analytics/{symbol}/open-interest?interval={step}";
        return market.Venue switch
        {
            Venue.Binance when product == "usdm" => Bounded(step, basis, 300,
                $"https://fapi.binance.com/futures/data/openInterestHist?symbol={symbol}&period={interval}"),
            Venue.Binance when product == "coinm" && market.Symbol.EndsWith("_PERP", StringComparison.Ordinal) =>
                Bounded(step, "контр.", 300,
                    $"https://dapi.binance.com/futures/data/openInterestHist?pair={symbol[..^5]}&contractType=PERPETUAL&period={interval}"),
            Venue.Binance when product == "coinm" => Current("контр.", Coinm + $"openInterest?symbol={symbol}"),
            Venue.Bybit when product is "linear" or "inverse" => Bounded(step, product == "inverse" ? market.Quote : basis, 180,
                Bybit + $"open-interest?category={product}&symbol={symbol}&intervalTime={bybitInterval}"),
            Venue.Okx => OkxHistory(),
            Venue.Bitget when product is "USDT-FUTURES" or "USDC-FUTURES" =>
                new(step, basis, 30, (_, _) => $"https://api.bitget.com/api/v3/market/open-interest-history?symbol={symbol}&period={interval}"),
            Venue.Bitget => Current(basis,
                $"https://api.bitget.com/api/v3/market/open-interest?category={product}&symbol={symbol}"),
            Venue.Mexc => Current("контр.", MexcContract + $"ticker?symbol={symbol}"),
            Venue.KuCoin => new(step, "контр.", 100, (n, _) => kuCoinInterest + $"&pageSize={n}",
                (from, until) => kuCoinInterest + $"&startAt={from}&endAt={until - 1}&pageSize=100"),
            Venue.Gate when product.StartsWith("futures/", StringComparison.Ordinal) => new(step, "контр.", 100,
                (n, _) => gateInterest + $"&limit={n}",
                (from, until) => gateInterest + $"&from={from / 1000}&to={(until - 1) / 1000}&limit=100"),
            Venue.Htx when product is "linear" or "inverse" => new(htxStep, "контр.", 200, (n, _) => HtxContract +
                $"{(product == "linear" ? "linear-swap-api" : "swap-api")}/v1/swap_his_open_interest?contract_code={symbol}&period={htxInterval}&amount_type=1&size={n}"),
            Venue.Htx when product == "delivery" => Current("контр.", HtxContract + $"api/v1/contract_open_interest?contract_code={symbol}"),
            Venue.Kraken => new(step, contract.Unit == QuantityUnit.Quote ? "контр." : basis, 180,
                (n, now) => krakenInterest + $"&since={now / 1000 - (long)step * n}",
                (from, until) => krakenInterest + $"&since={from / 1000}&to={(until - 1) / 1000}"),
            Venue.Weex => Current("контр.", WeexFutures + $"openInterest?symbol={symbol}"),
            Venue.Lighter => Current("USD", Lighter + $"orderBookDetails?market_id={symbol}"),
            Venue.Hyperliquid => Current(basis, ""),
            _ => null
        };

        InterestRequest OkxHistory()
        {
            int period = Math.Min(step, 14400);
            string bar = period >= 3600 ? (period == 3600 ? "1H" : "4H") : interval;
            string url = Okx + $"rubik/stat/contracts/open-interest-history?instId={symbol}&period={bar}";
            // OKX's begin and end are exclusive.
            return new(period, "контр.", 100, (n, _) => url + $"&limit={n}",
                (from, until) => url + $"&begin={from - 1}&end={until}&limit=100");
        }

        static InterestRequest Bounded(int period, string unit, int count, string url) => new(period, unit, count,
            (n, _) => url + $"&limit={n}",
            (from, until) => url + $"&startTime={from}&endTime={until - 1}&limit={count}");

        static InterestRequest Current(string unit, string url) => new(0, unit, 1, (_, _) => url);
    }

    internal static InterestPoint[] DecodeInterest(Market market, JsonElement root, int period, long observedAt)
    {
        string symbol = market.Symbol;
        IEnumerable<InterestPoint> points = market.Venue switch
        {
            Venue.Binance when period == 0 => root.Optional("symbol") == symbol
                ? [Native("time", "openInterest", root, FaceValue(root.Number("openInterest")))] : [],
            Venue.Binance => root.EnumerateArray().Where(row => market.Contract?.Product == "coinm"
                ? row.Optional("pair") + "_PERP" == symbol && row.Optional("contractType") == "PERPETUAL"
                : row.Optional("symbol") == symbol).Select(row => Native("timestamp", "sumOpenInterest", row,
                    market.Contract?.Product == "coinm" ? FaceValue(row.Number("sumOpenInterest")) :
                    QuoteValue(row.Number("sumOpenInterestValue")))),
            Venue.Bybit => MatchedBybit(),
            Venue.Okx => Envelope(root, "0").EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 4)
                .Select(row => new InterestPoint(row[0].Int(), row[1].Num(), ValidUsd(row[3].Num()))),
            Venue.Bitget => BitgetInterest(),
            Venue.Mexc => MexcInterest(),
            Venue.KuCoin => Envelope(root, "200000").EnumerateArray()
                .Select(row => Native("ts", "openInterest", row, FaceValue(row.Number("openInterest")))),
            Venue.Gate => root.EnumerateArray().Select(row => Native("time", "open_interest", row,
                QuoteValue(row.Number("open_interest_usd")) ?? FaceValue(row.Number("open_interest")), scale: 1000)),
            Venue.Htx => HtxInterest(),
            Venue.Kraken => KrakenInterest(),
            Venue.Weex => root.Optional("symbol") == symbol ? [Native("time", "openInterest", root)] : [],
            Venue.Lighter => Named(LighterField(root, "order_book_details"), "market_id")
                .Where(row => row.Optional("market_type") == "perp")
                .Select(row => new InterestPoint(observedAt, row.Number("open_interest_usd"), ValidUsd(row.Number("open_interest_usd")))),
            Venue.Hyperliquid => HyperInterest(),
            _ => []
        };
        return points.Where(point => point.Time > 0 && point.Time <= observedAt + 3000 &&
            double.IsFinite(point.Value) && point.Value >= 0).DistinctBy(point => point.Time).OrderBy(point => point.Time).ToArray();

        double? QuoteValue(double value) => UsdInterestQuote(market) ? ValidUsd(value) : null;
        double? FaceValue(double size) => OiFaceValue(market, size);

        IEnumerable<JsonElement> Named(JsonElement container, string field) =>
            container.EnumerateArray().Where(row => row.Optional(field) == symbol);

        IEnumerable<InterestPoint> MatchedBybit()
        {
            var data = BybitResult(root);
            if (data.Optional("symbol") != symbol || data.Optional("category") != market.Contract?.Product) return [];
            return data.GetProperty("list").EnumerateArray().Select(row =>
            {
                // Both-sided size is twice the single-sided open interest.
                double single = row.Number("singleOpenInterest");
                double amount = single >= 0 && double.IsFinite(single) ? single : row.Number("openInterest") / 2;
                return new InterestPoint((long)row.Number("timestamp"), amount,
                    market.Contract?.Product == "inverse" ? QuoteValue(amount) : null);
            });
        }

        IEnumerable<InterestPoint> BitgetInterest()
        {
            var data = Envelope(root, "00000");
            return Named(data.GetProperty("list"), "symbol").Select(row => period == 0
                ? new InterestPoint((long)data.Number("ts"), row.Number("openInterest"))
                : Native("timestamp", "openInterest", row, QuoteValue(row.Number("openInterestValue"))));
        }

        IEnumerable<InterestPoint> MexcInterest()
        {
            var data = Envelope(root, "0");
            return data.Optional("symbol") == symbol
                ? [Native("timestamp", "holdVol", data, EstimateOi(market, data.Number("holdVol"), data.Number("fairPrice")), estimated: true)] : [];
        }

        IEnumerable<InterestPoint> HtxInterest()
        {
            var data = HtxField(root, "data");
            if (period == 0)
                return Named(data, "contract_code").Select(row => new InterestPoint((long)root.Number("ts"), row.Number("volume"), FaceValue(row.Number("volume"))));
            return data.Optional("contract_code") == symbol
                ? data.GetProperty("tick").EnumerateArray().Where(row => row.Optional("amount_type") == "1")
                    .Select(row => Native("ts", "volume", row, market.Contract?.Product == "linear"
                        ? QuoteValue(row.Number("value")) : FaceValue(row.Number("volume")))) : [];
        }

        IEnumerable<InterestPoint> KrakenInterest()
        {
            var result = root.GetProperty("result");
            var timestamp = result.GetProperty("timestamp");
            var data = result.GetProperty("data");
            if (timestamp.GetArrayLength() != data.GetArrayLength()) throw new JsonException("Несогласованные данные Kraken");
            // This is the opening reading in the bucket, paired with the opening mark price.
            return Enumerable.Range(0, timestamp.GetArrayLength()).Select(i =>
            {
                double amount = data[i].ValueKind == JsonValueKind.Array ? data[i][0].Num() : data[i].Num();
                return new InterestPoint(timestamp[i].Int() * 1000, amount, FaceValue(amount));
            });
        }

        IEnumerable<InterestPoint> HyperInterest()
        {
            var universe = root[0].GetProperty("universe");
            var contexts = root[1];
            if (universe.GetArrayLength() != contexts.GetArrayLength()) throw new JsonException("Несогласованные данные Hyperliquid");
            return Enumerable.Range(0, universe.GetArrayLength()).Where(i => universe[i].Optional("name") == symbol)
                .Select(i =>
                {
                    double amount = contexts[i].Number("openInterest");
                    return new InterestPoint(observedAt, amount, EstimateOi(market, amount, contexts[i].Number("markPx")), true);
                });
        }
    }

    private static InterestPoint Native(string time, string number, JsonElement row, double? usd = null, bool estimated = false, int scale = 1) =>
        new((long)(row.Number(time) * scale), row.Number(number), usd, estimated && usd.HasValue);

    private static bool UsdInterestQuote(Market market) =>
        UsdQuotes.Supported(market.Quote) && market.Identifiers?.TrustedUsdQuote != false;

    private static double? ValidUsd(double value) => value >= 0 && double.IsFinite(value) ? value : null;

    private static double? OiFaceValue(Market market, double count) =>
        market.Contract is { Unit: QuantityUnit.Quote, Multiplier: > 0 } contract && UsdInterestQuote(market)
            ? ValidUsd(count * contract.Multiplier) : null;

    private static double? EstimateOi(Market market, double value, double price)
    {
        if (!UsdInterestQuote(market) || !double.IsFinite(price) || price <= 0) return null;
        double multiplier = market.Venue is Venue.KuCoin or Venue.Mexc ? market.Contract?.Multiplier ?? 0 : 1;
        return multiplier > 0 ? ValidUsd(value * multiplier * price) : null;
    }
}
