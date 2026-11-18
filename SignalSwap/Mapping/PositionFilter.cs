// This file is a hodgepodge of algorithms put together with Claude. I do not understand the math.

namespace SignalSwap.Mapping;

public sealed record Placed<T>(T Item, double Latitude, double Longitude, double AccuracyMeters);

public sealed record AnchorResult<T>(IReadOnlyList<Placed<T>> Placed, double? StepScale, bool Accepted);

public sealed class PositionFilter<T>
{
    const int E = 0;
    const int N = 1;
    const int B = 2;
    const int K = 3;

    const double MetersPerDegree = 111_320;
    const double ConfidenceRadius = 1.51;
    const double FixSigmaPerAccuracy = 1.0;
    const double MinFixSigma = 1.5;
    const double InitialHeadingSigma = 20 * Math.PI / 180;
    const double InitialScaleSigma = 0.10;
    const double StepNoisePerMeter = 0.03;
    const double DriftPerSecond = 0.3;
    const double FirstFixDoubt = 2.5;
    const double HeadingDriftPerMeter = 0.3 * Math.PI / 180;
    const double ScaleDriftPerMeter = 0.003;
    const double Gate = 13.8;
    const int RejectionsBeforeRestart = 3;
    const double CalibrateBelowSigma = 0.04;
    const double CalibrateAbove = 0.03;

    readonly List<Node> nodes = new();
    readonly List<Waiting> pending = new();
    double[] x = new double[4];
    double[,] p = new double[4, 4];
    double[,] since = Identity();
    Location? origin;
    double lastEast;
    double lastNorth;
    double lastWalked;
    DateTimeOffset lastTime;
    int rejections;

    public int PendingCount => pending.Count;

    public bool IsTracking => origin != null;

    public double HeadingCorrectionDegrees => x[B] * 180 / Math.PI;

    public double ScaleCorrection => x[K];

    public AnchorResult<T> Anchor(Location fix, double east, double north, double walked, bool force = false, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        double sigma = Math.Max(MinFixSigma, (fix.Accuracy ?? 10) * FixSigmaPerAccuracy);
        if (origin == null)
        {
            Start(fix, sigma * FirstFixDoubt, east, north, walked, now, 0, InitialHeadingSigma, 1, InitialScaleSigma);
            return new AnchorResult<T>(Array.Empty<Placed<T>>(), null, true);
        }

        Predict(east, north, walked, now);
        var (ze, zn) = ToLocal(fix);
        double ye = ze - x[E];
        double yn = zn - x[N];
        double r = sigma * sigma;
        double s00 = p[E, E] + r;
        double s01 = p[E, N];
        double s11 = p[N, N] + r;
        double det = s00 * s11 - s01 * s01;
        double i00 = s11 / det;
        double i01 = -s01 / det;
        double i11 = s00 / det;
        double distance = ye * (i00 * ye + i01 * yn) + yn * (i01 * ye + i11 * yn);

        if (force)
        {
            var placedBefore = PlaceFiltered();
            Start(fix, sigma, east, north, walked, now, x[B], Math.Sqrt(p[B, B]), x[K], Math.Sqrt(p[K, K]));
            return new AnchorResult<T>(placedBefore, null, true);
        }

        if (distance > Gate)
        {
            rejections++;
            if (rejections < RejectionsBeforeRestart)
                return new AnchorResult<T>(Array.Empty<Placed<T>>(), null, false);

            var placedBefore = PlaceFiltered();
            Start(fix, sigma, east, north, walked, now, x[B], InitialHeadingSigma, x[K], Math.Sqrt(p[K, K]));
            return new AnchorResult<T>(placedBefore, null, true);
        }

        rejections = 0;
        var priorX = (double[])x.Clone();
        var priorP = (double[,])p.Clone();

        var gain = new double[4, 2];
        for (int i = 0; i < 4; i++)
        {
            gain[i, 0] = p[i, E] * i00 + p[i, N] * i01;
            gain[i, 1] = p[i, E] * i01 + p[i, N] * i11;
        }

        for (int i = 0; i < 4; i++)
            x[i] += gain[i, 0] * ye + gain[i, 1] * yn;

        var a = Identity();
        for (int i = 0; i < 4; i++)
        {
            a[i, E] -= gain[i, 0];
            a[i, N] -= gain[i, 1];
        }

        var joseph = Multiply(Multiply(a, p), Transpose(a));
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                joseph[i, j] += r * (gain[i, 0] * gain[j, 0] + gain[i, 1] * gain[j, 1]);
        p = Symmetric(joseph);

        nodes.Add(new Node(priorX, priorP, since, (double[])x.Clone(), (double[,])p.Clone()));
        since = Identity();

        var placed = Smooth();
        nodes.Clear();
        nodes.Add(Snapshot());
        pending.Clear();

        double? stepScale = Math.Sqrt(p[K, K]) < CalibrateBelowSigma && Math.Abs(x[K] - 1) > CalibrateAbove ? x[K] : null;
        return new AnchorResult<T>(placed, stepScale, true);
    }

