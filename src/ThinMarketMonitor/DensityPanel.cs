using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class DensityPanel : DockPanel
{
    private sealed class Row(DensityLevel level) : INotifyPropertyChanged
    {
        public DensityLevel Level { get; private set; } = level;
        public string Ticker => Level.Market.Ticker;
        public string Source => Level.Market.Source.Label;
        public string Side => Level.SideLabel;
        public double Price => Level.Key.Price;
        public double Size => Level.Usd;
        public double Distance => Level.Distance;
        public double Age => (Level.Seen - Level.FirstSeen) / 1000d;
        public DateTime FirstSeen => DateTimeOffset.FromUnixTimeMilliseconds(Level.FirstSeen).LocalDateTime;
        public string Tier => Level.Large ? "2" : "1";
        public Brush Color { get; private set; } = DensityTint.Read(level.Color);
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(DensityLevel value)
        {
            if (value == Level) return;
            if (value.Color != Level.Color) Color = DensityTint.Read(value.Color);
            Level = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private readonly ObservableCollection<Row> rows = [];
    private readonly Dictionary<(string Market, DensityKey Key), Row> byKey = [];
    private readonly TextBlock status = Terminal.Copy("");

    public DensityPanel(Action openSettings)
    {
        var top = new DockPanel { Margin = new Thickness(8, 4, 8, 7) };
        var settings = new Button { Content = Terminal.LabelWithIcon("settings", "Настройки плотностей", 5) };
        settings.Click += (_, _) => openSettings();
        DockPanel.SetDock(settings, Dock.Right);
        top.Children.Add(settings);
        var title = Terminal.Heading("Плотности стакана");
        title.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(title);
        SetDock(top, Dock.Top);
        Children.Add(top);

        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(8, 6, 8, 4);
        SetDock(status, Dock.Bottom);
        Children.Add(status);

        var table = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, AlternationCount = 2,
            CanUserResizeRows = false, CanUserAddRows = false, CanUserDeleteRows = false,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Terminal.Brush("Plot"), BorderBrush = Terminal.Brush("Outline"),
            RowStyle = Terminal.Style("OverviewRow"), CellStyle = Terminal.Style("OverviewCell"),
            ColumnHeaderStyle = Terminal.Style("OverviewColumnHeader"), RowHeight = 26
        };
        AutomationProperties.SetName(table, "Найденные плотности");
        VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling);
        Add("Тикер", nameof(Row.Ticker), 116, expand: true);
        Add("Биржа", nameof(Row.Source), 122, expand: true);
        Add("Сторона", nameof(Row.Side), 83);
        Add("Цена", nameof(Row.Price), 85, "G10");
        Add("Размер ≈ USD", nameof(Row.Size), 110, "N0");
        Add("От цены, %", nameof(Row.Distance), 95, "0.00");
        Add("Стоит, с", nameof(Row.Age), 74, "0");
        Add("Замечена", nameof(Row.FirstSeen), 81, "HH:mm:ss");
        Add("Уровень", nameof(Row.Tier), 67, tint: true);
        Children.Add(table);

        void Add(string caption, string property, double width, string? format = null, bool expand = false, bool tint = false)
        {
            var binding = new Binding(property);
            if (format != null) binding.StringFormat = "{0:" + format + "}";
            var column = new DataGridTextColumn
            {
                Header = caption, Binding = binding, SortMemberPath = property,
                Width = expand ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                MinWidth = expand ? width : 55
            };
            if (tint)
            {
                var style = new Style(typeof(TextBlock), column.ElementStyle);
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(Row.Color))));
                style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
                column.ElementStyle = style;
            }
            table.Columns.Add(column);
        }
    }

    public void Update(DensityLevel[] levels, DensityVenueStatus[] venues, string[] pending)
    {
        var keys = new HashSet<(string, DensityKey)>();
        foreach (var level in levels)
        {
            var key = (level.Market.Key, level.Key);
            keys.Add(key);
            if (byKey.TryGetValue(key, out var row)) row.Update(level);
            else
            {
                byKey.Add(key, row = new(level));
                rows.Add(row);
            }
        }
        foreach (var (key, row) in byKey)
            if (!keys.Contains(key))
            {
                rows.Remove(row);
                byKey.Remove(key);
            }

        int failed = venues.Sum(v => v.Failed), limited = venues.Sum(v => v.Limited);
        status.Text = venues.Length == 0 ? "Выберите биржи и сегменты в настройках плотностей." :
            $"Плотностей: {rows.Count:N0} · Проверено стаканов: {venues.Sum(v => v.Sampled):N0} / {venues.Sum(v => v.Markets):N0}" +
            (pending.Length > 0 ? $" · Каталоги: {pending.Length}" : "") +
            (limited > 0 ? $" · Глубина ограничена: {limited:N0}" : "") +
            (failed > 0 ? $" · Ошибки: {failed:N0}" : "");
        status.ToolTip = venues.Length == 0 ? null : string.Join("\n", venues.Select(v =>
            $"{v.Venue.Label()} · {v.Sampled}/{v.Markets} · цикл ≈ {v.CycleSeconds} с" +
            (v.Error != null ? $"\n{v.Error}" : "")).Concat(pending));
    }
}

internal static class DensityTint
{
    public static SolidColorBrush Read(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
