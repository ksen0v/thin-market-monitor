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
    private static readonly Dictionary<string, BitmapImage> Logos = new(StringComparer.Ordinal);
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static FrameworkElement Icon(string name, double size = 15)
    {
        var path = new Path
        {
            Data = Geometry.Parse(name switch
            {
                "candles" => "M9 5 L9 3 M9 19 L9 21 M17 5 L17 3 M17 19 L17 21 M7 5 H11 V19 H7 Z M15 5 H19 V19 H15 Z",
                "line-chart" => "M3 17 L8 11 L13 14 L21 5",
                "dollar" => "M12 2 V22 M17 6 H10 A3 3 0 0 0 10 12 H14 A3 3 0 0 1 14 18 H7",
                "settings" => "M20 7 H10 M14 17 H4 M4 7 H6 M18 17 H20 M6 7 A2 2 0 1 0 10 7 A2 2 0 1 0 6 7 M14 17 A2 2 0 1 0 18 17 A2 2 0 1 0 14 17",
                "pin" => "M16 9 V4 H8 V9 A4 4 0 0 1 4 13 V15 H20 V13 A4 4 0 0 1 16 9 Z M12 15 V22",
                "pin-off" => "M2 2 L22 22 M12 15 V22 M5 15 H15 M9 4 H16 V9 Q16 11 18 12",
                "draw" => "M4 20 L6 15 L16 5 L19 8 L9 18 Z M14 7 L17 10 M12 20 H20",
                "horizontal-ray" => "M3 12 A2 2 0 1 0 7 12 A2 2 0 1 0 3 12 M7 12 H21 M17 8 L21 12 L17 16",
                "horizontal-line" => "M3 12 H21 M7 8 L3 12 L7 16 M17 8 L21 12 L17 16",
                "trend-ray" => "M3 18 A2 2 0 1 0 7 18 A2 2 0 1 0 3 18 M7 17 L21 5 M15 5 H21 V11",
                "segment" => "M3 18 A2 2 0 1 0 7 18 A2 2 0 1 0 3 18 M7 17 L17 7 M17 6 A2 2 0 1 0 21 6 A2 2 0 1 0 17 6",
                "signals-nav" => "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 M6.5 12 H9 L11 8.5 L13 15.5 L15 12 H17.5",
                "overview-nav" => "M5 3 H19 Q21 3 21 5 V19 Q21 21 19 21 H5 Q3 21 3 19 V5 Q3 3 5 3 Z " +
                    "M14 3 V21 M6 14.5 L8.5 11 L11 13 M17 8 H18 M17 12 H18 M17 16 H18",
                "settings-nav" => "M10.5 3 H13.5 L14 5.5 L15.2 6 L17.3 4.6 L19.4 6.7 L18 8.8 L18.5 10 L21 10.5 " +
                    "V13.5 L18.5 14 L18 15.2 L19.4 17.3 L17.3 19.4 L15.2 18 L14 18.5 L13.5 21 " +
                    "H10.5 L10 18.5 L8.8 18 L6.7 19.4 L4.6 17.3 L6 15.2 L5.5 14 L3 13.5 " +
                    "V10.5 L5.5 10 L6 8.8 L4.6 6.7 L6.7 4.6 L8.8 6 L10 5.5 Z " +
                    "M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12",
                "close" => "M6 6 L18 18 M18 6 L6 18",
                "ban" => "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 M5 5 L19 19",
                "listings" => "M12 2 L22 7 L12 12 L2 7 Z M2 12 L12 17 L22 12 M2 17 L12 22 L22 17",
                "density-nav" => "M12 3 V21 M4 6 H9 M2 10 H9 M6 14 H9 M3 18 H9 M15 6 H19 M15 10 H22 M15 14 H18 M15 18 H21",
                "activity" => "M2 12 H6 L9 4 L15 20 L18 12 H22",
                "volume" => "M4 20 H21 M7 17 V11 M12 17 V5 M17 17 V8",
                "bell" => "M4 17 H20 L18 14 V9 A6 6 0 0 0 6 9 V14 Z M10 21 H14",
                "pause" => "M9 5 V19 M15 5 V19",
                "play" => "M8 5 L19 12 L8 19 Z",
                "filter" => "M3 4 H21 L14 12 V20 L10 18 V12 Z",
                "info" => "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 M12 11 V16 M12 8 H12.1",
                "chevron" => "M7 10 L12 15 L17 10",
                "chevron-up" => "M7 14 L12 9 L17 14",
                "drawer-open" => "M3 4 H21 V20 H3 Z M15 4 V20 M17 8 H19 M17 12 H19 M17 16 H19",
                "drawer-closed" => "M3 4 H21 V20 H3 Z M15 4 V20 M7 12 H12 M10 10 L12 12 L10 14",
                "external" => "M14 3 H21 V10 M21 3 L11 13 M10 5 H4 V21 H20 V15",
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
        return new Viewbox { Child = path, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
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

    public static FrameworkElement VenueLogo(Venue venue, double size) =>
        Logo(VenueLogoSource(venue), size);

    public static ImageSource VenueLogoSource(Venue venue) =>
        AssetLogoSource((venue == Venue.Alpha ? Venue.Binance : venue).ToString().ToLowerInvariant());

    public static FrameworkElement AssetLogo(string asset, double size) => Logo(AssetLogoSource(asset), size);

    private static ImageSource AssetLogoSource(string asset)
    {
        if (!Logos.TryGetValue(asset, out var image))
        {
            image = new BitmapImage(new Uri($"pack://application:,,,/ThinMarketMonitor;component/Assets/{asset}.png"));
            image.Freeze();
            Logos.Add(asset, image);
        }
        return image;
    }

    private static Image Logo(ImageSource source, double size) => new()
    {
        Source = source, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
    };

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
