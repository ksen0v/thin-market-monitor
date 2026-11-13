using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThinMarketMonitor;

internal sealed class SettingsPanel : Border, IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Grid layout = new();
    private readonly Button[] navigation = new Button[3];
    private readonly Border[] indicators = new Border[3];
    private readonly FrameworkElement[] pages = new FrameworkElement[3];
    private readonly Dictionary<Venue, (Button Button, TextBlock State)> venues = new();
    private readonly List<TextBox> fields = [];
    private readonly TextBlock result = Terminal.Copy("");
    private readonly TextBlock validation = Terminal.Copy("");
    private readonly TextBlock updateStatus = Terminal.Copy($"v{Updater.Current}");
    private readonly Button check = new() { Content = "Проверить версию" };
    private readonly Button install = new() { Content = "Обновить и перезапустить", Visibility = Visibility.Collapsed };
    private readonly Settings settings;
    private readonly IReadOnlyDictionary<Venue, Catalog> catalogs;
    private readonly Updater updater;
    private readonly Action<bool> changed;
    private readonly Action close;
    private readonly Action restart;
    private Release? update;
    private bool checkedOnOpen;
    public bool Installing { get; private set; }

    public SettingsPanel(Settings settings, IReadOnlyDictionary<Venue, Catalog> catalogs, Updater updater,
        Action<bool> changed, Action close, Action restart)
    {
        this.settings = settings;
        this.catalogs = catalogs;
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

        var body = new Grid { MinHeight = 335 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(166) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var nav = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        AutomationProperties.SetName(nav, "Разделы настроек");
        var rail = Terminal.Rule(new Thickness(0, 0, 1, 0));
        rail.Child = nav;
        body.Children.Add(rail);
        AddNavigation(nav, 0, "Листинги", "listings");
        AddNavigation(nav, 1, "Сигнал цены", "activity");
        AddNavigation(nav, 2, "О программе", "info");
        var surface = new Border { Background = Terminal.Brush("Surface"), Padding = new Thickness(18, 16, 18, 16) };
        Grid.SetColumn(surface, 1);
        body.Children.Add(surface);
        var pageHost = new Grid();
        surface.Child = pageHost;
        pages[0] = Listings();
        pages[1] = Signals();
        pages[2] = About();
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
        SelectPage(0);
        Loaded += (_, _) => navigation[0].Focus();
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
        for (int i = 0; i < pages.Length; i++)
        {
            bool selected = i == index;
            pages[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            navigation[i].Background = selected ? Terminal.Brush("Control") : Brushes.Transparent;
            navigation[i].Foreground = Terminal.Brush(selected ? "Accent" : "Muted");
            indicators[i].Background = selected ? Terminal.Brush("Accent") : Brushes.Transparent;
            AutomationProperties.SetItemStatus(navigation[i], selected ? "Текущий раздел" : "");
        }
        if (index == 2 && !checkedOnOpen)
        {
            checkedOnOpen = true;
            _ = Check();
        }
    }

    private StackPanel Listings()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Листинги"));
        panel.Children.Add(Terminal.Copy("Нажимайте на значок биржи, чтобы сменить условие."));
        var logos = new UniformGrid { Columns = 4, Rows = 2, Margin = new Thickness(0, 14, -7, 5) };
        AutomationProperties.SetName(logos, "Фильтр по биржам");
        foreach (var venue in Enum.GetValues<Venue>())
        {
            var button = new Button { Style = Terminal.Style("VenueButton"), Margin = new Thickness(0, 0, 7, 7) };
            var content = new Grid();
            var logo = new Grid { Width = 48, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            string name = (venue == Venue.Alpha ? Venue.Binance : venue).ToString().ToLowerInvariant();
            logo.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}.png")),
                Width = venue switch { Venue.Okx => 55, Venue.Mexc => 33, Venue.KuCoin => 29, _ => 44 },
                Height = venue switch { Venue.Okx => 49, Venue.KuCoin => 28, _ => 42 },
                Stretch = Stretch.Uniform
            });
            if (venue == Venue.Alpha)
            {
                logo.Children.Add(new TextBlock
                {
                    Text = "α", FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(243, 186, 47)),
                    Background = Terminal.Brush("Plot"), Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(0, 0, -1, -1), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            content.Children.Add(logo);
            var state = new TextBlock
            {
                FontSize = 14, FontFamily = new FontFamily("Segoe UI Symbol"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 5, 0)
            };
            content.Children.Add(state);
            button.Content = content;
            logos.Children.Add(button);
            venues.Add(venue, (button, state));
            button.Click += (_, _) =>
            {
                var rule = settings.Rules.GetValueOrDefault(venue);
                var next = rule switch
                {
                    ListingRule.Ignore => ListingRule.Require,
                    ListingRule.Require => ListingRule.Exclude,
                    _ => ListingRule.Ignore
                };
                if (next == ListingRule.Ignore) settings.Rules.Remove(venue);
                else settings.Rules[venue] = next;
                UpdateVenue(venue);
                changed(false);
            };
            UpdateVenue(venue);
        }
        panel.Children.Add(logos);
        var legend = new WrapPanel();
        AddLegend("✓", " Требовать листинг", "Required");
        AddLegend("×", " Исключать при листинге", "Excluded");
        AddLegend("", "Без отметки · игнорировать", "Muted");
        panel.Children.Add(legend);
        return panel;

        void AddLegend(string mark, string text, string color)
        {
            var entry = new TextBlock { FontSize = 11, Foreground = Terminal.Brush("Muted"), Margin = new Thickness(0, 0, 12, 4) };
            entry.Inlines.Add(new System.Windows.Documents.Run(mark)
            {
                Foreground = Terminal.Brush(color), FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Symbol")
            });
            entry.Inlines.Add(text);
            legend.Children.Add(entry);
        }
    }

    private void UpdateVenue(Venue venue)
    {
        var (button, state) = venues[venue];
        var rule = settings.Rules.GetValueOrDefault(venue);
        var (mark, caption, border, background) = rule switch
        {
            ListingRule.Require => ("✓", "требуется листинг", "Required", "RequiredSurface"),
            ListingRule.Exclude => ("×", "требуется отсутствие листинга", "Excluded", "ExcludedSurface"),
            _ => ("", "игнорировать", "Outline", "Plot")
        };
        state.Text = mark;
        state.Foreground = button.BorderBrush = Terminal.Brush(border);
        button.Background = Terminal.Brush(background);
        string label = $"{venue.Label()} · {caption}";
        AutomationProperties.SetName(button, label);
        button.ToolTip = catalogs.TryGetValue(venue, out var catalog) && catalog.Error != null
            ? $"{label}\nКаталог недоступен · {catalog.Error}" : label;
    }

    private StackPanel Signals()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Резкий рост цены"));
        panel.Children.Add(Terminal.Copy("Включайте и настраивайте порог для каждого интервала."));
        var thresholds = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < settings.Thresholds.Length; i++)
        {
            int index = i;
            var threshold = settings.Thresholds[i];
            string interval = MainWindow.WindowName(threshold.Seconds);
            var enabled = new CheckBox { IsChecked = threshold.Enabled, Content = interval, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBox
            {
                Text = FormatThreshold(threshold.Percent), Width = 51, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            fields.Add(value);
            AutomationProperties.SetName(value, $"Порог за {interval}, %");
            var field = new StackPanel { Orientation = Orientation.Horizontal };
            field.Children.Add(value);
            field.Children.Add(new TextBlock { Text = "%", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var row = new DockPanel { MinHeight = 41 };
            DockPanel.SetDock(field, Dock.Right);
            row.Children.Add(field);
            row.Children.Add(enabled);
            var frame = Terminal.Rule(new Thickness(0, 1, 0, i == settings.Thresholds.Length - 1 ? 1 : 0));
            frame.Child = row;
            thresholds.Children.Add(frame);
            enabled.Checked += (_, _) => SetEnabled(index, true);
            enabled.Unchecked += (_, _) => SetEnabled(index, false);
            value.TextChanged += (_, _) => Validate();
            value.LostKeyboardFocus += (_, _) => SaveValue(index);
            value.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                SaveValue(index);
                e.Handled = true;
            };
        }
        panel.Children.Add(thresholds);
        validation.Foreground = Terminal.Brush("Excluded");
        validation.Margin = new Thickness(0, 6, 0, 0);
        validation.Visibility = Visibility.Collapsed;
        panel.Children.Add(validation);
        return panel;
    }

    private void SetEnabled(int index, bool enabled)
    {
        var previous = settings.Thresholds[index];
        if (previous.Enabled == enabled) return;
        settings.Thresholds[index] = previous with { Enabled = enabled };
        changed(true);
    }

    private static string FormatThreshold(double value) => value.ToString(CultureInfo.GetCultureInfo("ru-RU"));

    internal static bool TryThreshold(string input, out double value) =>
        double.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        value is > 0 and <= 10000;

    private void Validate()
    {
        bool invalid = false;
        foreach (var field in fields)
        {
            bool valid = TryThreshold(field.Text, out _);
            field.BorderBrush = Terminal.Brush(valid ? "Outline" : "Excluded");
            invalid |= !valid;
        }
        validation.Text = "Введите порог больше 0 и не выше 10 000%.";
        validation.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveValue(int index)
    {
        var previous = settings.Thresholds[index];
        if (!TryThreshold(fields[index].Text, out double percent) || previous.Percent == percent) return;
        settings.Thresholds[index] = previous with { Percent = percent };
        changed(true);
    }

    private StackPanel About()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Thin Market Monitor", accent: true));
        panel.Children.Add(Terminal.Copy("Мониторинг спотовых рынков для Windows. Приложение фильтрует монеты по листингам и открывает минутный график при резком росте цены."));
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
        foreach (var venue in venues.Keys) UpdateVenue(venue);
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
        for (int i = 0; i < fields.Count; i++) SaveValue(i);
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
