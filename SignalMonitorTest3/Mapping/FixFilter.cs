namespace SignalMonitorTest3.Mapping;

public sealed class FixFilter
{
    const double WalkingSpeedMetersPerSecond = 2;
    const double MaxGapSeconds = 10;
    const double MinAgreementMeters = 5;

    Location? previous;

    public double MaxAccuracyMeters { get; set; } = 20;
    public Location? Trusted { get; private set; }
    public int Streak { get; private set; }
    public int Needed { get; private set; } = 2;

    public void Offer(Location fix, Location? estimate)
    {
        if (fix.Accuracy is not double accuracy || accuracy > MaxAccuracyMeters)
        {
            previous = null;
            Streak = 0;
            return;
        }

        bool agrees = previous is { } last && Distance(last, fix) <= Allowance(last, fix, accuracy);
        Streak = agrees ? Streak + 1 : 1;
        previous = fix;

        Needed = estimate?.Accuracy is double estimateAccuracy && Distance(estimate, fix) > accuracy + estimateAccuracy ? 3 : 2;
        if (Streak >= Needed)
            Trusted = fix;
    }

    public void Reset()
    {
        previous = null;
        Trusted = null;
        Streak = 0;
    }

    static double Allowance(Location last, Location fix, double accuracy)
    {
        double seconds = Math.Min(MaxGapSeconds, Math.Abs((fix.Timestamp - last.Timestamp).TotalSeconds));
        return Math.Max(MinAgreementMeters, (last.Accuracy ?? accuracy) + accuracy) + WalkingSpeedMetersPerSecond * seconds;
    }

    static double Distance(Location a, Location b) =>
        Location.CalculateDistance(a, b, DistanceUnits.Kilometers) * 1000;
}
