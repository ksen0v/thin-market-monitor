using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    public static bool GateExecutedLiquidationsSupported(Market market) =>
        market.Venue == Venue.Gate && market.Contract?.Product is "futures/usdt" or "futures/btc" or "futures/usd1";

    public async Task<ChartExecution[]> GateExecutedLiquidations(Market market, CancellationToken ct)
    {
        if (!GateExecutedLiquidationsSupported(market)) return [];
        long now = CandleNow / 1000;
        await PaceHistory(Venue.Gate, ct);
        string symbol = Uri.EscapeDataString(market.Symbol);
        var root = await Get(GateContract + $"{market.Contract!.Product}/liq_orders?contract={symbol}&from={now - 3590}&to={now}&limit=100", ct);
        return DecodeGateExecutions(market, root);
    }

    internal static ChartExecution[] DecodeGateExecutions(Market market, JsonElement root) =>
        root.EnumerateArray().Where(row => row.Optional("contract") == market.Symbol).Select(row =>
        {
            double filledPrice = row.Number("fill_price"), direction = row.Number("order_size");
            long time = (long)row.Number("time") * 1000;
            LiquidationSide side = direction < 0 ? LiquidationSide.Long : direction > 0 ? LiquidationSide.Short : LiquidationSide.Unknown;
            // Gate publishes a per-order taker average, but no authoritative filled size in this public response.
            string id = FormattableString.Invariant($"{time}:{filledPrice:R}:{direction:R}:{row.Optional("order_price")}:{row.Optional("size")}");
            return new ChartExecution(time, filledPrice, 1, id, side);
        }).Where(fill => fill.Time > 0 && fill.Price > 0 && double.IsFinite(fill.Price)).ToArray();
}
