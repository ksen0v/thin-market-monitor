using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class SettingsWindow : Window
{
    private readonly Dictionary<Venue, ComboBox> rules = new();
    private readonly List<(Threshold Rule, CheckBox Enabled, TextBox Value)> thresholds = [];
    private readonly CancellationTokenSource stop = new();
    private readonly TextBlock updateStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon };
    private readonly Button check = new() { Content = "Проверить версию" };
    private readonly Button install = new() { Content = "Обновить и перезапустить", Visibility = Visibility.Collapsed };
    private Release? update;
    private readonly Updater updater;
    private bool installing;

    public Dictionary<Venue, ListingRule> Rules { get; private set; } = new();
    public Threshold[] Thresholds { get; private set; } = [];
    public bool Restarting { get; private set; }

    public SettingsWindow(Settings settings, IReadOnlyDictionary<Venue, Catalog> catalogs, Updater updater)
    {
        this.updater = updater;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Настройки · Thin Market Monitor";
        Width = 560;
        Height = 710;
        MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить", IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        save.Click += (_, _) => Commit();
        footer.Children.Add(save);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var panel = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        AddTitle(panel, "Листинги");
        panel.Children.Add(new TextBlock
        {
            Text = "✓ требуется листинг   × требуется отсутствие   — игнорировать",
            Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap
        });
        foreach (var venue in Enum.GetValues<Venue>())
        {
            var combo = new ComboBox { Width = 158, ItemsSource = new[] { "—  Игнорировать", "✓  Есть листинг", "×  Нет листинга" },
                SelectedIndex = (int)settings.Rules.GetValueOrDefault(venue) };
            System.Windows.Automation.AutomationProperties.SetName(combo, venue.Label());
            string name = venue.Label();
            if (catalogs.TryGetValue(venue, out var catalog) && catalog.Error != null)
            {
                name += " · каталог недоступен";
                combo.ToolTip = catalog.Error;
            }
            AddRow(panel, name, combo);
            rules[venue] = combo;
        }
        AddTitle(panel, "Резкий рост цены");
        foreach (var threshold in settings.Thresholds)
        {
            var enabled = new CheckBox { IsChecked = threshold.Enabled, Content = MainWindow.WindowName(threshold.Seconds),
                VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBox { Text = threshold.Percent.ToString(CultureInfo.CurrentCulture), Width = 100 };
            System.Windows.Automation.AutomationProperties.SetName(value, $"Порог за {MainWindow.WindowName(threshold.Seconds)}, %");
            var field = new StackPanel { Orientation = Orientation.Horizontal };
            field.Children.Add(value);
            field.Children.Add(new TextBlock { Text = " %", VerticalAlignment = VerticalAlignment.Center });
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(field, Dock.Right);
            row.Children.Add(field);
            row.Children.Add(enabled);
            panel.Children.Add(row);
            thresholds.Add((threshold, enabled, value));
        }
        panel.Children.Add(validation);

        AddTitle(panel, "Обновление приложения");
        panel.Children.Add(updateStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 8) };
        buttons.Children.Add(check);
        buttons.Children.Add(install);
        panel.Children.Add(buttons);
        check.Click += async (_, _) => await Check();
        install.Click += async (_, _) => await Install();
        install.IsEnabledChanged += (_, _) => save.IsEnabled = !installing;
        Loaded += async (_, _) => await Check();
        Closing += (_, e) => { if (installing) e.Cancel = true; };
        Closed += (_, _) => stop.Cancel();
    }

    private void Commit()
    {
        if (installing) return;
        var values = new List<Threshold>();
        foreach (var (rule, enabled, field) in thresholds)
        {
            if ((!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
                !double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
                value is not (> 0 and <= 10000))
            {
                validation.Text = "Введите порог больше 0 и не выше 10 000%.";
                field.Focus();
                return;
            }
            values.Add(new(rule.Seconds, enabled.IsChecked == true, value));
        }
        Rules = rules.ToDictionary(x => x.Key, x => (ListingRule)x.Value.SelectedIndex);
        Thresholds = values.ToArray();
        DialogResult = true;
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
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            updateStatus.Text = $"v{Updater.Current} · ошибка проверки · {ex.Message}";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { check.IsEnabled = true; }
    }

    private async Task Install()
    {
        if (update == null) return;
        installing = true;
        check.IsEnabled = install.IsEnabled = false;
        try
        {
            await updater.Stage(update, new Progress<int>(p => updateStatus.Text = $"Загрузка и проверка v{update.Version} · {p}%"), stop.Token);
            Restarting = true;
            installing = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            updateStatus.Text = $"Ошибка обновления · {ex.Message}";
        }
        finally
        {
            installing = false;
            check.IsEnabled = install.IsEnabled = true;
        }
    }

    private static void AddTitle(Panel panel, string title) => panel.Children.Add(new TextBlock
    {
        Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6)
    });

    private static void AddRow(Panel panel, string name, Control control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row);
    }
}

internal sealed class MarketPicker : Window
{
    private readonly ListBox list = new() { DisplayMemberPath = nameof(Market.Label) };
    public Market? Selected => list.SelectedItem as Market;

    public MarketPicker(IReadOnlyList<Market> markets)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Выбор инструмента";
        Width = 460;
        Height = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(12) };
        Content = layout;
        var query = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(query, "Тикер или биржа");
        DockPanel.SetDock(query, Dock.Top);
        layout.Children.Add(query);
        var choose = new Button { Content = "Закрепить график", IsDefault = true,
            Margin = new Thickness(0, 8, 0, 0), IsEnabled = false };
        DockPanel.SetDock(choose, Dock.Bottom);
        layout.Children.Add(choose);
        layout.Children.Add(list);
        list.SelectionChanged += (_, _) => choose.IsEnabled = Selected != null;
        list.MouseDoubleClick += (_, _) => { if (Selected != null) DialogResult = true; };
        choose.Click += (_, _) => { if (Selected != null) DialogResult = true; };
        query.TextChanged += (_, _) =>
        {
            list.ItemsSource = markets.Where(m => m.Label.Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        };
        query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            if (e.Key == Key.Escape) Close();
        };
        list.ItemsSource = markets;
        Loaded += (_, _) => query.Focus();
    }
}
