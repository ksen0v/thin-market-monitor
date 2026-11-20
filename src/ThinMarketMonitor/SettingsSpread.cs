using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel SpreadOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(Terminal.Heading("Спред, %"));
        panel.Children.Add(Terminal.Copy("(Лучший ask − лучший bid) / средняя цена bid и ask × 100%."));
        panel.Children.Add(Terminal.Copy("Для сигналов и их списка инструментов. Пустая граница — без ограничения. Границы включаются в диапазон."));
        var enabled = new CheckBox { Content = "Фильтровать по спреду", IsChecked = settings.SpreadFilter.Enabled,
            Margin = new Thickness(0, 10, 0, 7) };
        AutomationProperties.SetName(enabled, "Фильтровать по спреду");
        panel.Children.Add(enabled);
        var row = new WrapPanel();
        panel.Children.Add(row);
        TextBox Entry(string label, double? value)
        {
            var field = new StackPanel { Margin = new Thickness(0, 0, 9, 0) };
            field.Children.Add(Terminal.Copy(label, false));
            var entry = new TextBox { Width = 115, Text = value?.ToString(CultureInfo.GetCultureInfo("ru-RU")) ?? "",
                TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetName(entry, label);
            field.Children.Add(entry);
            row.Children.Add(field);
            return entry;
        }
        var minimum = Entry("Мин. спред, %", settings.SpreadFilter.Minimum);
        var maximum = Entry("Макс. спред, %", settings.SpreadFilter.Maximum);
        var apply = new Button { Content = "Применить", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(apply, "Применить диапазон спреда");
        row.Children.Add(apply);
        var validation = Terminal.Copy("");
        validation.Foreground = Terminal.Brush("Excluded");
        validation.Margin = new Thickness(0, 5, 0, 5);
        panel.Children.Add(validation);
        panel.Children.Add(Terminal.Copy("Котировки обновляются для всех инструментов после листингов. Пока bid/ask отсутствуют или устарели, инструмент не проходит фильтр. При выключенном фильтре котировки нужны только для добавленной колонки спреда в открытом обзоре."));

        bool Valid(out SpreadRule rule)
        {
            bool valid = SpreadRule.TryParse(minimum.Text, maximum.Text, enabled.IsChecked == true, out rule);
            validation.Text = valid ? "" : "Нужны числа от 0; минимум не должен превышать максимум.";
            minimum.BorderBrush = maximum.BorderBrush = Terminal.Brush(valid ? "Outline" : "Excluded");
            return valid;
        }
        void Commit()
        {
            if (!Valid(out var rule) || rule == settings.SpreadFilter) return;
            settings.SpreadFilter = rule;
            changed(false);
        }
        enabled.Click += (_, _) =>
        {
            if (enabled.IsChecked != true)
            {
                settings.SpreadFilter = settings.SpreadFilter with { Enabled = false };
                changed(false);
            }
            else if (Valid(out _)) Commit();
            else enabled.IsChecked = false;
        };
        apply.Click += (_, _) => Commit();
        foreach (var entry in new[] { minimum, maximum })
        {
            entry.TextChanged += (_, _) => Valid(out _);
            entry.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Commit();
                e.Handled = true;
            };
        }
        return panel;
    }
}
