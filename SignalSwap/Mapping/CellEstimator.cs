using SignalSwap.Core;

namespace SignalSwap.Mapping;

public readonly record struct CellEstimate(CellKey Key, double Rssi, int Sources, QualityLevel Level);

public static class CellEstimator
{
    const int MinTouchingCells = 2;
    const int Reach = 2;
    const double DeadZoneRssi = -95;

    public static IEnumerable<CellKey> Around(CellKey key)
    {
        for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    yield return new CellKey(key.X + dx, key.Y + dy);
    }

    public static IEnumerable<CellKey> CandidatesNear(IReadOnlyDictionary<CellKey, CellStats> cells, IEnumerable<CellKey> changed)
    {
        var seen = new HashSet<CellKey>();
        foreach (var key in changed)
            for (long dx = -Reach; dx <= Reach; dx++)
                for (long dy = -Reach; dy <= Reach; dy++)
                {
                    var near = new CellKey(key.X + dx, key.Y + dy);
                    if (seen.Add(near))
                        yield return near;
                }
    }

    public static CellEstimate? Estimate(IReadOnlyDictionary<CellKey, CellStats> cells, CellKey key)
    {
        if (cells.ContainsKey(key))
            return null;

        int touching = 0;
        foreach (var near in Around(key))
            if (cells.ContainsKey(near))
                touching++;
        if (touching < MinTouchingCells)
            return null;

        double weighted = 0;
        double weights = 0;
        int sources = 0;
        for (long dx = -Reach; dx <= Reach; dx++)
        {
            for (long dy = -Reach; dy <= Reach; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                if (!cells.TryGetValue(new CellKey(key.X + dx, key.Y + dy), out var stats))
                    continue;

                double rssi = stats.Average is double average && stats.DropRate < 0.5 ? average : DeadZoneRssi;
                double weight = 1.0 / (dx * dx + dy * dy);
                weighted += weight * rssi;
                weights += weight;
                sources++;
            }
        }

        double value = weighted / weights;
        return new CellEstimate(key, value, sources, SignalScale.LevelOf(value, null));
    }
}
