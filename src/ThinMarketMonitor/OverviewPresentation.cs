using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed partial class OverviewPanel
{
    private readonly Action savePresentation;
    private readonly DispatcherTimer fitTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
    private double tickerFit;
    private double[] metricFits = [];
    private DataGridColumn? draggingColumn;
    private double dragStartWidth;
    private Button? priorityButton;
    private ContextMenu? bookmarkMenu;

    private static readonly IReadOnlyDictionary<BookmarkColor, SolidColorBrush> bookmarkColors = new Dictionary<BookmarkColor, SolidColorBrush>
    {
        [BookmarkColor.Gold] = Swatch(237, 191, 105),
        [BookmarkColor.Green] = Swatch(161, 220, 133),
        [BookmarkColor.Blue] = Swatch(126, 200, 246),
        [BookmarkColor.Pink] = Swatch(244, 157, 173),
        [BookmarkColor.Violet] = Swatch(194, 168, 253)
    };

    private static SolidColorBrush Swatch(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Brush ColorBrush(BookmarkColor color) => bookmarkColors[color];
    private static string ColorName(BookmarkColor color) => color switch
    {
        BookmarkColor.Gold => "Золотая", BookmarkColor.Green => "Зелёная", BookmarkColor.Blue => "Голубая",
        BookmarkColor.Pink => "Розовая", _ => "Фиолетовая"
    };

    private BookmarkColor? BookmarkFor(Market market) =>
        settings.OverviewBookmarks.TryGetValue(market.Key, out var value) ? value : null;

    private DockPanel TickerHeader()
    {
        var header = new DockPanel();
        priorityButton = new Button { Style = Terminal.Style("OverviewBookmarkButton"), Margin = new Thickness(-6, 0, 3, 0) };
        RefreshPriorityButton();
        SetDock(priorityButton, Dock.Left);
        header.Children.Add(priorityButton);
        header.Children.Add(new TextBlock { Text = "Тикер", VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    private void RefreshPriorityButton()
    {
        if (priorityButton == null) return;
        var color = settings.OverviewBookmarkPriority;
        var brush = color is { } chosen ? ColorBrush(chosen) : null;
        priorityButton.Foreground = brush ?? Terminal.Brush("Muted");
        priorityButton.Tag = brush;
        string hint = color is { } current ? $"Приоритет закладок · {ColorName(current)}" : "Приоритет закладок · нет";
        priorityButton.ToolTip = hint;
        AutomationProperties.SetName(priorityButton, hint);
    }

    private bool IsBookmarkButton(Button? button) =>
        button != null && (button == priorityButton || ReferenceEquals(button.Style, Terminal.Style("OverviewBookmarkButton")));

    private void OverviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var element = e.OriginalSource as DependencyObject;
        if (Ancestor<Button>(element) is { } button && IsBookmarkButton(button))
        {
            e.Handled = true;
            OpenBookmarkMenu(button);
            return;
        }
        if (ResizeGripColumn(element) is { } column)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                settings.OverviewColumnWidths.Remove(WidthKey(column));
                FitAutomaticColumns();
                savePresentation();
            }
            return;
        }
        MarkUserSelection();
    }

    private void OverviewButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button button || !IsBookmarkButton(button)) return;
        e.Handled = true;
        OpenBookmarkMenu(button);
    }

    private void OpenBookmarkMenu(Button button)
    {
        bool priority = button == priorityButton;
        var row = priority ? null : Ancestor<DataGridRow>(button)?.Item as Row;
        if (!priority && row == null) return;
        if (bookmarkMenu != null) bookmarkMenu.IsOpen = false;
        var selected = priority ? settings.OverviewBookmarkPriority : row!.Bookmark;
        bookmarkMenu = new ContextMenu { Style = Terminal.Style("TerminalMenu"), PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (color, brush) in bookmarkColors)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(2),
                Background = brush, Margin = new Thickness(0, 0, 8, 0) });
            label.Children.Add(new TextBlock { Text = ColorName(color) });
            AddOption(color, label, ColorName(color));
        }
        string clear = priority ? "Без приоритета" : "Снять закладку";
        AddOption(null, clear, clear);
        bookmarkMenu.IsOpen = true;

        void AddOption(BookmarkColor? color, object label, string name)
        {
            var option = new MenuItem { Header = label, Style = Terminal.Style("TerminalMenuItem"), IsChecked = selected == color };
            AutomationProperties.SetName(option, name);
            option.Click += (_, _) =>
            {
                if (selected == color) return;
                if (priority)
                {
                    settings.OverviewBookmarkPriority = color;
                    RefreshPriorityButton();
                    SortFromHeader();
                }
                else if (row != null && rows.Contains(row))
                {
                    if (color is { } chosen) settings.OverviewBookmarks[row.Market.Key] = chosen;
                    else settings.OverviewBookmarks.Remove(row.Market.Key);
                    var prioritized = settings.OverviewBookmarkPriority;
                    bool regroup = prioritized != null && (row.Bookmark == prioritized || color == prioritized);
                    row.SetBookmark(color);
                    if (regroup) RefreshSort();
                }
                savePresentation();
            };
            bookmarkMenu!.Items.Add(option);
        }
    }

    private DataGridColumn? ResizeGripColumn(DependencyObject? element) =>
        Ancestor<Thumb>(element) is { Name: "PART_RightHeaderGripper" } grip &&
        Ancestor<DataGridColumnHeader>(grip) is { Column: { } column } && table.Columns.Contains(column) ? column : null;

    private void ColumnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (ResizeGripColumn(e.OriginalSource as DependencyObject) is not { } column) return;
        draggingColumn = column;
        dragStartWidth = column.ActualWidth;
    }

    private void ColumnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (draggingColumn is not { } column) return;
        draggingColumn = null;
        if (!e.Canceled && table.Columns.Contains(column) && Math.Abs(column.Width.DisplayValue - dragStartWidth) >= 0.5)
        {
            settings.OverviewColumnWidths[WidthKey(column)] = Math.Clamp(column.Width.DisplayValue, column.MinWidth, column.MaxWidth);
            savePresentation();
        }
        ApplyColumnWidths();
    }

    private string WidthKey(DataGridColumn column)
    {
        int index = table.Columns.IndexOf(column) - 1;
        return index < 0 ? "ticker" : columns[index].WidthKey;
    }

    private void QueueColumnFit()
    {
        if (!fitTimer.IsEnabled) fitTimer.Start();
    }

    private void FitAutomaticColumns()
    {
        if (table.Columns.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(table).PixelsPerDip;
        var text = new Typeface(table.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ticker = new Typeface(table.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        if (tickerFit == 0)
        {
            double max = 0;
            foreach (string name in rows.Select(row => row.Ticker).Distinct(StringComparer.Ordinal))
                max = Math.Max(max, Measure(name, ticker, table.FontSize));
            tickerFit = Math.Ceiling(max + 76 + Measure("Futures", text, 10));
        }
        double digit = Measure("0000000000", text, table.FontSize) / 10;
        for (int index = 0; index < columns.Length; index++)
        {
            var column = table.Columns[index + 1];
            if (column == draggingColumn || settings.OverviewColumnWidths.ContainsKey(columns[index].WidthKey)) continue;
            double header = Measure((string)column.Header, text, 11) + 41;
            string longest = rows.Select(row => row[index]).MaxBy(value => value.Length) ?? "";
            double content = Math.Max(Measure(longest, text, table.FontSize), longest.Length * digit) + 26;
            metricFits[index] = Math.Max(metricFits[index], Math.Ceiling(Math.Max(header, content)));
        }
        ApplyColumnWidths();

        double Measure(string value, Typeface font, double size) => new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, font, size, Terminal.Brush("Text"), dpi).WidthIncludingTrailingWhitespace;
    }

    private void ApplyColumnWidths()
    {
        for (int index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            if (column == draggingColumn) continue;
            double automatic = index == 0 ? Math.Max(118, tickerFit) : Math.Max(columns[index - 1].Kind == MetricKind.Turnover ? 105 : 78,
                metricFits[index - 1]);
            double width = Math.Clamp(settings.OverviewColumnWidths.GetValueOrDefault(WidthKey(column), automatic), column.MinWidth, column.MaxWidth);
            if (!column.Width.IsAbsolute || column.Width.Value != width) column.Width = new DataGridLength(width);
        }
    }
}
