using System.Windows;

namespace ThinMarketMonitor;

public partial class MainWindow
{
    private readonly DensityMonitor densityMonitor;
    private readonly DensityPanel densityPanel;

    private void BlockDensityCoin(Market market)
    {
        if (settings.Densities.Block(market)) DensitySettingsChanged();
    }

    private IEnumerable<Market> DensityCoinMarkets() => catalogs.Where(entry => entry.Value.Error == null)
        .OrderByDescending(entry => settings.Densities.Scans(entry.Key))
        .SelectMany(entry => entry.Value.Markets.Where(ExchangeApi.SupportsDensity));

    private void DensitySettingsChanged()
    {
        densityMonitor.Configure(settings.Densities, catalogs);
        notifications.SettingsChanged();
        RefreshDensities();
        Save();
        if (CatalogsToRetry().Length > 0) nextRetry = DateTimeOffset.MinValue;
    }

    private void RefreshDensities()
    {
        var options = settings.Densities;
        bool showBoard = settings.DensityWorkspace && fullscreenCell == null;
        var charts = cells.Append(overviewChart).Select(cell => (Cell: cell, Market: OverlayMarket(cell))).ToArray();
        var assets = charts.Select(chart => chart.Market?.ListingAsset).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var levels = showBoard || assets.Count != 0 ? densityMonitor.Visible(showBoard ? null : assets) : [];
        var chartLevels = levels.Where(level => assets.Contains(level.Market.ListingAsset))
            .ToLookup(level => level.Market.ListingAsset, StringComparer.Ordinal);
        foreach (var (cell, market) in charts)
            cell.Plot.SetDensityLevels(market == null ? [] : DensityChartLevel.ForMarket(market, chartLevels[market.ListingAsset]));
        if (!showBoard) return;

        var pending = densityMonitor.PendingCatalogs
            .Select(source => (Source: source, Error: catalogs.GetValueOrDefault(source)?.Error))
            .ToArray();
        densityPanel.Update(levels, densityMonitor.Status(), pending, settings.DebugMode);

        Market? OverlayMarket(ChartCell cell) =>
            cell.IsVisible && (ReferenceEquals(cell, overviewChart) ? options.OverviewOverlay : options.SignalsOverlay)
                ? cell.Instrument : null;
    }
}
