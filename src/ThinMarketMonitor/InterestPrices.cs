using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private async Task PriceInterest(Market market, int period, InterestPoint[] points, CancellationToken ct)
    {
        if (!UsdInterestQuote(market) || market.Contract?.Unit != QuantityUnit.Base || points.All(point => point.Usd.HasValue)) return;
        bool bitgetCurrent = market.Venue == Venue.Bitget && period == 0;
        if (!bitgetCurrent && market.Venue is not (Venue.Bybit or Venue.Kraken or Venue.KuCoin)) return;
        var missing = points.Where(point => point.Usd == null).ToArray();
        try
        {
            var prices = bitgetCurrent ? await BitgetInterestMark(market, missing[^1].Time, ct) :
                await InterestMarks(market, period, missing[0].Time, missing[^1].Time, ct);
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                if (point.Usd == null && prices.TryGetValue(point.Time, out double price) &&
                    EstimateOi(market, point.Value, price) is { } usd)
                    points[i] = point with { Usd = usd, Estimated = true };
            }
        }
        catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
        {
            // Native open interest stays available while the price source is unavailable.
        }
    }

    private async Task<Dictionary<long, double>> BitgetInterestMark(Market market, long time, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(market.Symbol);
        await PaceHistory(market.Venue, ct);
        var root = await Get(Mix + $"symbol-price?productType={market.Contract!.Product}&symbol={symbol}", ct);
        foreach (var row in Envelope(root, "00000").EnumerateArray())
            if (row.Optional("symbol") == market.Symbol && Math.Abs(row.Number("ts") - time) <= 60_000)
                return new() { [time] = row.Number("markPrice") };
        return [];
    }

    private async Task<Dictionary<long, double>> InterestMarks(Market market, int period, long from, long to, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(market.Symbol);
        int count = (int)((to - from) / (period * 1000L)) + 1;
        if (market.Venue == Venue.KuCoin)
        {
            // KuCoin publishes the size in contracts without historical notional. Use the same contract's trade history.
            var candles = await CandlePage(market, period, to + 1, count, ct);
            return candles.ToDictionary(candle => candle.Time, candle => candle.Open);
        }

        await PaceHistory(market.Venue, ct);
        if (market.Venue == Venue.Bybit)
        {
            string interval = period == 86400 ? "D" : (period / 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var data = BybitResult(await Get(Bybit + $"mark-price-kline?category=linear&symbol={symbol}" +
                $"&interval={interval}&start={from}&end={to}&limit={Math.Min(200, count)}", ct));
            if (data.Optional("symbol") != market.Symbol || data.Optional("category") != "linear") return [];
            return data.GetProperty("list").EnumerateArray().Where(row => row.GetArrayLength() >= 2)
                .DistinctBy(row => row[0].Int()).ToDictionary(row => row[0].Int(), row => row[1].Num());
        }

        string bar = period switch { 300 => "5m", 900 => "15m", 3600 => "1h", 14400 => "4h", _ => "1d" };
        var root = await Get($"https://futures.kraken.com/api/charts/v1/mark/{symbol}/{bar}?from={from / 1000}&to={to / 1000}&count={count}", ct);
        return root.GetProperty("candles").EnumerateArray().DistinctBy(row => row.GetProperty("time").Int())
            .ToDictionary(row => row.GetProperty("time").Int(), row => row.Number("open"));
    }
}
