using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel VolumeBaselineOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(Terminal.Copy("Средний оборот окна = оборот предыдущей базы × длительность окна / длительность базы. Текущее окно в базу не входит."));
        var row = new WrapPanel { Margin = new Thickness(0, 7, 0, 5) };
        row.Children.Add(new TextBlock { Text = "Базовый период", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var minutes = new TextBox { Width = 65, Text = (settings.VolumeReferenceSeconds / 60).ToString(), TextAlignment = TextAlignment.Right };
        minutes.ToolTip = "По умолчанию 60 минут. Допустимо от 1 до 60 минут.";
        AutomationProperties.SetName(minutes, "База импульса объёма, минуты");
        row.Children.Add(minutes);
        row.Children.Add(new TextBlock { Text = "мин", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 8, 0) });
        var apply = new Button { Content = "Применить" };
        AutomationProperties.SetName(apply, "Применить базу импульса объёма");
        row.Children.Add(apply);
        panel.Children.Add(row);
        var validation = Terminal.Copy("");
        validation.Foreground = Terminal.Brush("Excluded");
        panel.Children.Add(validation);
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(minutes.Text, out int value) || value is < 1 or > Settings.MaxVolumeReferenceSeconds / 60)
            { validation.Text = $"Базовый период — от 1 до {Settings.MaxVolumeReferenceSeconds / 60} минут."; return; }
            validation.Text = "";
            if (settings.VolumeReferenceSeconds == value * 60) return;
            settings.VolumeReferenceSeconds = value * 60;
            changed(true);
        };
        panel.Children.Add(Terminal.Copy("После запуска или разрыва нужно накопить базу и окно из потока сделок. При нулевой базе сигналов нет."));
        panel.Children.Add(Terminal.Copy("Проверка раз в секунду с задержкой 6 с. Длинные окна готовы позже коротких."));
        return panel;
    }
}
