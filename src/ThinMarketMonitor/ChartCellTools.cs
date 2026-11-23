using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed partial class ChartCell
{
    private ChartFeatures? features;
    private Button? drawingTools, indicatorTools;
    private readonly TextBlock indicatorHint = new()
    {
        Height = 15, FontSize = 10, Margin = new Thickness(7, 1, 7, 0),
        TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed
    };
    private ChartIndicatorOptions indicatorOptions = ChartIndicatorOptions.Off;
    private CancellationTokenSource? indicatorRun;
    private string? indicatorMarket;
    private int indicatorFrame;
    private readonly Dictionary<Venue, Market[]> interestCandidates = [];
    private readonly Dictionary<Venue, InterestLayer> interestLayers = [];
    private ExecutionTape? tradeTape, liquidationTape;
    private long profileSince;
    private string profileStatus = "";
    private string liquidationStatus = "";
    internal bool HasIndicatorLayout => indicatorHint.Visibility == Visibility.Visible;
    internal event Action? IndicatorLayoutChanged;

    private void ConfigureChartTools(DockPanel bar, ChartFeatures? shared)
    {
        if (shared == null) return;
        features = shared;
        indicatorHint.Foreground = Terminal.Brush("Muted");
        plot.Magnet = () => shared.Magnet;
        plot.DrawingsChanged += values => { if (market != null) shared.SetDrawings(market.Key, values); };
        shared.DrawingsChanged += key => { if (market?.Key == key) plot.SetDrawings(shared.Drawings(key)); };
        shared.IndicatorsChanged += key => { if (market?.Key == key) ResetChartTools(); };
        plot.IndicatorSummary += (shortText, detailText) =>
        {
            if (indicatorHint.Text == shortText && Equals(indicatorHint.ToolTip, detailText)) return;
            indicatorHint.Text = shortText;
            indicatorHint.ToolTip = detailText;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) plot.CancelDrawing();
            ResetChartTools();
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        indicatorTools = Terminal.IconButton("volume", "Индикаторы графика");
        drawingTools = Terminal.IconButton("draw", "Инструменты рисования");
        foreach (var button in new[] { drawingTools, indicatorTools })
        {
            button.MinHeight = 21;
            button.Padding = new Thickness(5, 1, 5, 1);
            tools.Children.Add(button);
        }
        drawingTools.Click += (_, _) => OpenTools(drawingTools, DrawingMenu());
        indicatorTools.Click += (_, _) => OpenTools(indicatorTools, IndicatorMenu());
    }

    private IEnumerable<MenuItem> DrawingMenu()
    {
        foreach (var (kind, label) in new[]
        {
            (DrawingKind.HorizontalRay, "Горизонтальный луч"),
            (DrawingKind.Ray, "Наклонный луч"),
            (DrawingKind.Segment, "Отрезок")
        })
            yield return Item(label, () => plot.BeginDrawing(kind), hint: "Поставьте точки на графике. Удерживайте клавишу магнита для привязки к свече.");
        yield return Item("Отменить построение · Esc", () => plot.CancelDrawing(), enabled: plot.IsDrawing);
        yield return Item("Удалить выбранный рисунок · Delete", () => plot.DeleteDrawing(), enabled: plot.HasSelectedDrawing);
    }

    private IEnumerable<MenuItem> IndicatorMenu()
    {
        if (market is not { } instrument || features == null) yield break;
        string key = instrument.Key;
        yield return Item("Объём свечей", () => Change(o => o with { Volume = !o.Volume }), indicatorOptions.Volume);
        yield return Item("Профиль по сделкам", () => Change(o => o with { Profile = !o.Profile }), indicatorOptions.Profile,
            hint: "Фактические сделки в видимом диапазоне. Доступная выборка может быть неполной.");
        yield return Item("Фактические ликвидации", () => Change(o => o with { Liquidations = !o.Liquidations }),
            indicatorOptions.Liquidations, hint: LiquidationAvailability(instrument) ?? "Биржа публикует цены исполнений.");

        yield return new MenuItem { Header = "Открытый интерес · источники", IsEnabled = false, Style = Terminal.Style("TerminalMenuItem") };
        foreach (var venue in Enum.GetValues<Venue>().Where(venue => venue != Venue.Alpha))
        {
            var layer = interestLayers.GetValueOrDefault(venue);
            bool chosen = indicatorOptions.Interest?.ContainsKey(venue) == true;
            string label = layer?.Market == null ? venue.Label() : $"{venue.Label()} · {layer.Market.Ticker}";
            string? status = layer?.Status ?? layer?.Reading?.Status;
            if (chosen && status != null) label += $" · {status}";
            yield return Item(label, () => SetInterest(venue, null), chosen,
                hint: layer?.Reading is { } reading ? $"{reading.Unit} · {PeriodLabel(reading.Period)}" : status);
            if (!chosen || interestCandidates.GetValueOrDefault(venue) is not { Length: > 1 } choices) continue;
            foreach (var candidate in choices)
                yield return Item($"    {candidate.Symbol} · {candidate.Contract!.Product}", () => SetInterest(venue, candidate.Key),
                    indicatorOptions.Interest?.GetValueOrDefault(venue) == candidate.Key,
                    hint: $"{candidate.Base}/{candidate.Quote} · {candidate.Source.Label}");
        }

        void Change(Func<ChartIndicatorOptions, ChartIndicatorOptions> change)
        {
            if (market?.Key == key) features.SetOptions(key, change(indicatorOptions));
        }
        void SetInterest(Venue venue, string? contract)
        {
            var sources = new Dictionary<Venue, string>(indicatorOptions.Interest ?? []);
            if (contract != null) sources[venue] = contract;
            else if (!sources.Remove(venue)) sources[venue] = "";
            Change(options => options with { Interest = sources });
        }
    }

    private static MenuItem Item(string caption, Action action, bool marked = false, bool enabled = true, string? hint = null)
    {
        var item = new MenuItem { Header = caption, Style = Terminal.Style("TerminalMenuItem"), IsChecked = marked,
            IsEnabled = enabled, ToolTip = hint };
        item.Click += (_, _) => action();
        return item;
    }

    private static void OpenTools(Button button, IEnumerable<MenuItem> items)
    {
        var menu = new ContextMenu { Style = Terminal.Style("TerminalMenu"), PlacementTarget = button, MinWidth = 206 };
        foreach (var item in items) menu.Items.Add(item);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void UpdateChartToolState()
    {
        if (drawingTools == null || indicatorTools == null) return;
        drawingTools.IsEnabled = indicatorTools.IsEnabled = market != null;
        indicatorTools.Foreground = Terminal.Brush(indicatorOptions.Empty ? "Text" : "Accent");
        AutomationProperties.SetItemStatus(indicatorTools, indicatorOptions.Empty ? "" : "Индикаторы включены");
    }

    private void ResetChartTools()
    {
        if (features == null) return;
        indicatorRun?.Cancel();
        indicatorRun = null;
        if (market?.Key != indicatorMarket || frame != indicatorFrame)
        {
            indicatorMarket = market?.Key;
            indicatorFrame = frame;
            interestLayers.Clear();
            interestCandidates.Clear();
            tradeTape = liquidationTape = null;
            profileStatus = liquidationStatus = "";
        }
        indicatorOptions = market == null ? ChartIndicatorOptions.Off : features.Options(market.Key);
        plot.SetDrawings(market == null ? [] : features.Drawings(market.Key));
        UpdateChartToolState();
        if (!indicatorOptions.Profile) tradeTape = null;
        if (!indicatorOptions.Liquidations) liquidationTape = null;
        foreach (var venue in interestLayers.Keys.Where(venue => indicatorOptions.Interest?.ContainsKey(venue) != true).ToArray())
            interestLayers.Remove(venue);
        bool shown = active && IsVisible && market != null && !indicatorOptions.Empty;
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (indicatorHint.Visibility != visibility)
        {
            indicatorHint.Visibility = visibility;
            IndicatorLayoutChanged?.Invoke();
        }
        if (!shown || market is not { } instrument)
        {
            plot.UpdateIndicators(null);
            return;
        }
        foreach (var venue in indicatorOptions.Interest?.Keys.AsEnumerable() ?? [])
            interestLayers.TryAdd(venue, new(venue, null, null, "Загрузка…"));
        if (indicatorOptions.Profile && tradeTape == null)
        {
            profileSince = api.CandleNow;
            tradeTape = new();
            profileStatus = "Сделки · выборка REST и поток";
        }
        if (indicatorOptions.Liquidations)
        {
            liquidationTape ??= new();
            liquidationStatus = LiquidationAvailability(instrument) ?? "Подключение данных ликвидаций…";
        }
        PublishIndicators(instrument);
        if (!indicatorOptions.Profile && !indicatorOptions.Liquidations && !indicatorOptions.HasInterest) return;
        var run = indicatorRun = new();
        _ = RunIndicators(instrument, indicatorOptions, frame, run);
    }

    private async Task RunIndicators(Market instrument, ChartIndicatorOptions options, int tf, CancellationTokenSource run)
    {
        var ct = run.Token;
        var jobs = new List<Task>();
        foreach (var (venue, choice) in options.Interest ?? []) jobs.Add(ObserveInterest(venue, choice));
        if (options.Profile) jobs.Add(InitialTrades());
        if (options.Liquidations && instrument is { Contract: not null, Venue: Venue.Kraken or Venue.Lighter })
            jobs.Add(ObserveLiquidations());
        if (options.Liquidations && ExchangeApi.GateExecutedLiquidationsSupported(instrument)) jobs.Add(ObserveGateLiquidations());
        if (options.Liquidations && BinanceLiquidations.Supports(instrument)) jobs.Add(StreamLiquidations());
        try { await Task.WhenAll(jobs); }
        catch (Exception error) when (ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error)) { }
        finally
        {
            if (ReferenceEquals(indicatorRun, run)) indicatorRun = null;
            run.Dispose();
        }

        async Task ObserveInterest(Venue venue, string choice)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var catalog = instrument.Contract != null && instrument.Venue == venue
                        ? new Catalog([instrument]) : await features!.Futures(venue, ct);
                    ct.ThrowIfCancellationRequested();
                    var candidates = ChartFeatures.ComparableContracts(instrument, venue, catalog.Markets);
                    interestCandidates[venue] = candidates;
                    var found = choice.Length > 0 ? candidates.FirstOrDefault(m => m.Key == choice) :
                        candidates.Length == 1 ? candidates[0] : null;
                    if (found == null)
                    {
                        string status = catalog.Error != null ? "Каталог недоступен" : candidates.Length > 1 ? "Выберите контракт" :
                            "Подтверждённого контракта нет";
                        interestLayers[venue] = new(venue, null, null, status);
                    }
                    else
                    {
                        var reading = await api.OpenInterest(found, tf, ct);
                        ct.ThrowIfCancellationRequested();
                        interestLayers[venue] = new(venue, found, reading);
                    }
                }
                catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
                {
                    interestLayers[venue] = new(venue, null, null, "Данные недоступны");
                }
                PublishIndicators(instrument);
                await Task.Delay(55_000, ct);
            }
        }

        async Task InitialTrades()
        {
            try
            {
                var trades = await features!.Recent(instrument, ct);
                ct.ThrowIfCancellationRequested();
                foreach (var trade in trades.Where(trade => trade.Time < profileSince)) AddExecution(instrument, trade, tradeTape);
                if (trades.All(trade => !double.IsFinite(ChartVolume.TradeAmount(instrument, trade))))
                    profileStatus = "Профиль · размер недавних сделок недоступен · только наблюдения потока";
            }
            catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
            { profileStatus = "Профиль · история недоступна · только наблюдения потока"; }
            PublishIndicators(instrument);
        }

        async Task ObserveLiquidations()
        {
            do
            {
                try
                {
                    var trades = await features!.Recent(instrument, ct);
                    ct.ThrowIfCancellationRequested();
                    foreach (var trade in trades.Where(trade => trade.Liquidated)) AddExecution(instrument, trade, liquidationTape);
                    liquidationStatus = instrument.Venue == Venue.Kraken
                        ? "Kraken · последние 100 сделок · опрос 15 с · сторона не подтверждена"
                        : "Lighter · недавние сделки и поток · сторона не подтверждена";
                }
                catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
                { liquidationStatus = "Недавние сделки недоступны · охват неполный"; }
                PublishIndicators(instrument);
                if (instrument.Venue != Venue.Kraken) break;
                await Task.Delay(15_000, ct);
            } while (!ct.IsCancellationRequested);
        }

        async Task StreamLiquidations()
        {
            using var lease = features!.Liquidations.Watch(instrument,
                item => _ = Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    liquidationTape?.Add(item);
                    PublishIndicators(instrument);
                }, DispatcherPriority.Background),
                status => _ = Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    liquidationStatus = status + " · только одиночные исполнения · охват неполный";
                    PublishIndicators(instrument);
                }, DispatcherPriority.Background));
            await Task.Delay(Timeout.Infinite, ct);
        }

        async Task ObserveGateLiquidations()
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var events = await features!.GateLiquidations(instrument, ct);
                    ct.ThrowIfCancellationRequested();
                    foreach (var item in events) liquidationTape?.Add(item);
                    liquidationStatus = "Gate · средние цены исполнения заявок · до 100 событий за последний час · опрос 25 с";
                }
                catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
                { liquidationStatus = "Gate · история исполнений недоступна"; }
                PublishIndicators(instrument);
                await Task.Delay(25_000, ct);
            }
        }
    }

    private void ObserveChartTrade(Market instrument, Trade trade)
    {
        if (indicatorRun?.IsCancellationRequested == true || !IsVisible || instrument.Key != indicatorMarket) return;
        if (indicatorOptions.Profile && trade.Time >= profileSince) AddExecution(instrument, trade, tradeTape);
        if (indicatorOptions.Liquidations && trade.Liquidated) AddExecution(instrument, trade, liquidationTape);
    }

    private static void AddExecution(Market market, Trade trade, ExecutionTape? tape) =>
        tape?.Add(new(trade.Time, trade.Price, ChartVolume.TradeAmount(market, trade), trade.Id));

    private static string? LiquidationAvailability(Market instrument)
    {
        if (instrument.Contract == null) return "Карта исполнений доступна для фьючерсных графиков";
        if (BinanceLiquidations.Supports(instrument) || ExchangeApi.GateExecutedLiquidationsSupported(instrument) ||
            instrument.Venue is Venue.Kraken or Venue.Lighter) return null;
        return "Биржа не предоставляет подтверждённые цены исполнения для карты";
    }

    private void PublishIndicators(Market instrument)
    {
        if (market?.Key != instrument.Key || !IsVisible || !active) return;
        plot.UpdateIndicators(new(instrument, indicatorOptions, tradeTape, liquidationTape, profileStatus,
            liquidationStatus, interestLayers.Values.OrderBy(layer => layer.Venue).ToArray()));
    }

    internal static string PeriodLabel(int period) => period == 0 ? "с момента сбора" : "шаг " + MainWindow.WindowName(period);
}
