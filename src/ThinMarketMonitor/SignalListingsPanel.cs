using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class SignalListingsPanel : Border
{
    private readonly Settings settings;
    private readonly IReadOnlyDictionary<MarketSource, Catalog> catalogs;
    private readonly Action changed;
    private readonly Dictionary<MarketSource, (Button Button, TextBlock State)> venues = new();

    public SignalListingsPanel(Settings settings, IReadOnlyDictionary<MarketSource, Catalog> catalogs,
        Action changed, Action close)
    {
        this.settings = settings;
        this.catalogs = catalogs;
        this.changed = changed;
        Width = 510;
        Background = Terminal.Brush("Shell");
        BorderBrush = Terminal.Brush("Outline");
        BorderThickness = new Thickness(1);
        var layout = new DockPanel();
        var heading = Terminal.Rule(new Thickness(0, 0, 0, 1));
        heading.Background = Terminal.Brush("Control");
        heading.Child = Terminal.DialogHeader("Листинги сигналов", "Закрыть листинги сигналов", close);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        layout.Children.Add(new ScrollViewer
        {
            Content = ListingRules(), Padding = new Thickness(16, 0, 16, 12), MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Child = layout;
        Loaded += (_, _) => venues.Values.First().Button.Focus();
    }

    public void RefreshCatalogStatus()
    {
        foreach (var source in venues.Keys) UpdateVenue(source);
    }

    private StackPanel ListingRules()
    {
        var panel = new StackPanel();
        var logos = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, -7, 5) };
        AutomationProperties.SetName(logos, "Листинги сигналов");
        foreach (var source in MarketSource.All)
        {
            var venue = source.Venue;
            var button = new Button { Style = Terminal.Style("VenueButton"), Margin = new Thickness(0, 0, 7, 7) };
            var content = new Grid();
            var logo = new Grid { Width = 48, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var image = Terminal.VenueLogo(venue, venue switch { Venue.Okx => 49, Venue.Mexc => 33, Venue.KuCoin => 29, _ => 42 });
            logo.Children.Add(image);
            if (venue == Venue.Alpha)
            {
                logo.Children.Add(new TextBlock
                {
                    Text = "α", FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(243, 186, 47)),
                    Background = Terminal.Brush("Plot"), Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(0, 0, -1, -1), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            content.Children.Add(logo);
            logo.Margin = new Thickness(0, 0, 0, 14);
            content.Children.Add(new TextBlock
            {
                Text = venue == Venue.Alpha ? "Alpha" : source.Segment.ToString(), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3)
            });
            var state = new TextBlock
            {
                FontSize = 14, FontFamily = new FontFamily("Segoe UI Symbol"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 5, 0)
            };
            content.Children.Add(state);
            button.Content = content;
            logos.Children.Add(button);
            venues.Add(source, (button, state));
            button.Click += (_, _) =>
            {
                settings.CycleListingRule(source, overview: false);
                UpdateVenue(source);
                changed();
            };
            UpdateVenue(source);
        }
        panel.Children.Add(logos);
        var legend = new WrapPanel();
        AddLegend("✓", " Требовать листинг", "Required");
        AddLegend("×", " Исключать при листинге", "Excluded");
        AddLegend("", "Без отметки · игнорировать", "Muted");
        panel.Children.Add(legend);
        return panel;

        void AddLegend(string mark, string text, string color)
        {
            var entry = new TextBlock { FontSize = 11, Foreground = Terminal.Brush("Muted"), Margin = new Thickness(0, 0, 12, 4) };
            entry.Inlines.Add(new System.Windows.Documents.Run(mark)
            {
                Foreground = Terminal.Brush(color), FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Symbol")
            });
            entry.Inlines.Add(text);
            legend.Children.Add(entry);
        }
    }

    private void UpdateVenue(MarketSource venue)
    {
        var (button, state) = venues[venue];
        var rule = settings.Rule(venue);
        var (mark, caption, border, background) = rule switch
        {
            ListingRule.Require => ("✓", "требуется листинг", "Required", "RequiredSurface"),
            ListingRule.Exclude => ("×", "требуется отсутствие листинга", "Excluded", "ExcludedSurface"),
            _ => ("", "игнорировать", "Outline", "Plot")
        };
        ToolTipService.SetShowOnDisabled(button, true);
        state.Text = mark;
        state.Foreground = button.BorderBrush = Terminal.Brush(border);
        button.Background = Terminal.Brush(background);
        string label = $"{venue.Label} · {caption}";
        AutomationProperties.SetName(button, label);
        button.ToolTip = catalogs.TryGetValue(venue, out var catalog) && catalog.Error != null
            ? $"{label}\nКаталог недоступен · {catalog.Error}" : label;
    }
}