    public void Rescale(double factor)
    {
        if (factor <= 0 || origin == null)
            return;

        x[K] /= factor;
        for (int i = 0; i < 4; i++)
        {
            p[K, i] /= factor;
            p[i, K] /= factor;
        }

        if (nodes.Count == 1 && pending.Count == 0)
        {
            nodes.Clear();
            nodes.Add(Snapshot());
        }
    }

    public void Add(T item, double east, double north, double walked, DateTimeOffset? at = null)
    {
        if (origin == null)
            return;

        double offsetEast = 0;
        double offsetNorth = 0;
        if (walked >= lastWalked)
        {
            Predict(east, north, walked, at ?? DateTimeOffset.UtcNow);
        }
        else
        {
            offsetEast = east - lastEast;
            offsetNorth = north - lastNorth;
        }

        nodes.Add(new Node((double[])x.Clone(), (double[,])p.Clone(), since, (double[])x.Clone(), (double[,])p.Clone()));
        since = Identity();
        pending.Add(new Waiting(item, nodes.Count - 1, offsetEast, offsetNorth));
    }

    public Location? Estimate(double east, double north, double walked, DateTimeOffset? at = null)
    {
        if (origin == null)
            return null;

        Predict(east, north, walked, at ?? DateTimeOffset.UtcNow);
        var (lat, lon) = ToGeo(x[E], x[N]);
        return new Location(lat, lon)
        {
            Accuracy = Radius(p),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public int Discard(Func<T, bool> match) => pending.RemoveAll(item => match(item.Item));

    public IReadOnlyList<Placed<T>> Flush()
    {
        var placed = PlaceFiltered();
        pending.Clear();
        if (origin != null)
        {
            nodes.Clear();
            nodes.Add(Snapshot());
            since = Identity();
        }
        return placed;
    }

    public void Reset()
    {
        origin = null;
        nodes.Clear();
        pending.Clear();
        since = Identity();
        rejections = 0;
    }

    void Start(Location fix, double sigma, double east, double north, double walked, DateTimeOffset now, double heading, double headingSigma, double scale, double scaleSigma)
    {
        origin = fix;
        x = new[] { 0, 0, heading, scale };
        p = new double[4, 4];
        p[E, E] = sigma * sigma;
        p[N, N] = sigma * sigma;
        p[B, B] = headingSigma * headingSigma;
        p[K, K] = scaleSigma * scaleSigma;
        lastEast = east;
        lastNorth = north;
        lastWalked = walked;
        lastTime = now;
        since = Identity();
        rejections = 0;
        nodes.Clear();
        nodes.Add(Snapshot());
        pending.Clear();
    }

    void Predict(double east, double north, double walked, DateTimeOffset now)
    {
        double de = east - lastEast;
        double dn = north - lastNorth;
        double dw = Math.Max(0, walked - lastWalked);
        double dt = Math.Max(0, (now - lastTime).TotalSeconds);
        lastEast = east;
        lastNorth = north;
        lastWalked = walked;
        if (now > lastTime)
            lastTime = now;
        if (de == 0 && dn == 0 && dw == 0 && dt == 0)
            return;

        double cos = Math.Cos(x[B]);
        double sin = Math.Sin(x[B]);
        double re = cos * de - sin * dn;
        double rn = sin * de + cos * dn;

        var f = Identity();
        f[E, B] = x[K] * (-sin * de - cos * dn);
        f[N, B] = x[K] * (cos * de - sin * dn);
        f[E, K] = re;
        f[N, K] = rn;

        x[E] += x[K] * re;
        x[N] += x[K] * rn;

        var next = Multiply(Multiply(f, p), Transpose(f));
        double positionNoise = StepNoisePerMeter * StepNoisePerMeter * dw + DriftPerSecond * DriftPerSecond * dt;
        next[E, E] += positionNoise;
        next[N, N] += positionNoise;
        next[B, B] += HeadingDriftPerMeter * HeadingDriftPerMeter * dw;
        next[K, K] += ScaleDriftPerMeter * ScaleDriftPerMeter * dw;
        p = Symmetric(next);
        since = Multiply(f, since);
    }

    List<Placed<T>> Smooth()
    {
        var smoothedX = new double[nodes.Count][];
        var smoothedP = new double[nodes.Count][,];
        int last = nodes.Count - 1;
        smoothedX[last] = nodes[last].FilteredX;
        smoothedP[last] = nodes[last].FilteredP;

        for (int i = last - 1; i >= 0; i--)
        {
            var next = nodes[i + 1];
            var c = Multiply(Multiply(nodes[i].FilteredP, Transpose(next.Transition)), Invert(next.PredictedP));
            var dx = Subtract(smoothedX[i + 1], next.PredictedX);
            var xs = (double[])nodes[i].FilteredX.Clone();
            for (int r = 0; r < 4; r++)
                for (int k = 0; k < 4; k++)
                    xs[r] += c[r, k] * dx[k];

            var dp = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int k = 0; k < 4; k++)
                    dp[r, k] = smoothedP[i + 1][r, k] - next.PredictedP[r, k];

            var ps = Multiply(Multiply(c, dp), Transpose(c));
            for (int r = 0; r < 4; r++)
                for (int k = 0; k < 4; k++)
                    ps[r, k] += nodes[i].FilteredP[r, k];

            smoothedX[i] = xs;
            smoothedP[i] = Symmetric(ps);
        }

        return pending.Select(item => Place(item, smoothedX[item.Node], smoothedP[item.Node])).ToList();
    }

    List<Placed<T>> PlaceFiltered() =>
        pending.Select(item => Place(item, nodes[item.Node].FilteredX, nodes[item.Node].FilteredP)).ToList();

    Placed<T> Place(Waiting item, double[] state, double[,] covariance)
    {
        double cos = Math.Cos(state[B]);
        double sin = Math.Sin(state[B]);
        double east = state[E] + state[K] * (cos * item.OffsetEast - sin * item.OffsetNorth);
        double north = state[N] + state[K] * (sin * item.OffsetEast + cos * item.OffsetNorth);
        double offset = Math.Sqrt(item.OffsetEast * item.OffsetEast + item.OffsetNorth * item.OffsetNorth);
        double radius = Math.Sqrt(Math.Pow(Radius(covariance), 2) + Math.Pow(ConfidenceRadius * StepNoisePerMeter, 2) * offset);
        var (lat, lon) = ToGeo(east, north);
        return new Placed<T>(item.Item, lat, lon, radius);
    }

    Node Snapshot() => new((double[])x.Clone(), (double[,])p.Clone(), Identity(), (double[])x.Clone(), (double[,])p.Clone());

    (double East, double North) ToLocal(Location location) =>
        ((location.Longitude - origin!.Longitude) * MetersPerDegree * Math.Cos(origin.Latitude * Math.PI / 180),
         (location.Latitude - origin.Latitude) * MetersPerDegree);

    (double Latitude, double Longitude) ToGeo(double east, double north) =>
        (origin!.Latitude + north / MetersPerDegree,
         origin.Longitude + east / (MetersPerDegree * Math.Cos(origin.Latitude * Math.PI / 180)));

    static double Radius(double[,] covariance) =>
        ConfidenceRadius * Math.Sqrt(Math.Max(0, (covariance[E, E] + covariance[N, N]) / 2));

    static double[,] Identity()
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
            m[i, i] = 1;
        return m;
    }

