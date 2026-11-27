using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

public partial class MainWindow
{
    private readonly PriceAlertBook priceAlerts;
    private readonly bool monitorPriceAlerts;
    private readonly Dictionary<string, Market> knownAlertMarkets = new(StringComparer.Ordinal);
    private MarketStreams? alertStreams;
    private Market[] alertMarkets = [];
    private bool pollingAlertTrades;
    private long nextAlertPoll;

    private void InitializePriceAlertButtons()
    {
        PriceAlertsButton.ToolTip = "Ценовые оповещения";
        PriceAlertColibriButton.Content = Terminal.AssetLogo("colibri", 17);
        ToolTipService.SetShowOnDisabled(PriceAlertColibriButton, true);
    }

    private void OpenPriceAlerts(object sender, RoutedEventArgs e) => ShowSettings(openPriceAlerts: true);

    private void OpenLatestPriceAlert(object sender, RoutedEventArgs e)
    {
        if (PriceAlertColibriButton.Tag is PriceAlert alert) OpenPriceAlertInColibri(alert);
    }

    private void OpenPriceAlertInColibri(PriceAlert alert)
    {
        if (ResolveAlertMarket(alert) is { } market) colibri.Open(market);
    }

    private void PlacePriceAlert(Market market, double price, double? reference)
    {
        if (!priceAlerts.Add(market, price, reference, api.CandleNow)) return;
        knownAlertMarkets[market.Key] = market;
        DrainPriceAlerts();
    }

    private void SetPriceAlertEnabled(Guid id, bool enabled)
    {
        priceAlerts.SetEnabled(id, enabled, api.CandleNow);
        DrainPriceAlerts();
    }

    private void RemovePriceAlert(Guid id)
    {
        priceAlerts.Remove(id);
        DrainPriceAlerts();
    }

    private Market? ResolveAlertMarket(PriceAlert alert)
    {
        if (catalogs.TryGetValue(alert.Source, out var catalog) && catalog.Error == null)
            return catalog.Markets.FirstOrDefault(market => market.Key == alert.MarketKey);
        return knownAlertMarkets.GetValueOrDefault(alert.MarketKey);
    }

    private void DrainPriceAlerts()
    {
        if (priceAlerts.Drain() is not { } batch) return;
        settings.PriceAlerts = batch.Levels;
        foreach (var hit in batch.Hits) knownAlertMarkets[hit.Market.Key] = hit.Market;
        foreach (string key in knownAlertMarkets.Keys.Where(key => !batch.Levels.Any(alert => alert.MarketKey == key)).ToArray())
            knownAlertMarkets.Remove(key);
        RefreshPriceAlertMarkets();
        UpdatePriceAlertLines();
        settingsPanel?.RefreshPriceAlertRows();
        Save();
        foreach (var hit in batch.Hits) notifications.PublishPriceAlert(hit);
    }

    private void RefreshPriceAlertMarkets()
    {
        RefreshPriceAlertButtons();
        if (!monitorPriceAlerts || stop.IsCancellationRequested) return;
        alertMarkets = settings.PriceAlerts.Where(alert => alert.Active).DistinctBy(alert => alert.MarketKey)
            .Select(ResolveAlertMarket).OfType<Market>().ToArray();
        var shared = streams == null || alertMarkets.Length == 0 ? null :
            candidates.Select(market => market.Key).ToHashSet(StringComparer.Ordinal);
        var extra = alertMarkets.Where(market => market.Streaming && shared?.Contains(market.Key) != true).ToArray();
        if (extra.Length == 0)
        {
            alertStreams?.Dispose();
            alertStreams = null;
        }
        else if (alertStreams != null) alertStreams.UpdateMarkets(extra);
        else alertStreams = new(api, extra, [], [], null, tradeObserved: priceAlerts.Observe);
    }

    private void RefreshPriceAlertButtons()
    {
        var last = settings.PriceAlerts.Where(alert => alert.TriggeredAt.HasValue).MaxBy(alert => alert.TriggeredAt);
        string label = last is { ReachedPrice: { } reached } ? $"{last.Ticker} · {PriceAlert.Format(reached)}" : "Оповещения";
        if (!Equals(PriceAlertsButton.Tag, label))
        {
            PriceAlertsButton.Tag = label;
            PriceAlertsButton.Content = Terminal.LabelWithIcon("bell", label, 5);
        }
        PriceAlertsButton.Visibility = GridSpace.Visibility;
        PriceAlertsButton.Foreground = Terminal.Brush(last == null ? "Text" : "Accent");
        PriceAlertColibriButton.Tag = last;
        PriceAlertColibriButton.Visibility = last != null && settings.ColibriEnabled && GridSpace.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        PriceAlertColibriButton.IsEnabled = last != null && ResolveAlertMarket(last) != null;
        string title = last == null ? "Открыть в Colibri" : $"Открыть {last.Ticker} · {last.Source.Label} в Colibri";
        AutomationProperties.SetName(PriceAlertColibriButton, title);
        PriceAlertColibriButton.ToolTip = PriceAlertColibriButton.IsEnabled ? title : "Рынок ожидает каталог биржи";
    }

    private void UpdatePriceAlertLines()
    {
        foreach (var cell in cells.Append(overviewChart))
            cell.Plot.SetPriceAlertLevels(cell.Instrument is { } market ? AlertLevels(market) : []);
    }

    private double[] AlertLevels(Market market)
    {
        string key = market.Key;
        return settings.PriceAlerts.Where(alert => alert.Active && alert.MarketKey == key).Select(alert => alert.Price).ToArray();
    }

    private async Task PollPriceAlertTrades()
    {
        long tick = Environment.TickCount64;
        if (pollingAlertTrades || tick < nextAlertPoll || stop.IsCancellationRequested) return;
        var targets = alertMarkets.Where(market => !market.Streaming).ToArray();
        if (targets.Length == 0) return;
        nextAlertPoll = tick + 5000;
        pollingAlertTrades = true;
        try
        {
            await Task.WhenAll(targets.Select(async market =>
            {
                try
                {
                    var trades = await api.RecentTrades(market, stop.Token);
                    if (stop.IsCancellationRequested) return;
                    long now = api.CandleNow;
                    foreach (var trade in trades) priceAlerts.Observe(market, trade, now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!stop.IsCancellationRequested)
                        System.Diagnostics.Trace.TraceWarning("Price alert trades unavailable for {0}: {1}", market.Key, ex.Message);
                }
            }));
        }
        finally { pollingAlertTrades = false; }
    }
}
