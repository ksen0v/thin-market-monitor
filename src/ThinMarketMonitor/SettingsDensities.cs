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
        AddOption("Скрывать мигающие уровни", options.IgnoreFlicker, value => options.IgnoreFlicker = value);
        AddOption("Скрывать зеркальные уровни", options.IgnoreMirrored, value => options.IgnoreMirrored = value);
        AddOption("Скрывать двусторонние серии заявок", options.IgnoreLadders, value => options.IgnoreLadders = value,
            "Похожие по размеру заявки с регулярным шагом по обе стороны рыночной цены.");
        AddOption("Исключать токены с плечом (3L, 3S, 5L, 5S)", options.HideLeveragedTokens, value => options.HideLeveragedTokens = value);
        AddOption("Исключать акции и фонды (ETF)", options.HideStocksAndFunds, value => options.HideStocksAndFunds = value);
        AddDiagnostic(panel, "Мигание определяется по повторному исчезновению уровня. Зеркальные уровни — три и более пары близких по сумме уровней по обе стороны спреда.");

        var venues = Enum.GetValues<Venue>();
        var exchange = new ComboBox { ItemsSource = venues.Select(v => v.Label()).ToArray(), Margin = new Thickness(0, 9, 0, 0) };
        panel.Children.Add(DensityRuleEditor.Field("Биржа", exchange));
        var markets = new WrapPanel();
        var spot = new CheckBox { Content = "Spot", Margin = new Thickness(0, 0, 13, 0) };
        var futures = new CheckBox { Content = "Futures" };
        markets.Children.Add(spot);
        markets.Children.Add(futures);
        panel.Children.Add(DensityRuleEditor.Field("Сканировать", markets));

        var editor = new DensityRuleEditor();
        panel.Children.Add(editor);
        var apply = new Button { Content = "Применить для биржи", Style = Terminal.Style("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 5) };
        var message = Terminal.Copy("");
        panel.Children.Add(apply);
        panel.Children.Add(message);
        AddDiagnostic(panel, "Сумма считается по видимым заявкам на одной цене. Расстояние — от середины спреда. Возраст отсчитывается от первого наблюдения.");

        exchange.SelectionChanged += (_, _) =>
        {
            if (exchange.SelectedIndex < 0) return;
            var venue = venues[exchange.SelectedIndex];
            var rule = options.Venues.GetValueOrDefault(venue) ?? new();
            spot.IsChecked = options.Scans(new(venue));
            futures.IsEnabled = venue != Venue.Alpha;
            futures.IsChecked = futures.IsEnabled && options.Scans(new(venue, Segment.Futures));
            editor.Load(rule);
            message.Text = "";
        };
        apply.Click += (_, _) =>
        {
            if (!editor.TryRead(out var rule)) { DensityRuleEditor.Invalid(message); return; }
            var venue = venues[exchange.SelectedIndex];
            bool useSpot = spot.IsChecked == true, useFutures = futures.IsEnabled && futures.IsChecked == true;
            if (useSpot) options.SetSourceRule(new(venue), ListingRule.Require);
            if (useFutures) options.SetSourceRule(new(venue, Segment.Futures), ListingRule.Require);
            options.Venues[venue] = new(useSpot, useFutures,
                rule.SmallUsd, rule.LargeUsd, rule.MinDistance, rule.MaxDistance, rule.MinSeconds, rule.SmallColor, rule.LargeColor);
            save();
            message.Foreground = Terminal.Brush("Rising");
            message.Text = $"Сохранено · {venue.Label()}";
        };
        exchange.SelectedIndex = Math.Max(0, Array.FindIndex(venues, venue =>
            options.Scans(new(venue)) || options.Scans(new(venue, Segment.Futures))));
        return panel;

        void AddOption(string label, bool enabled, Action<bool> change, string? description = null)
        {
            var toggle = new CheckBox { Content = label, IsChecked = enabled, ToolTip = description, Margin = new Thickness(0, 4, 0, 1) };
            toggle.Click += (_, _) =>
            {
                change(toggle.IsChecked == true);
                save();
            };
            panel.Children.Add(toggle);
        }
    }

    private sealed record DensityCoinChoice(string Asset, string Caption, Market? Market);

    private StackPanel DensityCoinOptions(Action save, Market? openingCoin, Func<IEnumerable<Market>>? markets)
    {
        var options = settings.Densities;
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Настройки монет"));
        panel.Children.Add(Terminal.Copy("Свои параметры монеты действуют на всех выбранных рынках."));
        var selector = new ComboBox
        {
            DisplayMemberPath = nameof(DensityCoinChoice.Caption), MaxDropDownHeight = 285,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)))
        };
        TextSearch.SetTextPath(selector, nameof(DensityCoinChoice.Caption));
        ScrollViewer.SetCanContentScroll(selector, true);
        VirtualizingPanel.SetIsVirtualizing(selector, true);
        VirtualizingPanel.SetVirtualizationMode(selector, VirtualizationMode.Recycling);
        var selectorRow = DensityRuleEditor.Field("Монета", selector);
        selectorRow.Margin = new Thickness(0, 9, 0, 4);
        panel.Children.Add(selectorRow);
        var inheritance = Terminal.Copy("");
        inheritance.Margin = new Thickness(0, 3, 0, 5);
        panel.Children.Add(inheritance);
        var editor = new DensityRuleEditor();
        panel.Children.Add(editor);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 5) };
        var apply = new Button { Content = "Сохранить для монеты", Style = Terminal.Style("PrimaryButton"),
            Margin = new Thickness(0, 0, 7, 4) };
        var reset = new Button { Content = "Сбросить к настройкам биржи", Margin = new Thickness(0, 0, 0, 4) };
        buttons.Children.Add(apply);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        var message = Terminal.Copy("");
        panel.Children.Add(message);

        selector.SelectionChanged += (_, _) => RefreshEditor();
        panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) LoadChoices(); };
        apply.Click += (_, _) =>
        {
            if (selector.SelectedItem is not DensityCoinChoice selected) return;
            if (!editor.TryRead(out var rule)) { DensityRuleEditor.Invalid(message); return; }
            options.CoinRules[selected.Asset] = rule;
            save();
            RefreshEditor();
            message.Foreground = Terminal.Brush("Rising");
            message.Text = "Настройки монеты сохранены";
        };
        reset.Click += (_, _) =>
        {
            if (selector.SelectedItem is not DensityCoinChoice selected || !options.CoinRules.Remove(selected.Asset)) return;
            save();
            RefreshEditor();
        };
        RefreshEditor();
        return panel;

        void LoadChoices()
        {
            var selection = (selector.SelectedItem as DensityCoinChoice)?.Asset ?? openingCoin?.ListingAsset;
            var available = (openingCoin == null ? Enumerable.Empty<Market>() : [openingCoin]).Concat(markets?.Invoke() ?? []);
            var byAsset = available.DistinctBy(item => item.ListingAsset)
                .ToDictionary(item => item.ListingAsset, StringComparer.Ordinal);
            var coins = byAsset.Keys.Concat(options.CoinRules.Keys).Distinct(StringComparer.Ordinal)
                .Select(asset =>
                {
                    var market = byAsset.GetValueOrDefault(asset);
                    string name = asset.Contains(':') ? market?.Base ?? asset : asset;
                    if (asset.Contains(':') && name != asset)
                        name = $"{name} · {(asset.StartsWith("equity:", StringComparison.Ordinal) ? "акция" : market!.Venue.Label())}";
                    return new DensityCoinChoice(asset, name, market);
                }).OrderBy(item => item.Caption, StringComparer.Ordinal).ThenBy(item => item.Asset, StringComparer.Ordinal).ToArray();
            selector.ItemsSource = coins;
            selector.SelectedItem = coins.FirstOrDefault(item => item.Asset == selection) ??
                coins.FirstOrDefault(item => options.CoinRules.ContainsKey(item.Asset));
            RefreshEditor();
        }

        void RefreshEditor()
        {
            message.Text = "";
            var selected = selector.SelectedItem as DensityCoinChoice;
            editor.IsEnabled = apply.IsEnabled = selected != null;
            reset.IsEnabled = false;
            if (selected == null)
            {
                inheritance.Text = selector.HasItems ? "Выберите монету" : "Доступные инструменты ещё не загружены";
                return;
            }
            if (options.CoinRules.TryGetValue(selected.Asset, out var saved))
            {
                editor.Load(saved);
                inheritance.Text = "Свои параметры";
                reset.IsEnabled = true;
                return;
            }
            var venue = selected.Market?.Venue;
            editor.Load(venue is { } source ? options.Venues.GetValueOrDefault(source) ?? new() : new DensityRule());
            inheritance.Text = venue is { } marketVenue ? $"Параметры биржи · {marketVenue.Label()}" : "Параметры биржи";
        }
    }
}

