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
        OverviewPage = 5, AboutPage = 6;
    private readonly CancellationTokenSource stop = new();
    private readonly Grid layout = new();
    private readonly Button[] navigation = new Button[7];
    private readonly Border[] indicators = new Border[7];
    private readonly FrameworkElement[] pages = new FrameworkElement[7];
    private readonly TextBlock result = Terminal.Copy("");
    private readonly TextBlock updateStatus = Terminal.Copy($"v{Updater.Current}");
    private readonly Button check = new() { Content = "Проверить версию" };
    private readonly Button install = new() { Content = "Обновить и перезапустить", Visibility = Visibility.Collapsed };
    private readonly Settings settings;
    private readonly Updater updater;
    private readonly Action<bool> changed;
    private readonly Action close;
    private readonly Action restart;
    private Release? update;
    private bool checkedOnOpen;
    public bool Installing { get; private set; }

    public SettingsPanel(Settings settings, Updater updater,
        Action<bool> changed, Action close, Action restart, bool overview = false)
    {
        this.settings = settings;
        this.updater = updater;
        this.changed = changed;
        this.close = close;
        this.restart = restart;
        Width = 664;
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

        var body = new Grid { MinHeight = 335, MaxHeight = 505 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(176) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var nav = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        AutomationProperties.SetName(nav, "Разделы настроек");
        var rail = Terminal.Rule(new Thickness(0, 0, 1, 0));
        rail.Child = nav;
        body.Children.Add(rail);
        AddNavigation(nav, FiltersPage, "Фильтры", "filter");
        AddNavigation(nav, ModulesPage, "Модули", "listings");
        AddNavigation(nav, PricePage, "Импульс цены", "activity");
        AddNavigation(nav, VolumePage, "Импульс объёма", "volume");
        AddNavigation(nav, ChartsPage, "Графики", "candles");
        AddNavigation(nav, OverviewPage, "Обзор", "volume");
        AddNavigation(nav, AboutPage, "О программе", "info");
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
        pages[AboutPage] = About();
        foreach (var page in pages) pageHost.Children.Add(page);

        var footer = Terminal.Rule(new Thickness(0, 1, 0, 0));
        var footerContent = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var done = new Button { Content = "Готово", Style = Terminal.Style("PrimaryButton") };
        done.Click += (_, _) => Close();
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
        int openingPage = overview ? OverviewPage : FiltersPage;
        SelectPage(openingPage);
        Loaded += (_, _) => navigation[openingPage].Focus();
    }

    private void AddNavigation(Panel nav, int index, string text, string icon)
    {
        var marker = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var button = new Button
        {
            Style = Terminal.Style("IconButton"), MinHeight = 34, Padding = new Thickness(9, 3, 9, 3),
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
    }

    private StackPanel About()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Thin Market Monitor", accent: true));
        panel.Children.Add(Terminal.Copy("Мониторинг спотовых и фьючерсных рынков для Windows."));
        panel.Children.Add(Terminal.Copy(Updater.Installed ? "Установленная версия · обновляется через Setup" : "Portable · автономный EXE"));
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
    }

    private async Task Check()
    {
        check.IsEnabled = false;
        install.Visibility = Visibility.Collapsed;
        updateStatus.Text = $"v{Updater.Current} · проверка GitHub…";
        try
        {
            update = await updater.Check(stop.Token);
            updateStatus.Text = update == null ? $"v{Updater.Current} · новых версий нет"
                : $"v{Updater.Current} · доступна v{update.Version}";
            install.Visibility = update == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested) updateStatus.Text = $"v{Updater.Current} · ошибка проверки · {ex.Message}";
        }
        finally { check.IsEnabled = true; }
    }

    private async Task Install()
    {
        if (update is not { } release) return;
        Installing = true;
        layout.IsEnabled = false;
        try
        {
            await updater.Stage(release, new Progress<int>(p => updateStatus.Text = $"Загрузка и проверка v{release.Version} · {p}%"), stop.Token);
            Installing = false;
            restart();
        }
        catch (Exception ex)
        {
            updateStatus.Text = $"Ошибка обновления · {ex.Message}";
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

    public void Dispose() => stop.Cancel();
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
