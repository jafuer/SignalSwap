using Android.Content;
using Android.Hardware;

namespace SignalSwap;

public sealed class FloorTracker : Java.Lang.Object, ISensorEventListener
{
    const int DefaultFloor = 1;
    const double FloorHeightMeters = 3.8;
    const double Smoothing = 0.1;
    const string FloorKey = "floor_last";
    const string PressureKey = "floor_pressure";
    const string SavedAtKey = "floor_saved_at";
    static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4);
    static readonly TimeSpan RestoreWindow = TimeSpan.FromMinutes(30);
    static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    readonly SensorManager sensors =
        (SensorManager)global::Android.App.Application.Context.GetSystemService(Context.SensorService)!;
    double? pressure;
    double? basePressure;
    int baseFloor = DefaultFloor;
    int? candidate;
    DateTimeOffset candidateSince;
    DateTimeOffset lastSaved;

    public int Floor { get; private set; } = DefaultFloor;

    public event Action<int>? FloorChanged;

    public bool Start()
    {
        Stop();
        var barometer = sensors.GetDefaultSensor(SensorType.Pressure);
        if (barometer == null)
            return false;

        Restore();
        sensors.RegisterListener(this, barometer, SensorDelay.Normal);
        return true;
    }

    public void Stop() => sensors.UnregisterListener(this);

    public void Rebaseline()
    {
        baseFloor = Floor;
        basePressure = pressure;
        candidate = null;
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Values == null || e.Values.Count == 0)
            return;

        double reading = e.Values[0];
        double smoothed = pressure is double p ? p + Smoothing * (reading - p) : reading;
        pressure = smoothed;

        if (basePressure is not double baseline)
        {
            basePressure = smoothed;
            return;
        }

        double height = SensorManager.GetAltitude((float)baseline, (float)smoothed);
        int detected = baseFloor + (int)Math.Round(height / FloorHeightMeters);
        var now = DateTimeOffset.UtcNow;

        if (detected == Floor)
        {
            candidate = null;
            Save(smoothed, now, false);
            return;
        }

        if (candidate != detected)
        {
            candidate = detected;
            candidateSince = now;
            return;
        }
        if (now - candidateSince < SettleTime)
            return;

        Floor = detected;
        baseFloor = detected;
        basePressure = smoothed;
        candidate = null;
        Save(smoothed, now, true);
        FloorChanged?.Invoke(detected);
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
    {
    }

    void Restore()
    {
        var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(Preferences.Default.Get(SavedAtKey, 0L));
        double savedPressure = Preferences.Default.Get(PressureKey, 0.0);

        if (DateTimeOffset.UtcNow - savedAt > RestoreWindow || savedPressure <= 0)
        {
            Floor = DefaultFloor;
            baseFloor = DefaultFloor;
            basePressure = null;
        }
        else
        {
            Floor = Preferences.Default.Get(FloorKey, DefaultFloor);
            baseFloor = Floor;
            basePressure = savedPressure;
        }
        candidate = null;
    }

    void Save(double currentPressure, DateTimeOffset now, bool force)
    {
        if (!force && now - lastSaved < SaveInterval)
            return;

        Preferences.Default.Set(FloorKey, Floor);
        Preferences.Default.Set(PressureKey, currentPressure);
        Preferences.Default.Set(SavedAtKey, now.ToUnixTimeMilliseconds());
        lastSaved = now;
    }
}
