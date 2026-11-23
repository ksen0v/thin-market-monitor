namespace ThinMarketMonitor;

internal sealed record ChartPaneWeights(double Price, Dictionary<Venue, double> Interest)
{
    internal bool Valid => Price is >= 0 and <= 1 && Interest is { Count: > 0 } &&
        Interest.All(entry => Enum.IsDefined(entry.Key) && entry.Key != Venue.Alpha && entry.Value is >= 0 and <= 1);
}

internal static class ChartPaneLayout
{
    internal const double DividerHeight = 8;
    internal const double HeaderHeight = 21;
    internal const double PriceMinimum = 44;
    internal const double InterestMinimum = 48;
    private const double ResizeSpace = 36;

    internal static double MinimumHeight(int indicators) => PriceMinimum +
        indicators * (InterestMinimum + DividerHeight) + ResizeSpace;

    internal static double[] Measure(double height, ReadOnlySpan<InterestLayer> panes, ChartPaneWeights? saved)
    {
        int count = panes.Length;
        double available = Math.Max(0, height - count * DividerHeight);
        var sizes = new double[count + 1];
        if (count == 0)
        {
            sizes[0] = available;
            return sizes;
        }
        double required = PriceMinimum + count * InterestMinimum;
        double flexible = Math.Max(0, available - required);
        double defaultShare = Math.Clamp(Math.Min(count * 170, height * (count == 1 ? .30 : .52)) -
            count * InterestMinimum, 0, flexible);
        double defaultWeight = flexible > 0 ? defaultShare / (count * flexible) : 0;
        double priceWeight = saved?.Price ?? 1 - defaultWeight * count;
        double totalWeight = priceWeight;
        for (int i = 0; i < count; i++)
        {
            sizes[i + 1] = saved?.Interest.GetValueOrDefault(panes[i].Venue, defaultWeight) ?? defaultWeight;
            totalWeight += sizes[i + 1];
        }
        double minimumFactor = Math.Min(1, available / required);
        double scale = totalWeight > 0 ? flexible / totalWeight : 0;
        sizes[0] = PriceMinimum * minimumFactor + (totalWeight > 0 ? priceWeight * scale : flexible - defaultShare);
        for (int i = 1; i < sizes.Length; i++)
            sizes[i] = InterestMinimum * minimumFactor + (totalWeight > 0 ? sizes[i] * scale : defaultShare / count);
        return sizes;
    }

    internal static ChartPaneWeights? Resize(double[] starting, ReadOnlySpan<Venue> panes, int boundary, double delta,
        ChartPaneWeights? saved)
    {
        double remaining = Math.Abs(delta);
        if (remaining < .5 || !double.IsFinite(remaining) || boundary < 0 || boundary >= panes.Length) return null;
        var sizes = (double[])starting.Clone();
        int larger = delta > 0 ? boundary : boundary + 1;
        int direction = delta > 0 ? 1 : -1;
        // Pass the resize to the next pane on the shrinking side once its neighbour reaches the minimum.
        for (int i = larger + direction; i >= 0 && i < sizes.Length && remaining > 0; i += direction)
        {
            double capacity = sizes[i] - (i == 0 ? PriceMinimum : InterestMinimum);
            double taken = Math.Min(remaining, Math.Max(0, capacity));
            sizes[i] -= taken;
            sizes[larger] += taken;
            remaining -= taken;
        }
        if (Math.Abs(delta) - remaining < .5) return null;
        double flexible = sizes.Sum() - PriceMinimum - InterestMinimum * panes.Length;
        if (flexible <= 0) return null;
        var weights = new Dictionary<Venue, double>(saved?.Interest ?? []);
        for (int i = 0; i < panes.Length; i++)
            weights[panes[i]] = Math.Clamp((sizes[i + 1] - InterestMinimum) / flexible, 0, 1);
        return new(Math.Clamp((sizes[0] - PriceMinimum) / flexible, 0, 1), weights);
    }
}
