using System.Windows;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel BlacklistOptions()
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Чёрный список сигналов"));
        panel.Children.Add(Terminal.Copy("Кнопка ⊘ в заголовке каждой ячейки исключает монету из сигналов и их списка инструментов. Исключение действует для всех бирж, пар, Spot и Futures этой монеты."));
        panel.Children.Add(Terminal.Copy("Чтобы снова получать сигналы, удалите монету из списка ниже. Обзор остаётся доступен."));
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(list);
        Render();
        return panel;

        void Render()
        {
            list.Children.Clear();
            if (settings.SignalBlacklist.Length == 0)
            {
                list.Children.Add(Terminal.Copy("Список пуст"));
                return;
            }
            foreach (string coin in settings.SignalBlacklist)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                var remove = Terminal.IconButton("close", $"Вернуть {coin} в сигналы");
                remove.Click += (_, _) =>
                {
                    settings.SignalBlacklist = settings.SignalBlacklist.Where(c => c != coin).ToArray();
                    changed(false);
                    Render();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var label = Terminal.Copy(coin, false);
                label.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(label);
                list.Children.Add(row);
            }
        }
    }
}
