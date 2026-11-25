using System.Windows;

namespace ThinMarketMonitor;

public partial class MainWindow
{
    private readonly DensityMonitor densityMonitor;
    private readonly DensityPanel densityPanel;

    private void OpenDensityListings() =>
        ShowListingRules("Листинги плотностей", settings.Densities.Rule, settings.Densities.CycleListingRule, DensitySettingsChanged);

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
        bool showSignals = settings.Densities.SignalsOverlay;
        foreach (var cell in cells)
            cell.Plot.SetDensityLevels(showSignals && cell.IsVisible ? densityMonitor.ForMarket(cell.Instrument) : []);
        overviewChart.Plot.SetDensityLevels(settings.Densities.OverviewOverlay && overviewChart.IsVisible
            ? densityMonitor.ForMarket(overviewChart.Instrument) : []);
        if (!settings.DensityWorkspace || fullscreenCell != null) return;

        var pending = densityMonitor.PendingCatalogs
            .Select(source => (Source: source, Error: catalogs.GetValueOrDefault(source)?.Error))
            .ToArray();
        densityPanel.Update(densityMonitor.Visible(), densityMonitor.Status(), pending, settings.DebugMode);
    }
}
