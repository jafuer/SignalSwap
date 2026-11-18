namespace SignalSwap.Core;

public interface IWifiSwitch
{
    bool IsAvailable { get; }
    bool IsWifiEnabled { get; }
    bool IsScreenOn { get; }
    Task<bool> SetWifiAsync(bool enabled);
}

public enum ToggleState
{
    Disabled,
    NeedsAccessibility,
    Watching,
    Confirming,
    TurningOff,
    WaitingToRetry,
    TurningOn,
    Checking
}

public sealed class AutoToggler
{
    const string OffSinceKey = "toggler.off_since";
    const int LogSize = 40;
    static readonly TimeSpan CheckFor = TimeSpan.FromSeconds(8);
    static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(45);
    static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    readonly AppSettings settings;
    readonly IWifiSwitch wifi;
    readonly List<string> log = new();
    Task<bool>? pending;
    bool pendingEnable;
    DateTimeOffset? badSince;
    DateTimeOffset checkStarted;
    DateTimeOffset? joinedAt;
    DateTimeOffset backoffUntil;

    public AutoToggler(AppSettings settings, IWifiSwitch wifi)
    {
        this.settings = settings;
        this.wifi = wifi;
    }

    public event Action<string>? Logged;

    public ToggleState State { get; private set; } = ToggleState.Watching;

    public DateTimeOffset? RetryAt { get; private set; }

    public DateTimeOffset? BadSince => badSince;

    public bool HoldingWifiOff => State is ToggleState.WaitingToRetry or ToggleState.TurningOn;

    public bool WantsFastChecks => State == ToggleState.Checking;

    public bool WaitingForScreen { get; private set; }

    public IReadOnlyList<string> Log => log;

    TimeSpan Retry => TimeSpan.FromMinutes(Math.Max(1, settings.RetryMinutes));

    TimeSpan Confirm => TimeSpan.FromSeconds(Math.Max(0, settings.ConfirmSeconds));

    bool CanActNow => wifi.IsScreenOn || settings.WakeScreen;

    public void Restore(DateTimeOffset now)
    {
        long offSince = Preferences.Default.Get(OffSinceKey, 0L);
        if (offSince <= 0)
            return;

        if (wifi.IsWifiEnabled)
        {
            Preferences.Default.Remove(OffSinceKey);
            return;
        }

        var due = DateTimeOffset.FromUnixTimeMilliseconds(offSince) + Retry;
        State = ToggleState.WaitingToRetry;
        RetryAt = due < now ? now : due;
        Write("Wi-Fi was left off by SignalSwap, it will be checked again soon.");
    }

    public void Release()
    {
        if (State == ToggleState.WaitingToRetry && wifi.IsAvailable && !wifi.IsWifiEnabled)
            _ = wifi.SetWifiAsync(true);
        ClearHold();
    }

    public void Update(SignalStatus status, DateTimeOffset now)
    {
        if (pending != null)
        {
            if (!pending.IsCompleted)
                return;
            bool ok = pending.Status == TaskStatus.RanToCompletion && pending.Result;
            pending = null;
            Finish(ok, now);
            return;
        }

        WaitingForScreen = false;

        if (!settings.AutoToggle)
        {
            Stand(ToggleState.Disabled);
            return;
        }

        if (!wifi.IsAvailable)
        {
            if (State != ToggleState.WaitingToRetry)
                State = ToggleState.NeedsAccessibility;
            badSince = null;
            return;
        }

        if (State is ToggleState.Disabled or ToggleState.NeedsAccessibility)
            State = ToggleState.Watching;

        switch (State)
        {
            case ToggleState.Watching:
            case ToggleState.Confirming:
                Watch(status, now);
                break;
            case ToggleState.WaitingToRetry:
                WaitToRetry(now);
                break;
            case ToggleState.Checking:
                Check(status, now);
                break;
        }
    }

    void Stand(ToggleState state)
    {
        badSince = null;
        if (State == ToggleState.WaitingToRetry && wifi.IsAvailable && !wifi.IsWifiEnabled)
        {
            Begin(true, "Automatic toggling was turned off, turning Wi-Fi back on.");
            return;
        }

        if (State == ToggleState.WaitingToRetry)
            ClearHold();
        State = state;
    }

