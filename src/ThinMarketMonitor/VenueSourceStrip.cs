using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed class VenueSourceStrip : WrapPanel
{
    private readonly Func<MarketSource, ListingRule> rule;
    private readonly Dictionary<MarketSource, Button> buttons = [];

    public VenueSourceStrip(string scope, Func<MarketSource, ListingRule> rule, Action<MarketSource> cycle, Action changed)
    {
        this.rule = rule;
        foreach (var group in MarketSource.All.GroupBy(source => source.Venue))
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 3) };
            foreach (var source in group)
            {
                var caption = new StackPanel { Orientation = Orientation.Horizontal };
                caption.Children.Add(Terminal.VenueLogo(source.Venue, 15));
                caption.Children.Add(new TextBlock
                {
                    Text = source.Segment == Segment.Spot ? "S" : "F",
                    Margin = new Thickness(5, 0, 0, 0),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                caption.Children.Add(new TextBlock
                {
                    Text = source.Venue.Label(),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var button = new Button
                {
                    Tag = source, Content = caption, MinHeight = 25, FontSize = 11,
                    Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 2, 0)
                };
                AutomationProperties.SetName(button, $"{scope} · {source.Venue.Label()} · {source.Segment}");
                button.Click += (_, _) =>
                {
                    cycle(source);
                    Refresh();
                    changed();
                };
                buttons.Add(source, button);
                pair.Children.Add(button);
            }
            Children.Add(pair);
        }
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var (source, button) in buttons)
        {
            var state = rule(source);
            var caption = StateLabel(state);
            if (AutomationProperties.GetItemStatus(button) == caption) continue;
            string foreground = state switch { ListingRule.Require => "Required", ListingRule.Exclude => "Excluded", _ => "Muted" };
            string background = state switch
            {
                ListingRule.Require => "RequiredSurface", ListingRule.Exclude => "ExcludedSurface", _ => "Surface"
            };
            button.ToolTip = $"{source.Venue.Label()} · {source.Segment}\n{caption}\nСледующее нажатие · {StateLabel(ListingFilter.Next(state))}";
            AutomationProperties.SetItemStatus(button, caption);
            button.BorderBrush = Terminal.Brush(state == ListingRule.Ignore ? "Outline" : foreground);
            button.Background = Terminal.Brush(background);
            button.Foreground = Terminal.Brush(foreground);
        }
    }

    private static string StateLabel(ListingRule rule) => rule switch
    {
        ListingRule.Require => "Брать инструменты",
        ListingRule.Exclude => "Исключать монеты с листингом",
        _ => "Игнорировать"
    };
}
