using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal readonly record struct DensityCardKey(string Asset, string Title, Segment Segment, string Delivery, QuantityUnit? Unit)
{
    internal static DensityCardKey For(Market market)
    {
        var contract = market.Contract;
        string priceBase = contract?.NativeBase ?? market.Base;
        if (priceBase == "XBT" && market.Base == "BTC") priceBase = market.Base;
        bool delivery = contract is { Perpetual: false };
        string title = delivery || market.ListingAsset.StartsWith("hyperliquid:", StringComparison.Ordinal) ||
            market.ListingAsset.StartsWith("lighter:", StringComparison.Ordinal) ? market.Ticker : $"{priceBase}/{market.Quote}";
        return new(market.ListingAsset, title, market.Source.Segment,
            delivery ? market.Key : "", contract?.Unit);
    }
}

internal sealed class DensityCard : Border
{
    internal const double HeaderHeight = 29;
    internal const double RowHeight = 23;
    private readonly DensityRows rows;
    private readonly Button terminal;
    private readonly Action<Market> openInTerminal;
    private DensityLevel[] levels;

    public DensityCard(DensityCardKey key, DensityLevel[] levels, Action<Market> editCoin, Action<Market> blockCoin,
        Action<Market> openInTerminal)
    {
        this.levels = levels;
        this.openInTerminal = openInTerminal;
        Background = Terminal.Brush("Plot");
        BorderBrush = Terminal.Brush("Outline");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, $"{key.Title} · {key.Segment}");

        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(6, 0, 3, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        terminal = Terminal.IconButton("external", $"Открыть в Colibri · {key.Title}");
        terminal.Content = Terminal.AssetLogo("colibri", 15);
        terminal.Visibility = Visibility.Collapsed;
        terminal.Click += (_, _) => OpenTerminal();
        Unloaded += (_, _) => CloseTerminalMenu();
        var settings = Terminal.IconButton("settings-nav", $"Настройки монеты · {key.Title}");
        var blacklist = Terminal.IconButton("ban", $"В чёрный список плотностей · {key.Title}");
        foreach (var button in new[] { terminal, settings, blacklist })
        {
            button.Width = 25;
            button.Height = 23;
            button.MinHeight = 0;
            button.Padding = new Thickness(0);
            button.Foreground = Terminal.Brush("Muted");
            actions.Children.Add(button);
        }
        settings.Click += (_, _) => editCoin(this.levels[0].Market);
        blacklist.Click += (_, _) => blockCoin(this.levels[0].Market);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var segment = new Border
        {
            Background = Terminal.Brush("Control"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = key.Segment.ToString(), FontSize = 10, Foreground = Terminal.Brush("Muted") }
        };
        DockPanel.SetDock(segment, Dock.Right);
        header.Children.Add(segment);
        header.Children.Add(new TextBlock
        {
            Text = key.Title, ToolTip = key.Title, FontWeight = FontWeights.SemiBold, Foreground = Terminal.Brush("Text"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var surface = new Border
        {
            Height = HeaderHeight, Background = Terminal.Brush("Surface"), BorderBrush = Terminal.Brush("Outline"),
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(4, 4, 0, 0), Child = header
        };
        DockPanel.SetDock(surface, Dock.Top);
        panel.Children.Add(surface);
        rows = new DensityRows(levels);
        panel.Children.Add(rows);
        Child = panel;
    }

    internal void Update(DensityLevel[] levels)
    {
        this.levels = levels;
        rows.Update(levels);
    }

    internal void SetColibriEnabled(bool enabled)
    {
        terminal.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) CloseTerminalMenu();
    }

    private void OpenTerminal()
    {
        if (terminal.Visibility != Visibility.Visible) return;
        if (terminal.ContextMenu is { IsOpen: true }) { CloseTerminalMenu(); return; }
        var markets = levels.Select(level => level.Market).DistinctBy(market => market.Key)
            .OrderBy(market => market.Venue).ThenBy(market => market.Key, StringComparer.Ordinal).ToArray();
        if (markets.Length == 1)
        {
            openInTerminal(markets[0]);
            return;
        }

        var menu = new ContextMenu
        {
            Style = Terminal.Style("TerminalMenu"), PlacementTarget = terminal, Placement = PlacementMode.Bottom, MinWidth = 176
        };
        foreach (var market in markets)
        {
            var item = new MenuItem
            {
                Header = $"{market.Source.Label} · {market.Ticker}", Icon = Terminal.VenueLogo(market.Venue, 14),
                Style = Terminal.Style("TerminalMenuItem")
            };
            item.Click += (_, _) =>
            {
                if (terminal.Visibility == Visibility.Visible &&
                    levels.FirstOrDefault(level => level.Market.Key == market.Key) is { } current)
                    openInTerminal(current.Market);
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(terminal.ContextMenu, menu)) terminal.ContextMenu = null; };
        terminal.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void CloseTerminalMenu()
    {
        if (terminal.ContextMenu is { } menu) menu.IsOpen = false;
    }

    internal void SetViewport(double top, double bottom) => rows.SetViewport(top - HeaderHeight - 1, bottom - HeaderHeight - 1);

    private sealed class DensityRows : FrameworkElement
    {
        private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
        private readonly Typeface face = new((FontFamily)Application.Current.FindResource("TerminalFont"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly Brush ink = Terminal.Brush("Text");
        private readonly Brush muted = Terminal.Brush("Muted");
        private readonly Brush bid = Terminal.Brush("Rising");
        private readonly Brush ask = Terminal.Brush("Falling");
        private readonly Brush alternate = Terminal.Brush("Shell");
        private readonly Dictionary<string, Brush> fills = new(StringComparer.Ordinal);
        private readonly ToolTip details = new();
        private DensityLevel[] levels = [];
        private double scale;
        private int firstRow;
        private int lastRow;

        internal DensityRows(DensityLevel[] levels)
        {
            ClipToBounds = true;
            ToolTip = details;
            ToolTipOpening += (_, e) => e.Handled = !DescribeRow(Mouse.GetPosition(this).Y);
            MouseMove += (_, e) =>
            {
                if (details.IsOpen) DescribeRow(e.GetPosition(this).Y);
            };
            Update(levels);
        }

        internal void Update(DensityLevel[] next)
        {
            levels = next;
            Height = RowHeight * levels.Length;
            scale = levels.Max(level => level.Usd);
            InvalidateVisual();
        }

        internal void SetViewport(double top, double bottom)
        {
            int first = Math.Clamp((int)Math.Floor(top / RowHeight), 0, levels.Length);
            int last = Math.Clamp((int)Math.Ceiling(bottom / RowHeight), first, levels.Length);
            if (firstRow == first && lastRow == last) return;
            firstRow = first;
            lastRow = last;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 80) return;
            const double left = 6, logoWidth = 21, right = 5;
            double width = ActualWidth - left - logoWidth - right;
            double amountWidth = width * .32, ageWidth = width * .18, priceWidth = width * .28;
            double logo = left + amountWidth, age = logo + logoWidth, price = age + ageWidth, distance = price + priceWidth;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = firstRow; i < Math.Min(lastRow, levels.Length); i++)
            {
                var level = levels[i];
                double y = i * RowHeight;
                var side = level.Key.Side == BookSide.Bid ? bid : ask;
                dc.DrawRectangle(i % 2 == 0 ? Brushes.Transparent : alternate, null, new Rect(0, y, ActualWidth, RowHeight));
                dc.DrawRectangle(side, null, new Rect(0, y + 2, 2, RowHeight - 4));
                if (scale > 0)
                {
                    double bar = Math.Max(6, (amountWidth - 3) * Math.Sqrt(level.Usd / scale));
                    dc.DrawRoundedRectangle(Fill(level.Color), null, new Rect(left, y + 3, bar, RowHeight - 6), 2, 2);
                }
                Draw(dc, "$" + ChartVolume.CompactUsd(level.Usd, "K"), left + 3, y, amountWidth - 5, ink, dpi);
                dc.DrawImage(Terminal.VenueLogoSource(level.Market.Venue), new Rect(logo + 2, y + (RowHeight - 14) / 2, 14, 14));
                Draw(dc, Age(level), age, y, ageWidth - 3, muted, dpi);
                Draw(dc, level.Key.Price.ToString("G9", Russian), price, y, priceWidth - 3, ink, dpi, alignRight: true);
                Draw(dc, level.Distance.ToString("0.##", Russian) + "%", distance, y, ActualWidth - distance - right, side, dpi,
                    alignRight: true);
            }
        }

        private void Draw(DrawingContext dc, string value, double x, double y, double width, Brush brush, double dpi, bool alignRight = false)
        {
            if (width < 4) return;
            var text = new FormattedText(value, Russian, FlowDirection.LeftToRight, face, 11, brush, dpi)
            {
                MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left
            };
            dc.DrawText(text, new Point(x, y + (RowHeight - text.Height) / 2));
        }

        private Brush Fill(string color)
        {
            if (fills.TryGetValue(color, out var fill)) return fill;
            var start = DensityTint.Read(color).Color;
            fill = new LinearGradientBrush(Color.FromArgb(115, start.R, start.G, start.B),
                Color.FromArgb(25, start.R, start.G, start.B), 0);
            fill.Freeze();
            return fills[color] = fill;
        }

        private static string Age(DensityLevel level)
        {
            long seconds = Math.Max(0, (level.Seen - level.FirstSeen) / 1000);
            return seconds >= 3600 ? $"{seconds / 3600}ч {seconds / 60 % 60}м" :
                seconds >= 60 ? $"{seconds / 60}м {seconds % 60}с" : $"{seconds}с";
        }

        private bool DescribeRow(double y)
        {
            int index = (int)Math.Floor(y / RowHeight);
            if (index < 0 || index >= levels.Length) return false;
            var level = levels[index];
            details.Content = $"{level.Market.Source.Label} · {level.SideLabel}\n" +
                $"Размер: ${level.Usd.ToString("N2", Russian)}\n" +
                $"Цена: {level.Key.Price.ToString("G12", Russian)} · {level.Distance.ToString("0.##", Russian)}% от рынка · {Age(level)}";
            return true;
        }
    }
}
