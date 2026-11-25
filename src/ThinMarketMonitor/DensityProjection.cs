namespace ThinMarketMonitor;

internal readonly record struct DensityChartLevel(DensityLevel Level, double Price)
{
    internal static DensityChartLevel[] ForMarket(Market chart, IEnumerable<DensityLevel> levels)
    {
        int chartUnits = ExchangeApi.PriceDenomination(chart);
        bool trustedQuote = chart.Identifiers?.TrustedUsdQuote != false;
        var result = new List<DensityChartLevel>();
        foreach (var level in levels)
        {
            var source = level.Market;
            if (source.ListingAsset != chart.ListingAsset) continue;
            double price = level.Key.Price;
            if (source.Key != chart.Key)
            {
                if (!trustedQuote || source.Identifiers?.TrustedUsdQuote == false) continue;
                // Dollar-denominated quotes use the same USD equivalence as the screener's turnover.
                if (source.Quote != chart.Quote && !(UsdQuotes.Supported(source.Quote) && UsdQuotes.Supported(chart.Quote))) continue;
                price *= (double)chartUnits / ExchangeApi.PriceDenomination(source);
            }
            if (price > 0 && double.IsFinite(price)) result.Add(new(level, price));
        }
        return result.ToArray();
    }
}
