using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class DensityPanel : DockPanel
{
    private const double Gap = 6;
    private const double TargetWidth = 315;
    private const double Overscan = 80;

    private sealed class Card(DensityCardKey key, DensityLevel[] levels)
    {
        public DensityCardKey Key { get; } = key;
        public DensityLevel[] Levels { get; set; } = levels;
        public int Column { get; set; }
        public double Top { get; set; }
        public double Height => DensityCard.HeaderHeight + DensityCard.RowHeight * Levels.Length + 2;
        public DensityCard? View { get; set; }
    }

    private readonly record struct Anchor(Card Card, double Offset);

    private readonly DensityOptions options;
    private readonly Action<Market> editCoin;
    private readonly Action<Market> blockCoin;
    private readonly Action<Market> openInTerminal;
    private readonly Canvas board = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        CanContentScroll = false
    };
    private readonly TextBlock status = Terminal.Copy("");
    private readonly VenueSourceStrip sources;
    private readonly List<Card> ordered = [];
    private readonly Dictionary<DensityCardKey, Card> cards = [];
    private readonly HashSet<Card> realized = [];
    private DensityLevel[] displayed = [];
    private double columnWidth;
    private double? pendingOffset;
    private bool terminalEnabled;

    public DensityPanel(DensityOptions options, Action openSettings, Action<Market> editCoin,
        Action<Market> blockCoin, Action<Market> openInTerminal, Action selectionChanged)
    {
        this.options = options;
        this.editCoin = editCoin;
        this.blockCoin = blockCoin;
        this.openInTerminal = openInTerminal;
        Background = Terminal.Brush("Shell");

        var top = Terminal.Rule(new Thickness(0, 0, 0, 1));
        top.Background = Terminal.Brush("Surface");
        top.Padding = new Thickness(6, 4, 5, 2);
        var tools = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var settings = Terminal.IconButton("settings", "Настройки плотностей");
        settings.Width = 31;
        settings.Height = 26;
        settings.Click += (_, _) => openSettings();
        actions.Children.Add(settings);
        SetDock(actions, Dock.Right);
        tools.Children.Add(actions);

        sources = new("Источник плотностей", options.Scans, options.ToggleVenue, options.ToggleSource, selectionChanged);
        AutomationProperties.SetName(sources, "Источники стакана");
        tools.Children.Add(sources);
        top.Child = tools;
        SetDock(top, Dock.Top);
        Children.Add(top);

        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(8, 5, 8, 4);
        status.Visibility = Visibility.Collapsed;
        SetDock(status, Dock.Bottom);
        Children.Add(status);

        AutomationProperties.SetName(scroll, "Карточки плотностей");
        scroll.Content = board;
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0) LayoutCards(CaptureAnchor());
            else
            {
                if (e.VerticalChange != 0) pendingOffset = null;
                RealizeCards(pendingOffset ?? scroll.VerticalOffset);
            }
        };
        Children.Add(scroll);
    }

    internal void SetColibriEnabled(bool enabled)
    {
        terminalEnabled = enabled;
        foreach (var card in realized) card.View?.SetColibriEnabled(enabled);
    }

    public void Update(DensityLevel[] levels, DensityVenueStatus[] venues, (MarketSource Source, string? Error)[] pending, bool debug)
    {
        if (!displayed.AsSpan().SequenceEqual(levels))
        {
            displayed = levels;
            UpdateCards(levels);
        }
        sources.Refresh();

        int failed = venues.Sum(venue => venue.Failed);
        int limited = venues.Sum(venue => venue.Limited);
        status.Text = venues.Length == 0 ? "Выберите биржу и рынок сверху." : debug
            ? $"Плотностей: {levels.Length:N0} · Стаканы: {venues.Sum(v => v.Sampled):N0}/{venues.Sum(v => v.Markets):N0}" +
              (pending.Length > 0 ? $" · Каталоги: {pending.Length}" : "") +
              (limited > 0 ? $" · Глубина ограничена: {limited:N0}" : "") +
              (failed > 0 ? $" · Ошибки: {failed:N0}" : "")
            : pending.Length > 0 ? pending.Any(item => item.Error != null)
                ? "Часть каталогов недоступна · список предварительный" : "Загрузка каталогов…"
            : levels.Length > 0 ? ""
            : failed > 0 ? "Стаканы недоступны" : venues.Any(venue => venue.Markets > 0 && venue.Sampled < venue.Markets)
                ? "Сканирование стаканов…" : "Нет плотностей по этим условиям";
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        status.ToolTip = debug && venues.Length > 0 ? string.Join("\n", venues.Select(v =>
            $"{v.Venue.Label()} · {v.Sampled}/{v.Markets} · цикл ≈ {v.CycleSeconds} с" +
            (v.Error != null ? $"\n{v.Error}" : ""))
            .Concat(pending.Select(item => $"{item.Source.Label} · {item.Error ?? "загрузка каталога"}"))) : null;
    }

    private void UpdateCards(DensityLevel[] levels)
    {
        var snapshots = levels.GroupBy(level => DensityCardKey.For(level.Market)).ToDictionary(group => group.Key, group =>
            group.OrderBy(level => level.Key.Side).ThenBy(level => level.Key.Side == BookSide.Bid ? -level.Key.Price : level.Key.Price)
                .ThenBy(level => level.Market.Venue).ThenBy(level => level.Market.Key, StringComparer.Ordinal).ToArray());
        bool changed = snapshots.Count != cards.Count ||
            ordered.Any(card => !snapshots.TryGetValue(card.Key, out var values) || values.Length != card.Levels.Length);
        var anchor = changed ? CaptureAnchor(snapshots) : null;
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            var card = ordered[i];
            if (!snapshots.TryGetValue(card.Key, out var next))
            {
                Hide(card);
                ordered.RemoveAt(i);
                cards.Remove(card.Key);
                continue;
            }
            if (!card.Levels.AsSpan().SequenceEqual(next))
            {
                card.Levels = next;
                card.View?.Update(next);
            }
        }
        foreach (var (key, items) in snapshots.Where(pair => !cards.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Value.Min(level => level.FirstSeen))
            .ThenBy(pair => pair.Key.Title, StringComparer.Ordinal).ThenBy(pair => pair.Key.Segment)
            .ThenBy(pair => pair.Key.Asset, StringComparer.Ordinal).ThenBy(pair => pair.Key.Delivery, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Unit))
        {
            var card = new Card(key, items);
            ordered.Add(card);
            cards.Add(key, card);
        }
        if (changed) LayoutCards(anchor);
    }

    private Anchor? CaptureAnchor(Dictionary<DensityCardKey, DensityLevel[]>? remaining = null)
    {
        double offset = pendingOffset ?? scroll.VerticalOffset;
        if (offset < .5) return null;
        Card? best = null;
        foreach (var card in ordered)
        {
            if (card.Top + card.Height <= offset || (remaining != null && !remaining.ContainsKey(card.Key))) continue;
            if (best == null || Math.Max(offset, card.Top) < Math.Max(offset, best.Top)
                || Math.Max(offset, card.Top) == Math.Max(offset, best.Top) && card.Column < best.Column)
                best = card;
        }
        return best == null ? null : new(best, offset - best.Top);
    }

    private void LayoutCards(Anchor? anchor)
    {
        double width = scroll.ViewportWidth;
        if (width < Gap * 2 + 1) return;
        int columns = Math.Max(1, (int)((width - Gap) / (TargetWidth + Gap)));
        columnWidth = (width - Gap * (columns + 1)) / columns;
        double top = Gap, rowHeight = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var card = ordered[i];
            card.Column = i % columns;
            card.Top = top;
            rowHeight = Math.Max(rowHeight, card.Height);
            if (card.Column == columns - 1 || i == ordered.Count - 1)
            {
                top += rowHeight + Gap;
                rowHeight = 0;
            }
        }
        board.Height = ordered.Count == 0 ? 0 : top;
        double position = pendingOffset ?? scroll.VerticalOffset;
        if (anchor is { } target)
            position = Math.Max(0, target.Card.Top + Math.Min(target.Offset, target.Card.Height - 1));
        position = Math.Clamp(position, 0, Math.Max(0, board.Height - scroll.ViewportHeight));
        pendingOffset = null;
        if (Math.Abs(position - scroll.VerticalOffset) > .5)
        {
            pendingOffset = position;
            scroll.ScrollToVerticalOffset(position);
        }
        RealizeCards(position);
    }

    private void RealizeCards(double offset)
    {
        if (columnWidth <= 0 || scroll.ViewportHeight <= 0) return;
        double start = Math.Max(0, offset - Overscan);
        double end = offset + scroll.ViewportHeight + Overscan;
        bool Visible(Card item) => item.Top + item.Height >= start && item.Top <= end;
        foreach (var card in realized.Where(item => !Visible(item) && item.View?.IsKeyboardFocusWithin != true).ToArray()) Hide(card);
        foreach (var card in ordered)
        {
            if (!Visible(card) && card.View?.IsKeyboardFocusWithin != true) continue;
            if (card.View == null)
            {
                card.View = new DensityCard(card.Key, card.Levels, editCoin, blockCoin, openInTerminal);
                card.View.SetColibriEnabled(terminalEnabled);
                board.Children.Add(card.View);
                realized.Add(card);
            }
            card.View.Width = columnWidth;
            Canvas.SetTop(card.View, card.Top);
            Canvas.SetLeft(card.View, Gap + card.Column * (columnWidth + Gap));
            card.View.SetViewport(start - card.Top, end - card.Top);
        }
    }

    private void Hide(Card card)
    {
        if (card.View == null) return;
        board.Children.Remove(card.View);
        card.View = null;
        realized.Remove(card);
    }
}

internal static class DensityTint
{
    public static SolidColorBrush Read(string hex) => FromColor((Color)ColorConverter.ConvertFromString(hex));

    internal static SolidColorBrush FromColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
