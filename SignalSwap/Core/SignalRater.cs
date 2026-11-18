namespace SignalSwap.Core;

public enum QualityLevel
{
    None = 0,
    Poor = 1,
    Weak = 2,
    Fair = 3,
    Good = 4,
    Excellent = 5
}

public sealed record QualityReport(
    QualityLevel Level,
    int Score,
    int? StrengthScore,
    int? LinkScore,
    int? ConnectionScore,
    double? Rssi,
    int? LinkMbps,
    double? LatencyMs,
    int ProbeCount,
    int ProbeFailures,
    bool NoInternet,
    bool Settling)
{
    public static QualityReport None { get; } = new(QualityLevel.None, 0, null, null, null, null, null, null, 0, 0, false, false);
}

public sealed class SignalRater
{
    public const int ExcellentAt = 80;
    public const int GoodAt = 60;
    public const int FairAt = 40;
    public const int WeakAt = 20;

    const int Hysteresis = 3;
    const int BottleneckAllowance = 30;
    const int NoInternetCap = 10;
    const int ProbesKept = 3;
    const double StrengthWeight = 0.40;
    const double LinkWeight = 0.25;
    const double ConnectionWeight = 0.35;
    const double CrowdedBandPenaltyDb = 3;
    const double SlowestUsefulLinkMbps = 6;
    const double FullLinkRatio = 25;

    static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(60);
    static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ValidationGrace = TimeSpan.FromSeconds(8);

    static readonly (double X, double Y)[] StrengthCurve =
    {
        (-90, 0),
        (-82, 25),
        (-75, 50),
        (-67, 75),
        (-55, 100)
    };

    static readonly (double X, double Y)[] LatencyCurve =
    {
        (60, 100),
        (150, 75),
        (400, 40),
        (1000, 10),
        (2500, 0)
    };

    readonly List<Sample> samples = new();
    readonly List<Probe> probes = new();
    DateTimeOffset? connectedSince;
    QualityLevel previous = QualityLevel.None;

    public void Reset()
    {
        samples.Clear();
        probes.Clear();
        connectedSince = null;
        previous = QualityLevel.None;
    }

    public void AddSample(DateTimeOffset at, int rssi, int? frequencyMhz, int? linkMbps, bool? validated)
    {
        connectedSince ??= at;
        samples.Add(new Sample(at, rssi, frequencyMhz, linkMbps is > 0 ? linkMbps : null, validated));
        samples.RemoveAll(s => at - s.At > SampleWindow);
    }

    public void AddProbe(DateTimeOffset at, double? latencyMs)
    {
        probes.Add(new Probe(at, latencyMs));
        if (probes.Count > ProbesKept)
            probes.RemoveAt(0);
    }

    public QualityReport Evaluate(DateTimeOffset now)
    {
        if (samples.Count == 0)
            return QualityReport.None;

        double rssi = Median(samples.Select(s => (double)s.Rssi));
        int? frequency = samples[^1].FrequencyMhz;
        var links = samples.Where(s => s.LinkMbps != null).Select(s => (double)s.LinkMbps!.Value).ToList();
        int? link = links.Count > 0 ? (int)Math.Round(Median(links)) : null;
        var recent = probes.Where(p => now - p.At <= ProbeWindow).ToList();

        double strength = StrengthScore(rssi, frequency);
        double? linkScore = link is int mbps ? LinkScore(mbps) : null;
        double? connection = ConnectionScore(recent);

        double total = strength * StrengthWeight;
        double weights = StrengthWeight;
        if (linkScore is double l)
        {
            total += l * LinkWeight;
            weights += LinkWeight;
        }
        if (connection is double c)
        {
            total += c * ConnectionWeight;
            weights += ConnectionWeight;
        }

        double worst = Math.Min(strength, Math.Min(linkScore ?? 100, connection ?? 100));
        double score = Math.Min(total / weights, worst + BottleneckAllowance);

        var age = connectedSince is { } since ? now - since : TimeSpan.Zero;
        bool noInternet = age >= ValidationGrace && samples[^1].Validated == false;
        if (noInternet)
            score = Math.Min(score, NoInternetCap);

        int rounded = (int)Math.Round(Math.Clamp(score, 0, 100));
        var successes = recent.Where(p => p.LatencyMs != null).Select(p => p.LatencyMs!.Value).ToList();

        return new QualityReport(
            Settle(rounded),
            rounded,
            Round(strength),
            Round(linkScore),
            Round(connection),
            rssi,
            link,
            successes.Count > 0 ? Median(successes) : null,
            recent.Count,
            recent.Count(p => p.LatencyMs == null),
            noInternet,
            age < SettleTime);
    }

    public static QualityLevel LevelOf(double score) => score switch
    {
        >= ExcellentAt => QualityLevel.Excellent,
        >= GoodAt => QualityLevel.Good,
        >= FairAt => QualityLevel.Fair,
        >= WeakAt => QualityLevel.Weak,
        _ => QualityLevel.Poor
    };

    public static double StrengthScore(double rssi, int? frequencyMhz)
    {
        double adjusted = frequencyMhz is > 0 and < 3000 ? rssi - CrowdedBandPenaltyDb : rssi;
        return Interpolate(StrengthCurve, adjusted);
    }

    public static double LinkScore(int mbps) =>
        Math.Clamp(100 * Math.Log(mbps / SlowestUsefulLinkMbps) / Math.Log(FullLinkRatio), 0, 100);

    public static double LatencyScore(double? latencyMs) =>
        latencyMs is double ms ? Interpolate(LatencyCurve, ms) : 0;

    QualityLevel Settle(int score)
    {
        QualityLevel level;
        if (previous == QualityLevel.None)
        {
            level = LevelOf(score);
        }
        else
        {
            var up = LevelOf(score - Hysteresis);
            var down = LevelOf(score + Hysteresis);
            level = up > previous ? up : down < previous ? down : previous;
        }

        previous = level;
        return level;
    }

    static double? ConnectionScore(List<Probe> recent)
    {
        if (recent.Count == 0)
            return null;
        if (recent.Count >= 2 && recent[^1].LatencyMs == null && recent[^2].LatencyMs == null)
            return 0;
        return recent.Average(p => LatencyScore(p.LatencyMs));
    }

    static double Interpolate((double X, double Y)[] curve, double x)
    {
        if (x <= curve[0].X)
            return curve[0].Y;

        for (int i = 1; i < curve.Length; i++)
        {
            if (x > curve[i].X)
                continue;
            var (x0, y0) = curve[i - 1];
            var (x1, y1) = curve[i];
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }

        return curve[^1].Y;
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    static int? Round(double? value) => value is double v ? (int)Math.Round(v) : null;

    readonly record struct Sample(DateTimeOffset At, int Rssi, int? FrequencyMhz, int? LinkMbps, bool? Validated);

    readonly record struct Probe(DateTimeOffset At, double? LatencyMs);
}
