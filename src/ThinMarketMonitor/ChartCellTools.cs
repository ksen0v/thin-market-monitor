using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed partial class ChartCell
{
    private ChartFeatures? features;
    private Button? drawingTools, indicatorTools;
    private Border? drawingToolbar;
    private readonly Dictionary<DrawingKind, Button> drawingActions = [];
    private bool drawingToolbarDefault;
    private bool? drawingToolbarBeforeFullscreen;
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
    private readonly DispatcherTimer interestViewportTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
    private TaskCompletionSource interestViewportChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InterestWindow? interestWindow;
    private ExecutionTape? tradeTape, liquidationTape;
    private long profileSince;
    private string profileStatus = "";
    private string liquidationStatus = "";
    internal int MinimumGridHeight { get; private set; } = 116;
    internal event Action? IndicatorLayoutChanged;

    private void ConfigureChartTools(DockPanel bar, DockPanel body, ChartFeatures? shared, bool showDrawingTools)
    {
        if (shared == null) return;
        features = shared;
        drawingToolbarDefault = showDrawingTools;
        indicatorHint.Foreground = Terminal.Brush("Muted");
        plot.Magnet = () => shared.Magnet;
        plot.DrawingsChanged += values => { if (market != null) shared.SetDrawings(market.Key, values); };
        shared.DrawingsChanged += key => { if (market?.Key == key) plot.SetDrawings(shared.Drawings(key)); };
        shared.IndicatorsChanged += key => { if (market?.Key == key) ResetChartTools(); };
        plot.IndicatorSummary += (shortText, detailText) =>
        {
            indicatorHint.Text = shortText;
            indicatorHint.ToolTip = detailText;
            if (indicatorTools != null) indicatorTools.ToolTip = detailText;
        };
        plot.InterestRangeChanged += range =>
        {
            interestWindow = range;
            interestViewportTimer.Stop();
            interestViewportTimer.Start();
        };
        interestViewportTimer.Tick += (_, _) =>
        {
            interestViewportTimer.Stop();
            var waiting = interestViewportChanged;
            interestViewportChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.TrySetResult();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) plot.CancelDrawing();
            ResetChartTools();
        };

        indicatorTools = Terminal.IconButton("volume", "Индикаторы графика");
        drawingTools = Terminal.IconButton("draw", "Панель рисования");
        foreach (var button in new[] { drawingTools, indicatorTools })
        {
            button.MinHeight = 21;
            button.Padding = new Thickness(5, 1, 5, 1);
            DockPanel.SetDock(button, button == drawingTools ? Dock.Left : Dock.Right);
            bar.Children.Add(button);
        }
        var drawingColumn = new StackPanel { Orientation = Orientation.Vertical };
        drawingToolbar = Terminal.Rule(new Thickness(1));
        drawingToolbar.CornerRadius = new CornerRadius(10);
        drawingToolbar.VerticalAlignment = VerticalAlignment.Center;
        drawingToolbar.Margin = new Thickness(4, 0, 4, 0);
        drawingToolbar.Padding = new Thickness(3);
        drawingToolbar.Child = drawingColumn;
        DockPanel.SetDock(drawingToolbar, Dock.Left);
        body.Children.Add(drawingToolbar);
        AutomationProperties.SetName(drawingToolbar, "Инструменты рисования");
        foreach (var (kind, icon, label) in new[]
        {
            (DrawingKind.HorizontalRay, "horizontal-ray", "Горизонтальный луч"),
            (DrawingKind.Ray, "trend-ray", "Наклонный луч"),
            (DrawingKind.Segment, "segment", "Отрезок")
        })
        {
            var button = Terminal.IconButton(icon, label);
            button.Width = 26;
            button.Height = 25;
            button.Margin = new Thickness(0, 0, 0, 2);
            button.Click += (_, _) =>
            {
                if (plot.CurrentDrawingTool == kind) plot.CancelDrawing();
                else plot.BeginDrawing(kind);
            };
            drawingActions.Add(kind, button);
            drawingColumn.Children.Add(button);
        }
        drawingTools.Click += (_, _) => SetDrawingToolbar(!DrawingToolbarExpanded);
        indicatorTools.Click += (_, _) => OpenTools(indicatorTools, IndicatorMenu());
        plot.DrawingToolChanged += UpdateDrawingButtons;
        SetDrawingToolbar(showDrawingTools);
    }

    private bool DrawingToolbarExpanded => drawingToolbar?.Visibility == Visibility.Visible;

    internal void ResetDrawingToolbar() => SetDrawingToolbar(drawingToolbarDefault);

    internal void SetFullscreenDrawingToolbar(bool fullscreen)
    {
        if (fullscreen)
        {
            drawingToolbarBeforeFullscreen = DrawingToolbarExpanded;
            SetDrawingToolbar(true);
        }
        else if (drawingToolbarBeforeFullscreen is { } previous)
        {
            drawingToolbarBeforeFullscreen = null;
            SetDrawingToolbar(previous);
        }
    }

    private void SetDrawingToolbar(bool show)
    {
        if (drawingToolbar == null || drawingTools == null) return;
        if (!show) plot.CancelDrawing();
        drawingToolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        drawingTools.Foreground = Terminal.Brush(show ? "Accent" : "Muted");
        drawingTools.ToolTip = show ? "Свернуть инструменты рисования" : "Развернуть инструменты рисования";
        AutomationProperties.SetItemStatus(drawingTools, show ? "Развёрнута" : "Свёрнута");
    }

    private void UpdateDrawingButtons()
    {
        foreach (var (kind, button) in drawingActions)
        {
            bool active = plot.CurrentDrawingTool == kind;
            button.Foreground = Terminal.Brush(active ? "Accent" : "Text");
            button.Background = active ? Terminal.Brush("AccentSurface") : System.Windows.Media.Brushes.Transparent;
            AutomationProperties.SetItemStatus(button, active ? "Активный инструмент" : "");
        }
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

        yield return Item("Открытый интерес в USD", () => Change(o => o with { InterestUsd = !o.InterestUsd }),
            indicatorOptions.InterestUsd, hint: "Единицы для шкал OI и линейки");
        yield return new MenuItem { Header = "Открытый интерес · источники", IsEnabled = false, Style = Terminal.Style("TerminalMenuItem") };
        foreach (var venue in Enum.GetValues<Venue>().Where(venue => venue != Venue.Alpha))
        {
            var layer = interestLayers.GetValueOrDefault(venue);
            bool chosen = indicatorOptions.Interest?.ContainsKey(venue) == true;
            string label = layer?.Market == null ? venue.Label() : $"{venue.Label()} · {layer.Market.Symbol}";
            string? status = layer?.Status ?? layer?.Reading?.Status;
            if (chosen && status != null) label += $" · {status}";
            yield return Item(label, () => SetInterest(venue, null), chosen,
                hint: layer?.Reading is { } reading ? $"{reading.Unit} · {PeriodLabel(reading.Period)}" : status);
            if (!chosen || interestCandidates.GetValueOrDefault(venue) is not { Length: > 0 } choices ||
                (choices.Length == 1 && layer?.Market?.Key == choices[0].Key)) continue;
            foreach (var candidate in choices)
                yield return Item($"    {candidate.Symbol} · {candidate.Contract!.Product}", () => SetInterest(venue, candidate.Key),
                    indicatorOptions.Interest?.GetValueOrDefault(venue) == candidate.Key,
                    hint: $"{candidate.Base}/{candidate.Quote} · {(candidate.Contract!.Unit == QuantityUnit.Quote ? "инверсный" : "линейный")}");
        }

        void Change(Func<ChartIndicatorOptions, ChartIndicatorOptions> change)
        {
            if (market?.Key == key) features.SetOptions(key, change(indicatorOptions));
        }
        void SetInterest(Venue venue, string? contract)
        {
            var sources = new Dictionary<Venue, string>(indicatorOptions.Interest ?? []);
            if (contract != null) sources[venue] = contract;
            else if (!sources.Remove(venue))
            {
                sources[venue] = "";
                chartViewport.ScrollToTop();
            }
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
        indicatorTools.IsEnabled = market != null;
        foreach (var button in drawingActions.Values) button.IsEnabled = market != null;
        indicatorTools.Foreground = Terminal.Brush(indicatorOptions.HasDetails ? "Accent" : "Text");
        AutomationProperties.SetItemStatus(indicatorTools, indicatorOptions.Empty ? "" : "Индикаторы включены");
    }

    private void ResetChartTools()
    {
        if (features == null) return;
        indicatorRun?.Cancel();
        indicatorRun = null;
        if (market?.Key != indicatorMarket || frame != indicatorFrame)
        {
            interestWindow = null;
            interestViewportTimer.Stop();
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
        bool details = shown && indicatorOptions.HasDetails;
        indicatorHint.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        int minimum = !details ? 116 : indicatorOptions.HasInterest ? 186 : 165;
        if (MinimumGridHeight != minimum)
        {
            MinimumGridHeight = minimum;
            IndicatorLayoutChanged?.Invoke();
        }
        if (!shown || market is not { } instrument)
        {
            interestViewportTimer.Stop();
            if (indicatorTools != null) indicatorTools.ToolTip = "Индикаторы графика";
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
                Task change = interestViewportChanged.Task;
                bool loadingHistory = false;
                try
                {
                    var catalog = instrument.Contract != null && instrument.Venue == venue
                        ? new Catalog([instrument]) : await features!.Futures(venue, ct);
                    ct.ThrowIfCancellationRequested();
                    var candidates = ChartFeatures.ComparableContracts(instrument, venue, catalog.Markets);
                    interestCandidates[venue] = candidates;
                    var found = ChartFeatures.ChooseInterestContract(instrument, candidates, choice);
                    if (found == null)
                    {
                        string status = catalog.Error != null ? "Каталог недоступен" : candidates.Length > 0 ? "Выберите контракт" :
                            "Подтверждённого контракта нет";
                        interestLayers[venue] = new(venue, null, null, status);
                    }
                    else
                    {
                        var reading = await api.OpenInterest(found, tf, ct, interestWindow);
                        ct.ThrowIfCancellationRequested();
                        loadingHistory = reading.LoadingHistory;
                        interestLayers[venue] = new(venue, found, reading);
                    }
                }
                catch (Exception error) when (!ct.IsCancellationRequested && ChartFeatures.ExpectedNetworkError(error))
                {
                    interestLayers[venue] = new(venue, null, null, "Данные недоступны");
                }
                PublishIndicators(instrument);
                try { await change.WaitAsync(TimeSpan.FromMilliseconds(loadingHistory ? 650 : 55_000), ct); }
                catch (TimeoutException) { }
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
        var panes = (indicatorOptions.Interest?.Keys.AsEnumerable() ?? []).Reverse()
            .Where(interestLayers.ContainsKey).Select(venue => interestLayers[venue]).ToArray();
        plot.UpdateIndicators(new(instrument, indicatorOptions, tradeTape, liquidationTape, profileStatus, liquidationStatus, panes));
    }

    internal static string PeriodLabel(int period) => period == 0 ? "с момента сбора" : "шаг " + MainWindow.WindowName(period);
}
