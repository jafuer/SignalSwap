using Android.Content;
using Android.Hardware;

namespace SignalSwap;

public sealed class DeadReckoner : Java.Lang.Object, ISensorEventListener
{
    const string StepLengthKey = "step_length";
    const double DefaultStepLength = 0.7;
    const double MinStepLength = 0.45;
    const double MaxStepLength = 1.0;
    const double CalibrationWeight = 0.3;
    const double CleanFieldTolerance = 0.15;
    const double OffsetBlend = 0.02;
    const double UprightPitch = 50 * Math.PI / 180;
    const double TiltedRoll = 60 * Math.PI / 180;

    readonly SensorManager sensors =
        (SensorManager)global::Android.App.Application.Context.GetSystemService(Context.SensorService)!;
    readonly float[] matrix = new float[9];
    readonly float[] remapped = new float[9];
    readonly float[] orientation = new float[3];

    double? gameAzimuth;
    double? magneticAzimuth;
    double offset;
    bool offsetReady;
    bool useGame;
    bool fieldClean = true;
    bool pocketed;
    bool tilted;
    double heading;
    bool hasHeading;
    double expectedFieldMicroTesla = 50;

    public double StepLength { get; private set; } = Preferences.Default.Get(StepLengthKey, DefaultStepLength);
    public double DeclinationDegrees { get; private set; }
    public double East { get; private set; }
    public double North { get; private set; }
    public double Walked { get; private set; }
    public int Steps { get; private set; }

    public string? PoseWarning => pocketed
        ? "The phone seems to be in a pocket, so steps keep your last direction. Hold it in front of you."
        : tilted
            ? "Hold the phone flat or upright in front of you for an accurate direction."
            : null;

    public event Action? Stepped;

    public bool Start()
    {
        Stop();

        var step = sensors.GetDefaultSensor(SensorType.StepDetector);
        var magneticRotation = sensors.GetDefaultSensor(SensorType.RotationVector);
        if (step == null || magneticRotation == null)
            return false;

        var gameRotation = sensors.GetDefaultSensor(SensorType.GameRotationVector);
        useGame = gameRotation != null;

        sensors.RegisterListener(this, magneticRotation, SensorDelay.Game);
        if (gameRotation != null)
            sensors.RegisterListener(this, gameRotation, SensorDelay.Game);
        if (sensors.GetDefaultSensor(SensorType.MagneticField) is { } magnetometer)
            sensors.RegisterListener(this, magnetometer, SensorDelay.Game);
        if (sensors.GetDefaultSensor(SensorType.Proximity) is { } proximity)
            sensors.RegisterListener(this, proximity, SensorDelay.Normal);
        sensors.RegisterListener(this, step, SensorDelay.Fastest);
        return true;
    }

    public void Stop() => sensors.UnregisterListener(this);

    public void SetDeclination(double latitude, double longitude, double altitudeMeters)
    {
        var field = new GeomagneticField(
            (float)latitude,
            (float)longitude,
            (float)altitudeMeters,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        DeclinationDegrees = field.Declination;
        expectedFieldMicroTesla = field.FieldStrength / 1000.0;
    }

    public void Calibrate(double scale)
    {
        StepLength = Math.Clamp(StepLength * (1 + CalibrationWeight * (scale - 1)), MinStepLength, MaxStepLength);
        Preferences.Default.Set(StepLengthKey, StepLength);
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Sensor == null || e.Values == null || e.Values.Count == 0)
            return;

        switch (e.Sensor.Type)
        {
            case SensorType.GameRotationVector:
                gameAzimuth = Azimuth(e.Values);
                UpdateHeading();
                break;
            case SensorType.RotationVector:
                magneticAzimuth = Azimuth(e.Values);
                UpdateHeading();
                break;
            case SensorType.MagneticField when e.Values.Count >= 3:
                double strength = Math.Sqrt(e.Values[0] * e.Values[0] + e.Values[1] * e.Values[1] + e.Values[2] * e.Values[2]);
                fieldClean = Math.Abs(strength - expectedFieldMicroTesla) / expectedFieldMicroTesla <= CleanFieldTolerance;
                break;
            case SensorType.Proximity:
                pocketed = e.Values[0] < e.Sensor.MaximumRange;
                break;
            case SensorType.StepDetector:
                OnStep();
                break;
        }
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
    {
    }

    double Azimuth(IList<float> values)
    {
        SensorManager.GetRotationMatrixFromVector(matrix, values.ToArray());
        SensorManager.GetOrientation(matrix, orientation);

        double pitch = orientation[1];
        double roll = orientation[2];
        if (Math.Abs(pitch) > UprightPitch)
        {
            SensorManager.RemapCoordinateSystem(matrix, Axis.X, Axis.Z, remapped);
            SensorManager.GetOrientation(remapped, orientation);
            tilted = false;
        }
        else
        {
            tilted = Math.Abs(roll) > TiltedRoll;
        }
        return orientation[0];
    }

    void UpdateHeading()
    {
        if (pocketed)
            return;

        double raw;
        if (useGame)
        {
            if (gameAzimuth is not double game)
                return;

            if (magneticAzimuth is double magnetic)
            {
                double target = Wrap(magnetic - game);
                if (!offsetReady)
                {
                    offset = target;
                    offsetReady = true;
                }
                else if (fieldClean)
                {
                    offset = Wrap(offset + OffsetBlend * Wrap(target - offset));
                }
            }

            if (!offsetReady)
                return;
            raw = game + offset;
        }
        else if (magneticAzimuth is double magnetic)
        {
            raw = magnetic;
        }
        else
        {
            return;
        }

        heading = raw + DeclinationDegrees * Math.PI / 180;
        hasHeading = true;
    }

    void OnStep()
    {
        if (!hasHeading)
            return;

        East += StepLength * Math.Sin(heading);
        North += StepLength * Math.Cos(heading);
        Walked += StepLength;
        Steps++;
        Stepped?.Invoke();
    }

    static double Wrap(double angle) => Math.IEEERemainder(angle, 2 * Math.PI);
}
