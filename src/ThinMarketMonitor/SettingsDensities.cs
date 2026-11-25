using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel DensitySettings(Action save)
    {
        var options = settings.Densities;
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Плотности стакана"));
        AddOption("Луч на графиках обзора", options.OverviewOverlay, value => options.OverviewOverlay = value);
        AddOption("Луч на графиках сигналов", options.SignalsOverlay, value => options.SignalsOverlay = value);
        AddOption("Всплывающие уведомления", options.Popup, value => options.Popup = value);
        AddOption("Звук при появлении плотности", options.Sound, value => options.Sound = value);
        AddOption("Скрывать мигающие уровни", options.IgnoreFlicker, value => options.IgnoreFlicker = value);
        AddOption("Скрывать зеркальные уровни", options.IgnoreMirrored, value => options.IgnoreMirrored = value);
        var explanation = Terminal.Copy("Мигание определяется по повторному исчезновению уровня. Зеркальные уровни — три и более пары близких по сумме уровней по обе стороны спреда.");
        explanation.Margin = new Thickness(0, 5, 0, 14);
        panel.Children.Add(explanation);

        var exchange = new ComboBox { ItemsSource = Enum.GetValues<Venue>().Select(v => v.Label()).ToArray() };
        AddField("Биржа", exchange);
        var markets = new WrapPanel();
        var spot = new CheckBox { Content = "Спот", Margin = new Thickness(0, 0, 13, 0) };
        var futures = new CheckBox { Content = "Фьючерсы" };
        markets.Children.Add(spot);
        markets.Children.Add(futures);
        AddField("Сканировать", markets);
        var small = AddInput("Уровень 1, ≈ USD");
        var smallColor = AddInput("Цвет 1, #RRGGBB");
        var large = AddInput("Уровень 2, ≈ USD");
        var largeColor = AddInput("Цвет 2, #RRGGBB");
        var minDistance = AddInput("От цены, минимум %");
        var maxDistance = AddInput("От цены, максимум %");
        var minSeconds = AddInput("Стоит минимум, с");
        var apply = new Button { Content = "Применить для биржи", Style = Terminal.Style("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 5) };
        panel.Children.Add(apply);
        var message = Terminal.Copy("");
        panel.Children.Add(message);
        panel.Children.Add(Terminal.Copy("Сумма считается по видимым заявкам на одной цене. Расстояние — от середины спреда. Возраст отсчитывается от первого наблюдения."));

        var venues = Enum.GetValues<Venue>();
        exchange.SelectionChanged += (_, _) =>
        {
            if (exchange.SelectedIndex < 0) return;
            var venue = venues[exchange.SelectedIndex];
            var rule = options.Venues.GetValueOrDefault(venue) ?? new();
            spot.IsChecked = rule.Spot;
            futures.IsEnabled = venue != Venue.Alpha;
            futures.IsChecked = futures.IsEnabled && rule.Futures;
            small.Text = RuleText(rule.SmallUsd);
            large.Text = RuleText(rule.LargeUsd);
            smallColor.Text = rule.SmallColor;
            largeColor.Text = rule.LargeColor;
            minDistance.Text = RuleText(rule.MinDistance);
            maxDistance.Text = RuleText(rule.MaxDistance);
            minSeconds.Text = RuleText(rule.MinSeconds);
            message.Text = "";
        };
        apply.Click += (_, _) =>
        {
            var venue = venues[exchange.SelectedIndex];
            bool secondsValid = int.TryParse(minSeconds.Text.Trim(), out int seconds);
            var rule = new DensityVenueRule(spot.IsChecked == true, futures.IsEnabled && futures.IsChecked == true,
                Parse(small), Parse(large), Parse(minDistance), Parse(maxDistance), seconds,
                smallColor.Text.Trim().ToUpperInvariant(), largeColor.Text.Trim().ToUpperInvariant());
            if (!secondsValid || !rule.Valid)
            {
                message.Foreground = Terminal.Brush("Falling");
                message.Text = "Проверьте значения. Уровень 2 должен превышать уровень 1. Диапазон расстояния — 0–100%, время — 0–86 400 с. Цвет задаётся как #RRGGBB.";
                return;
            }
            options.Venues[venue] = rule;
            save();
            message.Foreground = Terminal.Brush("Rising");
            message.Text = $"Сохранено · {venue.Label()}";
        };
        exchange.SelectedIndex = Math.Max(0, Array.FindIndex(venues, venue =>
            options.Venues.TryGetValue(venue, out var rule) && (rule.Spot || rule.Futures)));
        return panel;

        void AddOption(string label, bool enabled, Action<bool> change)
        {
            var toggle = new CheckBox { Content = label, IsChecked = enabled, Margin = new Thickness(0, 4, 0, 1) };
            toggle.Click += (_, _) =>
            {
                change(toggle.IsChecked == true);
                save();
            };
            panel.Children.Add(toggle);
        }

        void AddField(string label, FrameworkElement control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 6, 2) };
            var caption = Terminal.Copy(label, muted: false);
            caption.Width = 158;
            caption.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(caption, Dock.Left);
            AutomationProperties.SetName(control, label);
            row.Children.Add(caption);
            row.Children.Add(control);
            panel.Children.Add(row);
        }

        TextBox AddInput(string label)
        {
            var input = new TextBox { MinHeight = 25, Padding = new Thickness(5, 2, 5, 1) };
            AddField(label, input);
            return input;
        }

        static string RuleText(double value) => value.ToString("0.########", CultureInfo.CurrentCulture);
        static double Parse(TextBox input) =>
            double.TryParse(input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : double.NaN;
    }
}