    static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                    sum += a[i, k] * b[k, j];
                m[i, j] = sum;
            }
        return m;
    }

    static double[,] Transpose(double[,] a)
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                m[i, j] = a[j, i];
        return m;
    }

    static double[,] Symmetric(double[,] a)
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                m[i, j] = (a[i, j] + a[j, i]) / 2;
        return m;
    }

    static double[] Subtract(double[] a, double[] b)
    {
        var v = new double[4];
        for (int i = 0; i < 4; i++)
            v[i] = a[i] - b[i];
        return v;
    }

    static double[,] Invert(double[,] a)
    {
        var m = (double[,])a.Clone();
        var inv = Identity();
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 4; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                    pivot = row;

            if (pivot != col)
                for (int k = 0; k < 4; k++)
                {
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                    (inv[col, k], inv[pivot, k]) = (inv[pivot, k], inv[col, k]);
                }

            double diagonal = m[col, col];
            for (int k = 0; k < 4; k++)
            {
                m[col, k] /= diagonal;
                inv[col, k] /= diagonal;
            }

            for (int row = 0; row < 4; row++)
            {
                if (row == col)
                    continue;
                double factor = m[row, col];
                for (int k = 0; k < 4; k++)
                {
                    m[row, k] -= factor * m[col, k];
                    inv[row, k] -= factor * inv[col, k];
                }
            }
        }
        return inv;
    }

    readonly record struct Waiting(T Item, int Node, double OffsetEast, double OffsetNorth);

    sealed record Node(double[] PredictedX, double[,] PredictedP, double[,] Transition, double[] FilteredX, double[,] FilteredP);
}
