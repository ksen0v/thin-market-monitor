using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel : Border, IDisposable
{
    private const int FiltersPage = 0, ModulesPage = 1, PricePage = 2, VolumePage = 3, ChartsPage = 4,
        OverviewPage = 5, IntegrationsPage = 6, AboutPage = 7, BlacklistPage = 8, NotificationsPage = 9, InterfacePage = 10,
        DensityPage = 11, DensityBlacklistPage = 12, DensityCoinsPage = 13, PriceAlertsPage = 14, SoundsPage = 15;
    private const int PageCount = 16;
    private const double StandardHeight = 592;
    private readonly CancellationTokenSource stop = new();
    private readonly Grid layout = new();
    private readonly Button[] navigation = new Button[PageCount];
    private readonly Border[] indicators = new Border[PageCount];
    private readonly FrameworkElement[] pages = new FrameworkElement[PageCount];
    private readonly List<TextBlock> diagnosticCopy = [];
    private readonly TextBlock result = Terminal.Copy("");
    private readonly TextBlock updateStatus = Terminal.Copy($"v{Updater.Current}");
    private readonly Button check = new() { Content = "Проверить версию" };
    private readonly Button install = new() { Content = "Обновить и перезапустить", Visibility = Visibility.Collapsed };
    private readonly Settings settings;
    private readonly Updater updater;
    private readonly ColibriBridge colibri;
    private readonly TextBlock colibriStatus = Terminal.Copy("");
    private readonly TextBlock voiceStatus = Terminal.Copy("");
    private readonly Action<bool> changed;
    private readonly Action savePreferences;
    private readonly Action saveNotifications;
    private readonly Action saveOverview;
    private readonly Action saveDiagnostics;
    private readonly Action<SignalTone, int> previewSound;
    private readonly RussianVoicePack? neuralVoice;
    private readonly Action<string> previewVoice;
    private readonly Action close;
    private readonly Action restart;
    private Release? update;
    private bool checkedOnOpen;
    private bool downloadingVoice;
    private int currentPage;
    private SpeechTemplateEditor? speechEditor;
    public bool Installing { get; private set; }

    public SettingsPanel(Settings settings, Updater updater, ColibriBridge colibri,
        Action<bool> changed, Action close, Action restart, bool overview = false, Action? saveNotifications = null,
        Action? saveOverview = null, Action<SignalTone, int>? previewSound = null, RussianVoicePack? neuralVoice = null,
        Action<string>? previewVoice = null, Action? savePreferences = null, Action? saveDiagnostics = null,
        Action? saveDensities = null, bool densities = false, Market? densityCoin = null,
        Func<IEnumerable<Market>>? densityMarkets = null, Action<Guid, bool>? setPriceAlert = null,
        Action<Guid>? removePriceAlert = null, Action<PriceAlert>? openPriceAlert = null, bool openPriceAlerts = false)
    {
        this.settings = settings;
        this.updater = updater;
        this.colibri = colibri;
        this.changed = changed;
        this.savePreferences = savePreferences ?? (() => changed(false));
        this.saveNotifications = saveNotifications ?? (() => changed(false));
        this.saveOverview = saveOverview ?? (() => changed(false));
        this.saveDiagnostics = saveDiagnostics ?? this.savePreferences;
        this.previewSound = previewSound ?? ((_, _) => { });
        this.neuralVoice = neuralVoice;
        this.previewVoice = previewVoice ?? (_ => { });
        this.close = close;
        this.restart = restart;
        Width = 664;
        Height = StandardHeight;
        VerticalAlignment = VerticalAlignment.Center;
        Background = Terminal.Brush("Shell");
        BorderBrush = Terminal.Brush("Outline");
        BorderThickness = new Thickness(1);
        AutomationProperties.SetName(this, "Настройки");
        Child = layout;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var header = Terminal.Rule(new Thickness(0, 0, 0, 1));
        header.Background = Terminal.Brush("Control");
        header.Child = Terminal.DialogHeader("Настройки", "Закрыть настройки", Close);
        layout.Children.Add(header);

        var body = new Grid { MaxHeight = 505 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(176) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var nav = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        AutomationProperties.SetName(nav, "Разделы настроек");
        var rail = Terminal.Rule(new Thickness(0, 0, 1, 0));
        rail.Child = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(rail);
        AddNavigationGroup(nav, "Сигналы",
            (FiltersPage, "Фильтры", "filter"), (BlacklistPage, "Чёрный список", "ban"),
            (ModulesPage, "Модули", "listings"), (PricePage, "Импульс цены", "activity"),
            (VolumePage, "Импульс объёма", "volume"));
        AddNavigationGroup(nav, "Обзор", (OverviewPage, "Инструменты", "overview-nav"));
        AddNavigationGroup(nav, "Плотности", (DensityPage, "Сканирование", "listings"),
            (DensityCoinsPage, "Настройки монет", "settings"), (DensityBlacklistPage, "Чёрный список", "ban"));
        AddNavigationGroup(nav, "Общие",
            (NotificationsPage, "Уведомления", "bell"), (PriceAlertsPage, "Уровни цены", "bell"),
            (SoundsPage, "Звуки", "volume"),
            (InterfacePage, "Интерфейс", "settings"), (ChartsPage, "Графики", "candles"),
            (IntegrationsPage, "Интеграции", "external"), (AboutPage, "О программе", "info"));
        var surface = new Border { Background = Terminal.Brush("Surface"), Padding = new Thickness(18, 16, 18, 16) };
        Grid.SetColumn(surface, 1);
        body.Children.Add(surface);
        var pageHost = new Grid();
        surface.Child = new ScrollViewer { Content = pageHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        pages[FiltersPage] = Rules(RuleKind.Filter);
        pages[ModulesPage] = Modules();
        pages[PricePage] = Rules(RuleKind.Price);
        pages[VolumePage] = Rules(RuleKind.Volume);
        pages[ChartsPage] = Shortcuts();
        pages[OverviewPage] = OverviewOptions();
        pages[IntegrationsPage] = Integrations();
        pages[AboutPage] = About();
        pages[BlacklistPage] = BlacklistOptions(densities: false, () => changed(false));
        pages[InterfacePage] = InterfaceOptions();
        var saveDensityOptions = saveDensities ?? this.savePreferences;
        pages[NotificationsPage] = NotificationOptions(saveDensityOptions);
        pages[SoundsPage] = SoundOptions();
        pages[DensityPage] = DensitySettings(saveDensityOptions);
        pages[DensityBlacklistPage] = BlacklistOptions(densities: true, saveDensityOptions);
        pages[DensityCoinsPage] = DensityCoinOptions(saveDensityOptions, densityCoin, densityMarkets);
        pages[PriceAlertsPage] = PriceAlertOptions(setPriceAlert, removePriceAlert, openPriceAlert);
        foreach (var page in pages) pageHost.Children.Add(page);

        var footer = Terminal.Rule(new Thickness(0, 1, 0, 0));
        var footerContent = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var done = new Button { Content = "Готово", Style = Terminal.Style("PrimaryButton") };
        done.Click += (_, _) =>
        {
            if (speechEditor?.TrySavePending() == false)
            {
                SelectPage(NotificationsPage);
                speechEditor.FocusTemplate();
                return;
            }
            Close();
        };
        DockPanel.SetDock(done, Dock.Right);
        footerContent.Children.Add(done);
        result.FontSize = 11;
        result.VerticalAlignment = VerticalAlignment.Center;
        result.Margin = new Thickness(0, 0, 8, 0);
        footerContent.Children.Add(result);
        footer.Child = footerContent;
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        UpdateModuleNavigation();
        int openingPage = openPriceAlerts ? PriceAlertsPage :
            densityCoin != null ? DensityCoinsPage : densities ? DensityPage : overview ? OverviewPage : FiltersPage;
        SelectPage(openingPage);
        UpdateDiagnostics();
        Loaded += (_, _) =>
        {
            navigation[openingPage].BringIntoView();
            navigation[openingPage].Focus();
        };
    }

    private void AddNavigationGroup(Panel nav, string title, params (int Index, string Text, string Icon)[] entries)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        AutomationProperties.SetName(group, title);
        group.Children.Add(new TextBlock
        {
            Text = title, Foreground = Terminal.Brush("Text"), FontSize = 11,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(9, 3, 0, 4)
        });
        foreach (var (index, text, icon) in entries) AddNavigation(group, index, text, icon);
        nav.Children.Add(group);
    }

    private void AddNavigation(Panel nav, int index, string text, string icon)
    {
        var marker = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        var button = new Button
        {
            Style = Terminal.Style("IconButton"), MinHeight = 30, Padding = new Thickness(9, 3, 9, 3),
            HorizontalContentAlignment = HorizontalAlignment.Left, Content = Terminal.LabelWithIcon(icon, text, 8)
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => SelectPage(index);
        row.Children.Add(button);
        row.Children.Add(marker);
        nav.Children.Add(row);
        navigation[index] = button;
        indicators[index] = marker;
    }

    private void SelectPage(int index)
    {
        EndCapture();
        currentPage = index;
        for (int i = 0; i < pages.Length; i++)
        {
            bool selected = i == index;
            pages[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            navigation[i].Background = selected ? Terminal.Brush("Control") : Brushes.Transparent;
            navigation[i].Foreground = Terminal.Brush(selected ? "Accent" : "Muted");
            indicators[i].Background = selected ? Terminal.Brush("Accent") : Brushes.Transparent;
            AutomationProperties.SetItemStatus(navigation[i], selected ? "Текущий раздел" : "");
        }
        if (index == AboutPage && !checkedOnOpen)
        {
            checkedOnOpen = true;
            _ = Check();
        }
        if (index is NotificationsPage or SoundsPage) _ = CheckVoice();
        UpdateFilterVisibility();
    }

    private StackPanel InterfaceOptions()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Интерфейс"));
        var mode = new CheckBox { Content = "Режим диагностики", IsChecked = settings.DebugMode, Margin = new Thickness(0, 5, 0, 8) };
        AutomationProperties.SetName(mode, "Режим диагностики");
        mode.Click += (_, _) =>
        {
            settings.DebugMode = mode.IsChecked == true;
            UpdateDiagnostics();
            saveDiagnostics();
        };
        panel.Children.Add(mode);
        panel.Children.Add(Terminal.Copy("Показывает состояние потоков, счётчики и подробные пояснения."));
        return panel;
    }

    private void AddDiagnostic(Panel panel, string text)
    {
        var block = Terminal.Copy(text);
        diagnosticCopy.Add(block);
        panel.Children.Add(block);
    }

    private void UpdateDiagnostics()
    {
        foreach (var block in diagnosticCopy)
            block.Visibility = settings.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetIsEnabled(updateStatus, settings.DebugMode);
        ToolTipService.SetIsEnabled(voiceStatus, settings.DebugMode);
        UpdateFilterVisibility();
    }

    private StackPanel NotificationOptions(Action saveDensities)
    {
        var panel = new StackPanel();
        AddNotificationHeading("Импульсы цены и объёма");
        panel.Children.Add(NotificationOption("Всплывающее окно", settings.SignalPopup, value => settings.SignalPopup = value));
        panel.Children.Add(NotificationOption("Звук", settings.SignalSound, value => settings.SignalSound = value));
        AddNotificationHeading("Ценовые оповещения");
        panel.Children.Add(NotificationOption("Всплывающее окно", settings.PriceAlertPopup, value => settings.PriceAlertPopup = value));
        panel.Children.Add(NotificationOption("Звук", settings.PriceAlertSound, value => settings.PriceAlertSound = value));
        AddNotificationHeading("Плотности");
        panel.Children.Add(NotificationOption("Всплывающее окно", settings.Densities.Popup, value => settings.Densities.Popup = value, saveDensities));
        panel.Children.Add(NotificationOption("Звук", settings.Densities.Sound, value => settings.Densities.Sound = value, saveDensities));
        AddNotificationHeading("Озвучка сигналов");
        panel.Children.Add(NotificationOption("Озвучка на русском языке", settings.SignalVoice, value => settings.SignalVoice = value));
        voiceStatus.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(voiceStatus);
        if (neuralVoice != null)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var download = new Button { Content = "Загрузить нейроголос · 86 МБ", Margin = new Thickness(0, 0, 6, 4),
                Visibility = neuralVoice.IsReady ? Visibility.Collapsed : Visibility.Visible };
            download.Click += async (_, _) =>
            {
                downloadingVoice = true;
                download.IsEnabled = false;
                voiceStatus.Text = "Загружается русский голос «Дмитрий»…";
                try
                {
                    await neuralVoice.Install().WaitAsync(stop.Token);
                    download.Visibility = Visibility.Collapsed;
                    await CheckVoice();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!stop.IsCancellationRequested)
                    {
                        voiceStatus.Text = "Не удалось загрузить нейроголос. Повторите попытку.";
                        voiceStatus.ToolTip = ex.Message;
                    }
                }
                finally
                {
                    downloadingVoice = false;
                    if (!stop.IsCancellationRequested) download.IsEnabled = true;
                }
            };
            actions.Children.Add(download);
            panel.Children.Add(actions);
        }
        speechEditor = new(settings, saveNotifications, previewVoice) { CanPreview = neuralVoice?.IsReady == true };
        panel.Children.Add(speechEditor);
        var speechSettings = new Button { Content = "Открыть параметры речи Windows", Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left };
        speechSettings.Click += (_, _) =>
        {
            try { using var process = Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                voiceStatus.Text = "Нажмите Win + R, введите ms-settings:speech и нажмите Enter.";
            }
        };
        panel.Children.Add(speechSettings);
        return panel;

        void AddNotificationHeading(string caption)
        {
            var heading = Terminal.Heading(caption);
            if (panel.Children.Count > 0) heading.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(heading);
        }
    }

    private CheckBox NotificationOption(string name, bool enabled, Action<bool> set, Action? save = null)
    {
        var check = new CheckBox { Content = name, IsChecked = enabled, Margin = new Thickness(0, 9, 0, 0) };
        check.Click += (_, _) =>
        {
            set(check.IsChecked == true);
            (save ?? saveNotifications)();
        };
        return check;
    }

    private async Task CheckVoice()
    {
        if (neuralVoice?.IsReady == true)
        {
            voiceStatus.Text = "Нейроголос «Дмитрий» готов.";
            voiceStatus.ToolTip = null;
            if (speechEditor != null) speechEditor.CanPreview = true;
            if (voiceMixPreview != null) voiceMixPreview.IsEnabled = true;
            return;
        }
        if (downloadingVoice) return;
        voiceStatus.Text = "";
        bool available = await Task.Run(WindowsSignalVoice.RussianVoiceAvailable);
        if (stop.IsCancellationRequested || downloadingVoice || neuralVoice?.IsReady == true) return;
        if (speechEditor != null) speechEditor.CanPreview = available;
        if (voiceMixPreview != null) voiceMixPreview.IsEnabled = available;
        voiceStatus.Text = available
            ? "Доступен русский голос Windows."
            : "Загрузите русский нейроголос или добавьте русский голос в параметрах речи Windows.";
    }

    private StackPanel About()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Thin Market Monitor", accent: true));
        AddDiagnostic(panel, Updater.Installed ? "Установленная версия · обновляется через Setup" : "Portable · автономный EXE");
        var section = Terminal.Rule(new Thickness(0, 1, 0, 0));
        section.Margin = new Thickness(0, 16, 0, 0);
        section.Padding = new Thickness(0, 13, 0, 0);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Обновление через GitHub", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5)
        });
        updateStatus.Margin = new Thickness(0, 0, 0, 7);
        content.Children.Add(updateStatus);
        var buttons = new WrapPanel();
        check.Margin = new Thickness(0, 0, 5, 4);
        install.Margin = new Thickness(0, 0, 0, 4);
        buttons.Children.Add(check);
        buttons.Children.Add(install);
        content.Children.Add(buttons);
        section.Child = content;
        panel.Children.Add(section);
        check.Click += async (_, _) => await Check();
        install.Click += async (_, _) => await Install();
        return panel;
    }

    public void UpdateFilter(string status)
    {
        result.Text = status;
        UpdateFilterVisibility();
    }

    private void UpdateFilterVisibility() =>
        result.Visibility = settings.DebugMode && result.Text.Length > 0 &&
            currentPage is FiltersPage or ModulesPage or PricePage or VolumePage or BlacklistPage or NotificationsPage
            ? Visibility.Visible : Visibility.Collapsed;

    private async Task Check()
    {
        check.IsEnabled = false;
        install.Visibility = Visibility.Collapsed;
        updateStatus.Text = $"v{Updater.Current} · проверка GitHub…";
        updateStatus.ToolTip = null;
        try
        {
            update = await updater.Check(stop.Token);
            updateStatus.Text = update == null ? $"v{Updater.Current} · новых версий нет"
                : $"v{Updater.Current} · доступна v{update.Version}";
            install.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested)
            {
                updateStatus.Text = $"v{Updater.Current} · не удалось проверить обновления";
                updateStatus.ToolTip = ex.Message;
            }
        }
        finally { check.IsEnabled = true; }
    }

    private async Task Install()
    {
        if (update is not { } release) return;
        Installing = true;
        layout.IsEnabled = false;
        updateStatus.ToolTip = null;
        try
        {
            await updater.Stage(release, new Progress<int>(p => updateStatus.Text = $"Загрузка и проверка v{release.Version} · {p}%"), stop.Token);
            Installing = false;
            restart();
        }
        catch (Exception ex)
        {
            updateStatus.Text = "Не удалось обновить приложение";
            updateStatus.ToolTip = ex.Message;
        }
        finally
        {
            Installing = false;
            layout.IsEnabled = true;
        }
    }

    public void Close()
    {
        if (Installing) return;
        close();
    }

    public void Dispose()
    {
        if (mixSaveTimer.IsEnabled) SaveSoundMix();
        stop.Cancel();
        colibri.StatusChanged -= ShowColibriStatus;
    }
}

