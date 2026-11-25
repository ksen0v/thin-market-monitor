using System.Windows;

namespace ThinMarketMonitor;

public partial class MainWindow
{
    private readonly DensityMonitor densityMonitor;
    private readonly DensityPanel densityPanel;

    private bool ScansDensity(MarketSource source) =>
        settings.Densities.Venues.TryGetValue(source.Venue, out var rule) && rule.Enabled(source.Segment);

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

        var pending = MarketSource.All.Where(ScansDensity)
            .Where(source => !catalogs.TryGetValue(source, out var catalog) || catalog.Error != null)
            .Select(source => $"{source.Label} · {catalogs.GetValueOrDefault(source)?.Error ?? "загрузка каталога"}")
            .ToArray();
        densityPanel.Update(densityMonitor.Visible(), densityMonitor.Status(), pending);
    }
}
