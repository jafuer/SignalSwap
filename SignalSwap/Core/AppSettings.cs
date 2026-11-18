namespace SignalSwap.Core;

public enum MapStyle
{
    Street = 0,
    Satellite = 1,
    Hybrid = 2
}

public sealed class AppSettings
{
    public const string DefaultServerUrl = "https://api.signalswap.net:5080";
    const string Prefix = "settings.";

    public event Action<string>? Changed;

    public bool AutoToggle
    {
        get => Get(nameof(AutoToggle), true);
        set => Set(nameof(AutoToggle), value);
    }

    public QualityLevel ToggleBelow
    {
        get => (QualityLevel)Math.Clamp(Get(nameof(ToggleBelow), (int)QualityLevel.Good), (int)QualityLevel.Weak, (int)QualityLevel.Excellent);
        set => Set(nameof(ToggleBelow), (int)value);
    }

    public int ConfirmSeconds
    {
        get => Get(nameof(ConfirmSeconds), 10);
        set => Set(nameof(ConfirmSeconds), value);
    }

    public int RetryMinutes
    {
        get => Get(nameof(RetryMinutes), 5);
        set => Set(nameof(RetryMinutes), value);
    }

    public bool WakeScreen
    {
        get => Get(nameof(WakeScreen), true);
        set => Set(nameof(WakeScreen), value);
    }

    public bool ConnectionTest
    {
        get => Get(nameof(ConnectionTest), true);
        set => Set(nameof(ConnectionTest), value);
    }

    public bool Mapping
    {
        get => Get(nameof(Mapping), true);
        set => Set(nameof(Mapping), value);
    }

    public bool ShowEstimates
    {
        get => Get(nameof(ShowEstimates), true);
        set => Set(nameof(ShowEstimates), value);
    }

    public double CellSize
    {
        get => Get(nameof(CellSize), 5.0);
        set => Set(nameof(CellSize), value);
    }

    public MapStyle MapStyle
    {
        get => (MapStyle)Get(nameof(MapStyle), (int)MapStyle.Hybrid);
        set => Set(nameof(MapStyle), (int)value);
    }

    public int DropWindowSeconds
    {
        get => Get(nameof(DropWindowSeconds), 120);
        set => Set(nameof(DropWindowSeconds), value);
    }

    public bool KeepRunning
    {
        get => Get(nameof(KeepRunning), true);
        set => Set(nameof(KeepRunning), value);
    }

    public string ServerUrl
    {
        get => Get(nameof(ServerUrl), DefaultServerUrl);
        set => Set(nameof(ServerUrl), string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value.Trim().TrimEnd('/'));
    }

    static T Get<T>(string key, T fallback) => Preferences.Default.Get(Prefix + key, fallback);

    void Set<T>(string key, T value)
    {
        if (EqualityComparer<T>.Default.Equals(Get(key, value), value) && Preferences.Default.ContainsKey(Prefix + key))
            return;
        Preferences.Default.Set(Prefix + key, value);
        Changed?.Invoke(key);
    }
}