    void Watch(SignalStatus status, DateTimeOffset now)
    {
        var report = status.Report;
        if (!status.Wifi.Connected || report.Level == QualityLevel.None || report.Settling || !Below(report))
        {
            badSince = null;
            State = ToggleState.Watching;
            return;
        }

        badSince ??= now;
        State = ToggleState.Confirming;
        if (now - badSince.Value < Confirm || now < backoffUntil)
            return;

        if (!CanActNow)
        {
            WaitingForScreen = true;
            return;
        }

        int seconds = (int)Math.Round((now - badSince.Value).TotalSeconds);
        Begin(false, $"Signal has been {Describe(report)} for {seconds} s, turning Wi-Fi off for {Minutes(Retry)}.");
    }

    void WaitToRetry(DateTimeOffset now)
    {
        if (wifi.IsWifiEnabled)
        {
            ClearHold();
            State = ToggleState.Watching;
            Write("Wi-Fi was turned back on outside SignalSwap.");
            return;
        }

        if (RetryAt is { } due && now < due)
            return;

        if (!CanActNow)
        {
            WaitingForScreen = true;
            return;
        }

        Begin(true, "Turning Wi-Fi on to check the signal again.");
    }

    void Check(SignalStatus status, DateTimeOffset now)
    {
        if (!wifi.IsWifiEnabled)
        {
            State = ToggleState.Watching;
            Write("Wi-Fi was turned off outside SignalSwap.");
            return;
        }

        if (!status.Wifi.Connected)
        {
            joinedAt = null;
            if (now - checkStarted >= JoinTimeout)
            {
                State = ToggleState.Watching;
                Write("No network was joined, leaving Wi-Fi on.");
            }
            return;
        }

        joinedAt ??= now;
        var report = status.Report;
        if (now - joinedAt.Value < CheckFor || report.Settling || report.Level == QualityLevel.None)
            return;

        if (Below(report))
        {
            Begin(false, $"{status.Wifi.Ssid ?? "The network"} is still {Describe(report)}, turning Wi-Fi off again for {Minutes(Retry)}.");
            return;
        }

        State = ToggleState.Watching;
        Write($"{status.Wifi.Ssid ?? "The network"} is {Describe(report)}, keeping Wi-Fi on.");
    }

    void Begin(bool enable, string message)
    {
        Write(message);
        pendingEnable = enable;
        State = enable ? ToggleState.TurningOn : ToggleState.TurningOff;
        try
        {
            pending = wifi.SetWifiAsync(enable);
        }
        catch (Exception ex)
        {
            pending = Task.FromException<bool>(ex);
        }
    }

    void Finish(bool ok, DateTimeOffset now)
    {
        badSince = null;
        if (pendingEnable)
        {
            if (ok)
            {
                ClearHold();
                State = settings.AutoToggle ? ToggleState.Checking : ToggleState.Disabled;
                checkStarted = now;
                joinedAt = null;
                Write("Wi-Fi is on, checking the signal.");
            }
            else if (!settings.AutoToggle)
            {
                ClearHold();
                State = ToggleState.Disabled;
                Write("Could not turn Wi-Fi back on. Turn it on from Quick Settings.");
            }
            else
            {
                State = ToggleState.WaitingToRetry;
                RetryAt = now + FailureBackoff;
                Write("Could not turn Wi-Fi on, trying again in 1 minute.");
            }
            return;
        }

        if (ok)
        {
            State = ToggleState.WaitingToRetry;
            RetryAt = now + Retry;
            Preferences.Default.Set(OffSinceKey, now.ToUnixTimeMilliseconds());
            Write($"Wi-Fi is off. It will be checked again at {RetryAt.Value.ToLocalTime():h:mm tt}.");
        }
        else
        {
            State = ToggleState.Watching;
            backoffUntil = now + FailureBackoff;
            Write("Could not turn Wi-Fi off, trying again in 1 minute.");
        }
    }

    void ClearHold()
    {
        RetryAt = null;
        Preferences.Default.Remove(OffSinceKey);
    }

    bool Below(QualityReport report) => report.Level != QualityLevel.None && report.Level < settings.ToggleBelow;

    static string Describe(QualityReport report) => report.NoInternet
        ? "without internet"
        : $"{report.Level.ToString().ToLowerInvariant()} ({report.Score})";

    static string Minutes(TimeSpan span) => span.TotalMinutes == 1 ? "1 minute" : $"{span.TotalMinutes:0} minutes";

    void Write(string message)
    {
        string line = $"{DateTime.Now:h:mm:ss tt}  {message}";
        log.Insert(0, line);
        if (log.Count > LogSize)
            log.RemoveAt(log.Count - 1);
        Logged?.Invoke(line);
    }
}
