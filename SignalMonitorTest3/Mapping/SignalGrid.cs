namespace SignalMonitorTest3.Mapping;

public readonly record struct CellKey(long X, long Y);

public sealed class CellStats
{
    long rssiSum;

    public CellStats(CellKey key) => Key = key;

    public CellKey Key { get; }
    public int Samples { get; private set; }
    public int Drops { get; private set; }
    public int RssiSamples { get; private set; }
    public int Min { get; private set; } = int.MaxValue;
    public int Max { get; private set; } = int.MinValue;
    public HashSet<string> AccessPoints { get; } = new();

    public double? Average => RssiSamples == 0 ? (double?)null : (double)rssiSum / RssiSamples;

    public double DropRate => Samples == 0 ? 0 : (double)Drops / Samples;

    public Color Color => Average is double avg && DropRate < 0.5
        ? SignalScale.Rate((int)Math.Round(avg)).Color
        : SignalScale.DroppedColor;

    public void Add(SignalReading reading)
    {
        Samples++;
        AccessPoints.Add(reading.Bssid);

        if (reading.SignalStrength is not int rssi)
        {
            Drops++;
            return;
        }

        RssiSamples++;
        rssiSum += rssi;
        Min = Math.Min(Min, rssi);
        Max = Math.Max(Max, rssi);
    }
}

public sealed class SignalGrid
{
    const double MetersPerDegreeLatitude = 111_320;

    readonly double metersPerDegreeLongitude;
    readonly Dictionary<CellKey, CellStats> cells = new();

    public SignalGrid(double cellSizeMeters, double referenceLatitude)
    {
        CellSize = cellSizeMeters;
        metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(Math.Round(referenceLatitude) * Math.PI / 180);
    }

    public double CellSize { get; }

    public IReadOnlyDictionary<CellKey, CellStats> Cells => cells;

    public CellKey KeyOf(double latitude, double longitude) => new(
        (long)Math.Floor(longitude * metersPerDegreeLongitude / CellSize),
        (long)Math.Floor(latitude * MetersPerDegreeLatitude / CellSize));

    public CellStats Add(SignalReading reading)
    {
        var key = KeyOf(reading.Latitude, reading.Longitude);
        if (!cells.TryGetValue(key, out var stats))
        {
            stats = new CellStats(key);
            cells[key] = stats;
        }
        stats.Add(reading);
        return stats;
    }

    public CellStats? Find(double latitude, double longitude) =>
        cells.TryGetValue(KeyOf(latitude, longitude), out var stats) ? stats : null;

    public Location[] Corners(CellKey key)
    {
        double south = key.Y * CellSize / MetersPerDegreeLatitude;
        double north = (key.Y + 1) * CellSize / MetersPerDegreeLatitude;
        double west = key.X * CellSize / metersPerDegreeLongitude;
        double east = (key.X + 1) * CellSize / metersPerDegreeLongitude;
        return new[]
        {
            new Location(south, west),
            new Location(north, west),
            new Location(north, east),
            new Location(south, east)
        };
    }
}
