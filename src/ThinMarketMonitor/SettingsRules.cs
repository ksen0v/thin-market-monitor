using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private enum RuleKind { Price, Volume, Filter }
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private string? capture;
    private Button? captureButton;
    private TextBlock? shortcutStatus;
    public bool CapturingShortcut => capture != null;

    private StackPanel Modules()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Модули"));
        var price = ModuleButton("Импульс цены", () => settings.PriceModule, enabled => settings.PriceModule = enabled);
        var volume = ModuleButton("Импульс объёма", () => settings.VolumeModule, enabled => settings.VolumeModule = enabled);
        price.Margin = new Thickness(0, 7, 0, 5);
        panel.Children.Add(price);
        panel.Children.Add(volume);
        return panel;
    }

    private Button ModuleButton(string label, Func<bool> enabled, Action<bool> set)
    {
        var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 40 };
        AutomationProperties.SetName(button, label);
        Refresh();
        button.Click += (_, _) =>
        {
            set(!enabled());
            Refresh();
            UpdateModuleNavigation();
            changed(true);
        };
        return button;

        void Refresh()
        {
            bool on = enabled();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = on ? "✓" : "", Width = 16, FontFamily = new FontFamily("Segoe UI Symbol") });
            row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0) });
            button.Content = row;
            button.Foreground = Terminal.Brush(on ? "Required" : "Text");
            button.Background = Terminal.Brush(on ? "RequiredSurface" : "Plot");
            button.BorderBrush = Terminal.Brush(on ? "Required" : "Outline");
            AutomationProperties.SetItemStatus(button, on ? "Включён" : "Выключен");
        }
    }

    private void UpdateModuleNavigation()
    {
        ((FrameworkElement)navigation[PricePage].Parent).Visibility = settings.PriceModule ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)navigation[VolumePage].Parent).Visibility = settings.VolumeModule ? Visibility.Visible : Visibility.Collapsed;
    }

    private StackPanel Rules(RuleKind kind)
    {
        var panel = new StackPanel();
        bool price = kind == RuleKind.Price, filter = kind == RuleKind.Filter;
        string unit = price ? "%" : "USD";
        panel.Children.Add(Terminal.Heading(kind switch
        {
            RuleKind.Price => "Импульс цены", RuleKind.Volume => "Импульс объёма", _ => "Фильтры списка"
        }));
        panel.Children.Add(Terminal.Copy(kind switch
        {
            RuleKind.Price => "Рост цены в скользящем окне.",
            RuleKind.Volume => "Сумма сделок за скользящее окно. USD-стейблкоины считаются по ≈ $1.",
            _ => "Минимальный объём после фильтра листингов. Действуют все включённые условия. USD-стейблкоины ≈ $1."
        }));
        if (filter) panel.Children.Add(Terminal.Copy("24 ч — данные тикера. Остальные периоды — завершённые минутные свечи."));
        var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 6) };
        panel.Children.Add(rows);
        var composer = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(composer);
        var validation = Terminal.Copy("");
        validation.Foreground = Terminal.Brush("Excluded");
        validation.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(validation);

        var period = new TextBox
        {
            Width = filter ? 65 : 85, Text = filter ? "5" : "120",
            Margin = new Thickness(0, 0, 5, 4), TextAlignment = TextAlignment.Right
        };
        AutomationProperties.SetName(period, filter ? "Период в минутах" : "Период в секундах");
        var periodLabel = new TextBlock
        {
            Text = filter ? "мин" : "с", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 4)
        };
        if (filter)
        {
            var shortcuts = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Insert(panel.Children.IndexOf(composer), shortcuts);
            foreach (var (name, minutes) in new[] { ("5 мин", 5), ("1 ч", 60), ("24 ч", 1440) })
            {
                var pick = new Button { Content = name, MinHeight = 24, Margin = new Thickness(0, 0, 5, 0) };
                pick.Click += (_, _) => period.Text = minutes.ToString(CultureInfo.InvariantCulture);
                shortcuts.Children.Add(pick);
            }
        }
        var amount = new TextBox
        {
            Width = 89, Text = filter ? "300" : price ? "8" : "800",
            Margin = new Thickness(0, 0, 5, 4), TextAlignment = TextAlignment.Right
        };
        AutomationProperties.SetName(amount, $"Новый порог, {unit}");
        composer.Children.Add(period);
        composer.Children.Add(periodLabel);
        composer.Children.Add(amount);
        composer.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
        var add = new Button { Content = filter ? "Добавить условие" : "Добавить период", Margin = new Thickness(0, 0, 0, 4) };
        composer.Children.Add(add);
        add.Click += (_, _) =>
        {
            int limit = filter ? Settings.MaxFilterSeconds / 60 : Settings.MaxModuleSeconds;
            if (!int.TryParse(period.Text, out int value) || value < 1 || value > limit ||
                !TryAmount(amount.Text, price, out double threshold))
            {
                validation.Text = $"Введите период 1–{limit} {(filter ? "мин" : "с")} и положительный порог.";
                return;
            }
            int seconds = filter ? value * 60 : value;
            var existing = GetRules();
            if (existing.Length >= Settings.MaxRules)
            {
                validation.Text = $"Допускается до {Settings.MaxRules} строк.";
                return;
            }
            if (!filter && existing.Any(r => r.Seconds == seconds))
            {
                validation.Text = "Этот период уже есть.";
                return;
            }
            Update(existing.Append(new VolumeRule(seconds, true, threshold)).ToArray());
            RenderRows();
        };

        RenderRows();
        return panel;

        VolumeRule[] GetRules() => kind switch
        {
            RuleKind.Price => settings.Thresholds.Select(r => new VolumeRule(r.Seconds, r.Enabled, r.Percent)).ToArray(),
            RuleKind.Volume => settings.VolumeThresholds, _ => settings.VolumeFilters
        };

        void Update(VolumeRule[] values)
        {
            if (price) settings.Thresholds = values.Select(r => new Threshold(r.Seconds, r.Enabled, r.Usd)).ToArray();
            else if (filter) settings.VolumeFilters = values;
            else settings.VolumeThresholds = values;
            changed(!filter);
        }

        void RenderRows()
        {
            validation.Text = "";
            rows.Children.Clear();
            var current = GetRules();
            for (int i = 0; i < current.Length; i++)
            {
                int index = i;
                var rule = current[i];
                string interval = MainWindow.WindowName(rule.Seconds);
                var row = new DockPanel { MinHeight = 39 };
                var enabled = new CheckBox
                {
                    Content = filter ? $"Мин. объём · {interval}" : interval, IsChecked = rule.Enabled,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var entry = new TextBox
                {
                    Text = rule.Usd.ToString(Russian), Width = 90, TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(entry, $"Порог за {interval}, {unit}");
                bool removable = filter || !Settings.Defaults.Any(r => r.Seconds == rule.Seconds);
                var remove = Terminal.IconButton("close", $"Удалить период {interval}");
                remove.Visibility = removable ? Visibility.Visible : Visibility.Hidden;
                remove.Margin = new Thickness(8, 0, 0, 0);
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(entry);
                right.Children.Add(new TextBlock { Text = unit, Width = 27, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                right.Children.Add(remove);
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(enabled);
                var frame = Terminal.Rule(new Thickness(0, 1, 0, index == current.Length - 1 ? 1 : 0));
                frame.Child = row;
                rows.Children.Add(frame);

                enabled.Checked += (_, _) => SetEnabled(true);
                enabled.Unchecked += (_, _) => SetEnabled(false);
                entry.TextChanged += (_, _) =>
                {
                    bool valid = TryAmount(entry.Text, price, out _);
                    entry.BorderBrush = Terminal.Brush(valid ? "Outline" : "Excluded");
                    validation.Text = valid ? "" : "Введите положительный порог.";
                };
                entry.LostKeyboardFocus += (_, _) => Commit();
                entry.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    Commit();
                    e.Handled = true;
                };
                remove.Click += (_, _) =>
                {
                    Update(GetRules().Where((_, at) => at != index).ToArray());
                    RenderRows();
                    add.Focus();
                };

                void SetEnabled(bool value)
                {
                    var values = GetRules();
                    if (values[index].Enabled == value) return;
                    values[index] = values[index] with { Enabled = value };
                    Update(values);
                }

                void Commit()
                {
                    if (!TryAmount(entry.Text, price, out double threshold)) return;
                    var values = GetRules();
                    if (values.Length <= index || values[index].Usd == threshold) return;
                    values[index] = values[index] with { Usd = threshold };
                    Update(values);
                }
            }
        }
    }

    private static bool TryAmount(string value, bool price, out double amount) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) &&
        amount > 0 && amount <= (price ? 10_000 : 1e12);

    private StackPanel Shortcuts()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Графики"));
        panel.Children.Add(Terminal.Copy("ЛКМ в области графика сдвигает обе оси. ЛКМ на шкале меняет её масштаб."));
        var ruler = AddShortcut("Линейка цены", settings.Ruler.Label);
        var fullscreen = AddShortcut("Полный экран", settings.Fullscreen.ToString());
        var up = AddShortcut("Следующий таймфрейм", settings.FrameUp.ToString());
        var down = AddShortcut("Предыдущий таймфрейм", settings.FrameDown.ToString());
        var frames = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (int frame in Frames.All)
        {
            var box = new CheckBox { Content = Frames.Label(frame), IsChecked = settings.FrameButtons.Contains(frame),
                Margin = new Thickness(0, 4, 12, 4) };
            box.Checked += (_, _) => { settings.FrameButtons = settings.FrameButtons.Append(frame).Distinct().Order().ToArray(); changed(false); };
            box.Unchecked += (_, _) =>
            {
                settings.FrameButtons = settings.FrameButtons.Where(x => x != frame).ToArray();
                changed(false);
            };
            frames.Children.Add(box);
        }
        panel.Children.Add(Terminal.Copy("Кнопки таймфреймов в ячейках"));
        panel.Children.Add(frames);
        panel.Children.Add(Terminal.Copy("Если линейка назначена на ЛКМ, она получает область графика. Шкалы и колесо остаются доступными."));
        shortcutStatus = Terminal.Copy("");
        shortcutStatus.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(shortcutStatus);
        ruler.Click += (_, _) => Capture("ruler", ruler);
        fullscreen.Click += (_, _) => Capture("fullscreen", fullscreen);
        up.Click += (_, _) => Capture("frame-up", up);
        down.Click += (_, _) => Capture("frame-down", down);
        PreviewKeyDown += (_, e) =>
        {
            if (capture == null) return;
            e.Handled = true;
            if (e.Key == Key.Escape) { EndCapture(); return; }
            if (e.IsRepeat) return;
            if (!(ChartGesture.Bindable(e.Key) || (capture is "frame-up" or "frame-down" && e.Key is Key.Up or Key.Down))
                || Keyboard.Modifiers != ModifierKeys.None)
            {
                shortcutStatus.Text = "Выберите букву, цифру или функциональную клавишу. Для ТФ доступны также ↑ и ↓.";
                return;
            }
            if ((capture != "ruler" && settings.Ruler.Mouse == null && settings.Ruler.Key == e.Key) ||
                (capture != "fullscreen" && settings.Fullscreen == e.Key) ||
                (capture != "frame-up" && settings.FrameUp == e.Key) ||
                (capture != "frame-down" && settings.FrameDown == e.Key))
            {
                shortcutStatus.Text = "Эта клавиша уже назначена другому действию.";
                return;
            }
            switch (capture)
            {
                case "ruler": settings.Ruler = new(e.Key); break;
                case "frame-up": settings.FrameUp = e.Key; break;
                case "frame-down": settings.FrameDown = e.Key; break;
                default: settings.Fullscreen = e.Key; break;
            }
            EndCapture();
            changed(false);
        };
        PreviewMouseDown += (_, e) =>
        {
            if (capture != "ruler" || captureButton?.IsMouseOver != true) return;
            settings.Ruler = new(Key.None, e.ChangedButton);
            e.Handled = true;
            EndCapture();
            changed(false);
        };
        return panel;

        Button AddShortcut(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 5) };
            var button = new Button { Content = value, MinWidth = 160 };
            AutomationProperties.SetName(button, $"Переназначить · {label}");
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
            return button;
        }

        void Capture(string action, Button button)
        {
            EndCapture();
            capture = action;
            captureButton = button;
            button.Content = "Ожидание ввода…";
            shortcutStatus.Text = action == "ruler" ? "Нажмите клавишу или кнопку мыши на этом поле. Esc — отмена."
                : "Нажмите клавишу. Esc — отмена.";
            button.Focus();
        }
    }

    private StackPanel OverviewOptions()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Листинги обзора"));
        panel.Children.Add(Terminal.Copy($"Источник списка · {settings.OverviewSource.Label}. Его можно сменить в таблице обзора. Условия ниже проверяют листинги на других площадках."));
        panel.Children.Add(ListingRules(overview: true));
        var separator = Terminal.Rule(new Thickness(0, 1, 0, 0));
        separator.Margin = new Thickness(0, 10, 0, 14);
        panel.Children.Add(separator);
        panel.Children.Add(Terminal.Heading("Колонки обзора"));
        panel.Children.Add(Terminal.Copy("Тикер всегда виден. ATR% — средний истинный диапазон за период, делённый на последнюю цену закрытия."));
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(list);
        var addBar = new WrapPanel();
        var types = new ComboBox { ItemsSource = new[] { "Оборот USD", "Изменение %", "ATR %" }, SelectedIndex = 0,
            Width = 135, Margin = new Thickness(0, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        var minutes = new TextBox { Text = "1440", Width = 60, Margin = new Thickness(0, 0, 5, 4) };
        AutomationProperties.SetName(minutes, "Период колонки, минуты");
        var add = new Button { Content = "Добавить", MinHeight = 26, Margin = new Thickness(0, 0, 0, 4) };
        addBar.Children.Add(types);
        addBar.Children.Add(minutes);
        addBar.Children.Add(new TextBlock { Text = "мин", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
        addBar.Children.Add(add);
        panel.Children.Add(addBar);
        var status = Terminal.Copy("");
        status.Foreground = Terminal.Brush("Excluded");
        panel.Children.Add(status);
        add.Click += (_, _) =>
        {
            if (!int.TryParse(minutes.Text, out int value) || value is < 1 or > 1440)
            { status.Text = "Введите период от 1 до 1440 минут."; return; }
            var metric = new OverviewColumn((MetricKind)types.SelectedIndex, value);
            if (settings.OverviewColumns.Length >= 5 || settings.OverviewColumns.Contains(metric))
            { status.Text = "До пяти разных колонок."; return; }
            settings.OverviewColumns = settings.OverviewColumns.Append(metric).ToArray();
            changed(false);
            Render();
        };
        Render();
        return panel;

        void Render()
        {
            status.Text = "";
            list.Children.Clear();
            foreach (var metric in settings.OverviewColumns)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = Terminal.IconButton("close", $"Удалить {OverviewMetrics.Label(metric)}");
                remove.Click += (_, _) =>
                {
                    settings.OverviewColumns = settings.OverviewColumns.Where(x => x != metric).ToArray();
                    settings.OverviewSort = -1;
                    changed(false);
                    Render();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(Terminal.Copy(OverviewMetrics.Label(metric), false));
                list.Children.Add(row);
            }
        }
    }

    private void EndCapture()
    {
        if (captureButton != null) captureButton.Content = capture switch
        {
            "ruler" => settings.Ruler.Label, "frame-up" => settings.FrameUp.ToString(), "frame-down" => settings.FrameDown.ToString(),
            _ => settings.Fullscreen.ToString()
        };
        capture = null;
        captureButton = null;
        if (shortcutStatus != null) shortcutStatus.Text = "";
    }
}
