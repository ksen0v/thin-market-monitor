namespace ThinMarketMonitor;

internal static class DensityLadders
{
    internal const int EvidenceLimit = 32;
    private const int MinimumLevels = 4;
    private const double StepTolerance = .15;
    private const double WithinSideSizeRatio = 3;
    private const double BetweenSidesSizeRatio = 2;

    internal readonly record struct Level(DensityKey Key, double Usd);
    private sealed record Ladder(double Step, double AverageUsd, Level[] Levels);

    internal static double EvidenceDistance(double maxDistance) => Math.Max(5, maxDistance);

    internal static HashSet<DensityKey> Find(List<Level> bids, List<Level> asks)
    {
        if (bids.Count < MinimumLevels || asks.Count < MinimumLevels) return [];
        var buying = FindLadders(bids);
        var selling = FindLadders(asks);
        var hidden = new HashSet<DensityKey>();
        foreach (var bid in buying)
            foreach (var ask in selling)
            {
                double stepDifference = Math.Abs(bid.Step - ask.Step);
                if (stepDifference > Math.Max(bid.Step, ask.Step) * StepTolerance ||
                    Math.Max(bid.AverageUsd, ask.AverageUsd) > Math.Min(bid.AverageUsd, ask.AverageUsd) * BetweenSidesSizeRatio) continue;
                foreach (var level in bid.Levels) hidden.Add(level.Key);
                foreach (var level in ask.Levels) hidden.Add(level.Key);
            }
        return hidden;
    }

    private static List<Ladder> FindLadders(List<Level> side)
    {
        var ladders = new List<Ladder>();
        var run = new List<Level>(side.Count);
        for (int first = 0; first <= side.Count - MinimumLevels; first++)
        {
            var anchor = side[first];
            // An independent wall can sit between the two nearest rungs of a series.
            for (int second = first + 1; second <= Math.Min(first + 3, side.Count - MinimumLevels + 2); second++)
            {
                var next = side[second];
                double step = Math.Abs(next.Key.Price - anchor.Key.Price);
                double smallest = Math.Min(anchor.Usd, next.Usd);
                double largest = Math.Max(anchor.Usd, next.Usd);
                if (step <= 0 || largest > smallest * WithinSideSizeRatio) continue;
                run.Clear();
                run.Add(anchor);
                run.Add(next);
                int singleSteps = 1;
                for (int index = second + 1; index < side.Count; index++)
                {
                    var candidate = side[index];
                    double gaps = Math.Abs(candidate.Key.Price - run[^1].Key.Price) / step;
                    if (gaps > 2 + StepTolerance) break;
                    double rounded = Math.Round(gaps);
                    if (rounded < 1 || Math.Abs(gaps - rounded) > StepTolerance) continue;
                    double low = Math.Min(candidate.Usd, smallest);
                    double high = Math.Max(candidate.Usd, largest);
                    if (high > low * WithinSideSizeRatio) continue;
                    smallest = low;
                    largest = high;
                    run.Add(candidate);
                    if (rounded == 1) singleSteps++;
                }
                if (run.Count >= MinimumLevels && singleSteps >= 2)
                    ladders.Add(new(step, run.Average(level => level.Usd), run.ToArray()));
            }
        }
        return ladders;
    }
}
