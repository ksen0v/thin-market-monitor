using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ThinMarketMonitor;

internal static class Terminal
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static FrameworkElement Icon(string name)
    {
        var path = new Path
        {
            Data = Geometry.Parse(name switch
            {
                "candles" => "M9 5 L9 3 M9 19 L9 21 M17 5 L17 3 M17 19 L17 21 M7 5 H11 V19 H7 Z M15 5 H19 V19 H15 Z",
                "settings" => "M20 7 H10 M14 17 H4 M4 7 H6 M18 17 H20 M6 7 A2 2 0 1 0 10 7 A2 2 0 1 0 6 7 M14 17 A2 2 0 1 0 18 17 A2 2 0 1 0 14 17",
                "pin" => "M16 9 V4 H8 V9 A4 4 0 0 1 4 13 V15 H20 V13 A4 4 0 0 1 16 9 Z M12 15 V22",
                "pin-off" => "M2 2 L22 22 M12 15 V22 M5 15 H15 M9 4 H16 V9 Q16 11 18 12",
                "latest" => "M15 14 L20 9 L15 4 M20 9 H9 A4 4 0 0 0 5 13 V20",
                "close" => "M6 6 L18 18 M18 6 L6 18",
                "listings" => "M12 2 L22 7 L12 12 L2 7 Z M2 12 L12 17 L22 12 M2 17 L12 22 L22 17",
                "activity" => "M2 12 H6 L9 4 L15 20 L18 12 H22",
                "volume" => "M4 20 H21 M7 17 V11 M12 17 V5 M17 17 V8",
                "filter" => "M3 4 H21 L14 12 V20 L10 18 V12 Z",
                "info" => "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 M12 11 V16 M12 8 H12.1",
                "chevron" => "M7 10 L12 15 L17 10",
                "drawer-open" => "M3 4 H21 V20 H3 Z M15 4 V20 M17 8 H19 M17 12 H19 M17 16 H19",
                "drawer-closed" => "M3 4 H21 V20 H3 Z M15 4 V20 M7 12 H12 M10 10 L12 12 L10 14",
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            }),
            Width = 24, Height = 24, StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetBinding(Shape.StrokeProperty, new Binding
        {
            Path = new PropertyPath("(0)", TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self
        });
        return new Viewbox { Child = path, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
    }

    public static StackPanel LabelWithIcon(string icon, string label, double gap)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(Icon(icon));
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return stack;
    }

    public static Button IconButton(string icon, string label)
    {
        var button = new Button { Content = Icon(icon), ToolTip = label, Style = Style("IconButton") };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    public static FrameworkElement VenueLogo(Venue venue, double size)
    {
        string asset = (venue == Venue.Alpha ? Venue.Binance : venue).ToString().ToLowerInvariant();
        return new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/ThinMarketMonitor;component/Assets/{asset}.png")),
            Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static void AttachMenu(Button button, IEnumerable<MenuItem> items, double minWidth)
    {
        var menu = new ContextMenu { Style = Style("TerminalMenu"), MinWidth = minWidth, PlacementTarget = button };
        foreach (var item in items)
        {
            item.Style = Style("TerminalMenuItem");
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = !menu.IsOpen;
    }

    public static Border Rule(Thickness thickness) => new()
    {
        BorderBrush = Brush("Outline"), BorderThickness = thickness
    };

    public static DockPanel DialogHeader(string title, string closeLabel, Action close)
    {
        var header = new DockPanel { Margin = new Thickness(16, 6, 10, 6) };
        var button = IconButton("close", closeLabel);
        button.MinWidth = button.MinHeight = 28;
        button.Background = Brush("Surface");
        button.BorderBrush = Brush("Outline");
        button.Click += (_, _) => close();
        DockPanel.SetDock(button, Dock.Right);
        header.Children.Add(button);
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    public static TextBlock Copy(string text, bool muted = true) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(muted ? "Muted" : "Text"), LineHeight = 17
    };

    public static TextBlock Heading(string text, bool accent = false) => new()
    {
        Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7),
        Foreground = Brush(accent ? "Accent" : "Text")
    };
}
