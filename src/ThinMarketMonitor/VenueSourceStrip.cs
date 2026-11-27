using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed class VenueSourceStrip : WrapPanel
{
    private readonly Func<MarketSource, bool> enabled;
    private readonly Dictionary<Venue, (Button Main, Button? Segment, Dictionary<MarketSource, MenuItem> Items)> buttons = [];

    public VenueSourceStrip(string scope, Func<MarketSource, bool> enabled, Action<Venue> toggleVenue,
        Action<MarketSource> toggleSegment, Action changed)
    {
        this.enabled = enabled;
        foreach (var group in MarketSource.All.GroupBy(source => source.Venue))
        {
            var venue = group.Key;
            var label = venue.Label();
            var groupPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 3) };
            var caption = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(Terminal.VenueLogo(venue, 15));
            caption.Children.Add(new TextBlock { Text = label, Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center });
            var main = new Button
            {
                Tag = venue, Content = caption,
                MinHeight = 25, FontSize = 11, Padding = new Thickness(6, 1, 6, 1)
            };
            AutomationProperties.SetName(main, $"{scope} · {label}");
            main.Click += (_, _) =>
            {
                toggleVenue(venue);
                Refresh();
                changed();
            };
            groupPanel.Children.Add(main);
            var items = new Dictionary<MarketSource, MenuItem>();
            Button? segment = null;
            if (group.Count() > 1)
            {
                segment = Terminal.IconButton("chevron", $"Рынки · {label}");
                segment.Content = Terminal.Icon("chevron", 11);
                segment.MinHeight = 25;
                segment.Width = 19;
                segment.Padding = new Thickness(0);
                foreach (var source in group)
                {
                    var item = new MenuItem { Header = source.Segment.ToString(), IsCheckable = true, StaysOpenOnClick = true };
                    AutomationProperties.SetName(item, source.Label);
                    item.Click += (_, _) =>
                    {
                        toggleSegment(source);
                        Refresh();
                        changed();
                    };
                    items.Add(source, item);
                }
                Terminal.AttachMenu(segment, items.Values, 130);
                groupPanel.Children.Add(segment);
            }
            buttons.Add(venue, (main, segment, items));
            Children.Add(groupPanel);
        }
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var (venue, (main, segment, items)) in buttons)
        {
            foreach (var (source, item) in items) item.IsChecked = enabled(source);
            string caption = items.Count == 0 ? enabled(new MarketSource(venue)) ? "Spot" : "" :
                string.Join(" + ", items.Where(pair => pair.Value.IsChecked).Select(pair => pair.Key.Segment));
            bool active = caption.Length != 0;
            string state = active ? caption : "Выключена";
            main.ToolTip = $"{venue.Label()} · {state}";
            AutomationProperties.SetItemStatus(main, state);
            main.BorderBrush = Terminal.Brush(active ? "Accent" : "Outline");
            main.Background = Terminal.Brush(active ? "AccentSurface" : "Surface");
            main.Foreground = Terminal.Brush(active ? "Accent" : "Muted");
            if (segment == null) continue;
            segment.Background = main.Background;
            segment.Foreground = main.Foreground;
            AutomationProperties.SetItemStatus(segment, state);
        }
    }
}
