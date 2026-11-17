namespace SignalMonitorTest3.Mapping;

public sealed record Placed<T>(T Item, double Latitude, double Longitude, double AccuracyMeters);

public sealed record AnchorResult<T>(IReadOnlyList<Placed<T>> Placed, double? StepScale);

public sealed class GapStitcher<T>
{
    const double MetersPerDegree = 111_320;
    const double DriftPerMeterWalked = 0.05;
    const double MinFitDisplacement = 5;
    const double MaxRotation = Math.PI / 4;
    const double MinScale = 0.7;
    const double MaxScale = 1.4;
    const double MinCalibrationWalk = 20;
    const double MinStraightness = 0.7;

    readonly List<(T Item, double East, double North, double Walked)> pending = new();
    Location? anchor;
    double anchorEast;
    double anchorNorth;
    double anchorWalked;
    Location? calibrationAnchor;
    double calibrationEast;
    double calibrationNorth;
    double calibrationWalked;

    public int PendingCount => pending.Count;

    public AnchorResult<T> Anchor(Location fix, double east, double north, double walked)
    {
        IReadOnlyList<Placed<T>> placed = anchor != null && pending.Count > 0
            ? Stitch(anchor, fix, east, north, walked)
            : Array.Empty<Placed<T>>();
        double? scale = Calibrate(fix, east, north, walked);

        anchor = fix;
        anchorEast = east;
        anchorNorth = north;
        anchorWalked = walked;
        pending.Clear();
        return new AnchorResult<T>(placed, scale);
    }

    public void Add(T item, double east, double north, double walked)
    {
        if (anchor != null)
            pending.Add((item, east, north, walked));
    }

    public Location? Estimate(double east, double north, double walked)
    {
        if (anchor == null)
            return null;

        var (lat, lon) = Offset(anchor, east - anchorEast, north - anchorNorth);
        return new Location(lat, lon)
        {
            Accuracy = Drifted(anchor, walked),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public IReadOnlyList<Placed<T>> Flush()
    {
        if (anchor == null || pending.Count == 0)
        {
            pending.Clear();
            return Array.Empty<Placed<T>>();
        }

        var start = anchor;
        var placed = pending.Select(p =>
        {
            var (lat, lon) = Offset(start, p.East - anchorEast, p.North - anchorNorth);
            return new Placed<T>(p.Item, lat, lon, Drifted(start, p.Walked));
        }).ToList();

        pending.Clear();
        return placed;
    }

    public void Reset()
    {
        anchor = null;
        calibrationAnchor = null;
        pending.Clear();
    }

    double? Calibrate(Location fix, double east, double north, double walked)
    {
        if (calibrationAnchor == null)
        {
            SetCalibration(fix, east, north, walked);
            return null;
        }

        double walkedSince = walked - calibrationWalked;
        double needed = Math.Max(MinCalibrationWalk, 2 * (AccuracyOf(calibrationAnchor) + AccuracyOf(fix)));
        if (walkedSince < needed)
            return null;

        var (targetEast, targetNorth) = Between(calibrationAnchor, fix);
        double reckoned = Length(east - calibrationEast, north - calibrationNorth);
        double? scale = reckoned > 0 && reckoned >= MinStraightness * walkedSince
            ? Math.Clamp(Length(targetEast, targetNorth) / reckoned, MinScale, MaxScale)
            : null;

        SetCalibration(fix, east, north, walked);
        return scale;
    }

    void SetCalibration(Location fix, double east, double north, double walked)
    {
        calibrationAnchor = fix;
        calibrationEast = east;
        calibrationNorth = north;
        calibrationWalked = walked;
    }

    List<Placed<T>> Stitch(Location start, Location end, double endEast, double endNorth, double endWalked)
    {
        var (targetEast, targetNorth) = Between(start, end);
        double reckonedEast = endEast - anchorEast;
        double reckonedNorth = endNorth - anchorNorth;
        double reckoned = Length(reckonedEast, reckonedNorth);
        double target = Length(targetEast, targetNorth);

        double rotation = 0;
        double scale = 1;
        if (reckoned >= MinFitDisplacement && target >= MinFitDisplacement)
        {
            rotation = Math.Clamp(
                Wrap(Math.Atan2(targetNorth, targetEast) - Math.Atan2(reckonedNorth, reckonedEast)),
                -MaxRotation,
                MaxRotation);
            scale = Math.Clamp(target / reckoned, MinScale, MaxScale);
        }

        var (fitEast, fitNorth) = Transform(reckonedEast, reckonedNorth, rotation, scale);
        double residualEast = targetEast - fitEast;
        double residualNorth = targetNorth - fitNorth;
        double rawError = Length(targetEast - reckonedEast, targetNorth - reckonedNorth);
        double accuracy = Math.Max(AccuracyOf(start), AccuracyOf(end))
            + Length(residualEast, residualNorth) / 2
            + rawError / 4;
        double span = endWalked - anchorWalked;

        var placed = new List<Placed<T>>(pending.Count);
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            double f = span > 0
                ? Math.Clamp((p.Walked - anchorWalked) / span, 0, 1)
                : (i + 1.0) / (pending.Count + 1);

            var (east, north) = Transform(p.East - anchorEast, p.North - anchorNorth, rotation, scale);
            var (lat, lon) = Offset(start, east + f * residualEast, north + f * residualNorth);
            placed.Add(new Placed<T>(p.Item, lat, lon, accuracy));
        }
        return placed;
    }

    double Drifted(Location start, double walked) =>
        AccuracyOf(start) + DriftPerMeterWalked * Math.Max(0, walked - anchorWalked);

    static (double East, double North) Transform(double east, double north, double rotation, double scale)
    {
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);
        return (scale * (east * cos - north * sin), scale * (east * sin + north * cos));
    }

    static (double East, double North) Between(Location from, Location to) =>
        ((to.Longitude - from.Longitude) * MetersPerDegree * Math.Cos(from.Latitude * Math.PI / 180),
         (to.Latitude - from.Latitude) * MetersPerDegree);

    static double Wrap(double angle) => Math.IEEERemainder(angle, 2 * Math.PI);

    static double Length(double east, double north) => Math.Sqrt(east * east + north * north);

    static double AccuracyOf(Location location) => location.Accuracy ?? 10;

    static (double Latitude, double Longitude) Offset(Location origin, double east, double north) =>
        (origin.Latitude + north / MetersPerDegree,
         origin.Longitude + east / (MetersPerDegree * Math.Cos(origin.Latitude * Math.PI / 180)));
}