internal sealed class DensityRuleEditor : StackPanel
{
    private readonly TextBox small = Input();
    private readonly TextBox large = Input();
    private readonly TextBox minDistance = Input();
    private readonly TextBox maxDistance = Input();
    private readonly TextBox minSeconds = Input();
    private readonly DensityColorPicker smallColor = new();
    private readonly DensityColorPicker largeColor = new();

    internal DensityRuleEditor()
    {
        Children.Add(Field("Уровень 1, ≈ USD", small));
        Children.Add(Field("Цвет уровня 1", smallColor));
        Children.Add(Field("Уровень 2, ≈ USD", large));
        Children.Add(Field("Цвет уровня 2", largeColor));
        Children.Add(Field("От цены, минимум %", minDistance));
        Children.Add(Field("От цены, максимум %", maxDistance));
        Children.Add(Field("Стоит минимум, с", minSeconds));
        Load(new());
    }

    internal void Load(DensityRule rule)
    {
        small.Text = Format(rule.SmallUsd);
        large.Text = Format(rule.LargeUsd);
        smallColor.Hex = rule.SmallColor;
        largeColor.Hex = rule.LargeColor;
        minDistance.Text = Format(rule.MinDistance);
        maxDistance.Text = Format(rule.MaxDistance);
        minSeconds.Text = Format(rule.MinSeconds);
    }

    internal bool TryRead(out DensityRule rule)
    {
        bool validSeconds = int.TryParse(minSeconds.Text.Trim(), out int seconds);
        rule = new(Parse(small), Parse(large), Parse(minDistance), Parse(maxDistance), seconds, smallColor.Hex, largeColor.Hex);
        return validSeconds && rule.Valid;
    }

    internal static void Invalid(TextBlock message)
    {
        message.Foreground = Terminal.Brush("Falling");
        message.Text = "Уровень 2 должен превышать уровень 1. Расстояние — от 0 до 100%, возраст — от 0 до 86 400 с.";
    }

    internal static DockPanel Field(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 6, 2) };
        var caption = Terminal.Copy(label, muted: false);
        caption.Width = 158;
        caption.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(caption, Dock.Left);
        AutomationProperties.SetName(control, label);
        row.Children.Add(caption);
        row.Children.Add(control);
        return row;
    }

    private static TextBox Input() => new() { MinHeight = 25, Padding = new Thickness(5, 2, 5, 1) };
    private static string Format(double value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    private static double Parse(TextBox input) =>
        double.TryParse(input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : double.NaN;
}