internal sealed class MarketPicker : Border
{
    private readonly TextBox query = new();
    private readonly ListBox list = new() { DisplayMemberPath = nameof(Market.Label), Height = 187 };
    private readonly Button choose = new()
    {
        Content = "Закрепить график", HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false
    };
    private IReadOnlyList<Market> markets = [];

    public MarketPicker(IReadOnlyList<Market> markets, Action close, Action<Market> confirm)
    {
        Width = 410;
        VerticalAlignment = VerticalAlignment.Center;
        Background = Terminal.Brush("Shell");
        BorderBrush = Terminal.Brush("Outline");
        BorderThickness = new Thickness(1);
        AutomationProperties.SetName(this, "Выбор инструмента");
        var layout = new StackPanel();
        Child = layout;
        var header = Terminal.Rule(new Thickness(0, 0, 0, 1));
        header.Background = Terminal.Brush("Control");
        header.Child = Terminal.DialogHeader("Выбор инструмента", "Закрыть выбор инструмента", close);
        layout.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(11) };
        var search = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        AutomationProperties.SetName(query, "Тикер или биржа");
        search.Children.Add(query);
        var hint = new TextBlock
        {
            Text = "Тикер или биржа", Foreground = Terminal.Brush("Muted"), IsHitTestVisible = false,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        search.Children.Add(hint);
        body.Children.Add(search);
        list.FontSize = 13;
        AutomationProperties.SetName(list, "Инструменты");
        body.Children.Add(list);
        layout.Children.Add(body);
        var footer = Terminal.Rule(new Thickness(0, 1, 0, 0));
        footer.Padding = new Thickness(12, 8, 12, 8);
        choose.Style = Terminal.Style("PrimaryButton");
        footer.Child = choose;
        layout.Children.Add(footer);

        void Commit()
        {
            if (list.SelectedItem is Market selected) confirm(selected);
        }

        list.SelectionChanged += (_, _) => choose.IsEnabled = list.SelectedItem is Market;
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject clicked && ItemsControl.ContainerFromElement(list, clicked) is ListBoxItem)
                Commit();
        };
        choose.Click += (_, _) => Commit();
        query.TextChanged += (_, _) =>
        {
            hint.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderResults(selectFirst: true);
        };
        query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        UpdateMarkets(markets);
        Loaded += (_, _) => query.Focus();
    }

    public void UpdateMarkets(IReadOnlyList<Market> available)
    {
        if (ReferenceEquals(markets, available)) return;
        markets = available;
        RenderResults(selectFirst: false);
    }

    private void RenderResults(bool selectFirst)
    {
        string queryText = query.Text.Trim();
        var previous = list.SelectedItem as Market;
        var matches = markets.Where(m => m.Label.Contains(queryText, StringComparison.OrdinalIgnoreCase)).ToArray();
        list.ItemsSource = matches;
        if (selectFirst && matches.Length > 0) list.SelectedIndex = 0;
        else if (previous != null) list.SelectedItem = matches.FirstOrDefault(m => m.Key == previous.Key);
    }
}
