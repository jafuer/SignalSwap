using SignalSwap.Accounts;
using SignalSwap.Mapping;

namespace SignalSwap.Core;

public sealed class SignalEngine
{
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);

    readonly AppSettings settings;
    readonly AccountService account;
    readonly ReadingStore store;
    readonly SyncService sync;
    IDispatcherTimer? timer;
    bool ticking;

    public SignalEngine(AppSettings settings, AccountService account, ReadingStore store, SyncService sync)
    {
        this.settings = settings;
        this.account = account;
        this.store = store;
        this.sync = sync;
        Monitor = new SignalMonitor(settings, ConnectionProbe.Run);
        Toggler = new AutoToggler(settings, new AccessibilityWifiSwitch());
        Mapper = new MappingEngine(settings, store);
    }

    public event Action? Updated;

    public event Action? StoreReloaded;

    public SignalMonitor Monitor { get; }

    public AutoToggler Toggler { get; }

    public MappingEngine Mapper { get; }

    public ScoreHistory History { get; } = new(60);

    public bool Running { get; private set; }

    public bool LocationGranted { get; private set; }

    public string? BackgroundProblem { get; private set; }

    public string? LastError { get; private set; }

    public bool MapOnScreen { get; set; }

    public bool AppInForeground { get; set; } = true;

    public bool MappingActive => settings.Mapping && account.IsSignedIn && LocationGranted;

    public async Task StartAsync(bool locationGranted, bool activityGranted)
    {
        LocationGranted = locationGranted;
        if (Running)
        {
            StartBackground();
            return;
        }

        Running = true;
        if (timer == null)
        {
            await account.LoadAsync();
            await store.LoadAsync(account.IsSignedIn ? account.Session?.UserId : null);
            Toggler.Restore(DateTimeOffset.UtcNow);

            account.SessionChanged += OnSessionChanged;
            settings.Changed += OnSettingChanged;
            sync.ReadingsMerged += networks =>
            {
                Mapper.ResetCoverage(networks);
                Updated?.Invoke();
            };
            sync.NetworkCleared += Mapper.NetworkCleared;

            timer = Application.Current!.Dispatcher.CreateTimer();
            timer.Interval = TickEvery;
            timer.Tick += async (_, _) => await TickAsync();
        }

        Mapper.Initialize(locationGranted, activityGranted);
        timer.Start();
        StartBackground();
        await TickAsync();
    }

    public void Stop()
    {
        if (!Running)
            return;
        Running = false;
        timer?.Stop();
        Toggler.Release();
        _ = Mapper.ShutdownAsync();
        SignalSwapService.Stop();
    }

    void StartBackground()
    {
        if (!LocationGranted)
        {
            BackgroundProblem = "Location permission is off, so SignalSwap only works while the app is open.";
            return;
        }

        try
        {
            SignalSwapService.Start();
            BackgroundProblem = null;
        }
        catch (Exception ex)
        {
            BackgroundProblem = $"Background monitoring could not start, so SignalSwap pauses when the app is hidden: {ex.Message}";
        }
    }

    async Task TickAsync()
    {
        if (ticking || !Running)
            return;

        ticking = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wifi = WifiSampler.Read();
            var status = Monitor.Update(wifi, now, Toggler.WantsFastChecks);
            History.Add(now, status.Report);
            Toggler.Update(status, now);
            await Mapper.TickAsync(wifi, MappingActive, Toggler.HoldingWifiOff || (MapOnScreen && AppInForeground));
            sync.Tick(now);
            SignalSwapService.UpdateStatus(Describe(status));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            ticking = false;
        }

        Updated?.Invoke();
    }

    async void OnSessionChanged()
    {
        try
        {
            await store.LoadAsync(account.IsSignedIn ? account.Session?.UserId : null);
            Mapper.ResetCoverage();
            sync.Reset();
            StoreReloaded?.Invoke();
            if (account.IsSignedIn)
                await sync.SyncNowAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    void OnSettingChanged(string key)
    {
        if (key == nameof(AppSettings.CellSize))
            Mapper.ResetCoverage();
    }

    string Describe(SignalStatus status)
    {
        if (Toggler.HoldingWifiOff)
            return Toggler.RetryAt is { } at
                ? $"Wi-Fi is off until {at.ToLocalTime():h:mm tt} because the signal was weak"
                : "Wi-Fi is off because the signal was weak";

        if (!status.Wifi.Enabled)
            return "Wi-Fi is off";
        if (!status.Wifi.Connected)
            return "Not connected to Wi-Fi";

        var report = status.Report;
        string name = status.Wifi.Ssid ?? "Wi-Fi";
        string quality = report.NoInternet ? "no internet" : SignalScale.LabelOf(report.Level).ToLowerInvariant();
        string mapping = Mapper.Recording ? ", mapping" : "";
        return $"{name}: {quality}{mapping}";
    }
}
