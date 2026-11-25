using System.Windows;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel BlacklistOptions(bool densities, Action save)
    {
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading(densities ? "Чёрный список плотностей" : "Чёрный список сигналов"));
        panel.Children.Add(Terminal.Copy(densities ? "Монеты, исключённые из поиска плотностей на всех биржах."
            : "Монеты, исключённые из сигналов на всех биржах."));
        if (!densities)
            AddDiagnostic(panel, "Кнопка ⊘ на графике исключает монету во всех парах и сегментах. Удаление из списка возвращает сигналы.");
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(list);
        Render();
        return panel;

        void Render()
        {
            list.Children.Clear();
            var coins = densities ? settings.Densities.Blacklist : settings.SignalBlacklist;
            if (coins.Length == 0)
            {
                list.Children.Add(Terminal.Copy("Список пуст"));
                return;
            }
            foreach (string coin in coins)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                var remove = Terminal.IconButton("close", $"Вернуть {coin} в {(densities ? "плотности" : "сигналы")}");
                remove.Click += (_, _) =>
                {
                    var next = coins.Where(c => c != coin).ToArray();
                    if (densities) settings.Densities.Blacklist = next;
                    else settings.SignalBlacklist = next;
                    save();
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
