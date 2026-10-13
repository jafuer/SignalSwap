namespace SignalMonitorTest1;

public partial class MainPage : ContentPage
{
    readonly List<string> logLines = new();

    public MainPage()
    {
        InitializeComponent();
#if ANDROID
        
        WifiToggleAccessibilityService.LogMessage += message =>
            MainThread.BeginInvokeOnMainThread(() => AppendLog(message));
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshStatus();
    }

    void RefreshStatus()
    {
#if ANDROID
        bool serviceOn = WifiToggleAccessibilityService.Instance != null;
        ServiceLabel.Text = serviceOn
            ? "Accessibility service: ON"
            : "Accessibility service: OFF (use button 1)";
        WifiLabel.Text = "Wi-Fi: " + (WifiToggleAccessibilityService.IsWifiEnabled() ? "on" : "off");
        ToggleNowButton.IsEnabled = serviceOn;
        DelayedToggleButton.IsEnabled = serviceOn;
#endif
    }

    void OnOpenSettingsClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionAccessibilitySettings);
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.StartActivity(intent);
#endif
    }

    async void OnToggleNowClicked(object? sender, EventArgs e)
    {
#if ANDROID
        if (WifiToggleAccessibilityService.Instance is not { } service) return;
        ToggleNowButton.IsEnabled = false;
        await Task.Run(() => service.ToggleWifiAsync());
        RefreshStatus();
#else
        await Task.CompletedTask;
#endif
    }

    void OnDelayedToggleClicked(object? sender, EventArgs e)
    {
#if ANDROID
        WifiToggleAccessibilityService.Instance?.ToggleAfterDelay(TimeSpan.FromSeconds(10));
#endif
    }

    void OnRefreshClicked(object? sender, EventArgs e) => RefreshStatus();

    void AppendLog(string message)
    {
        logLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (logLines.Count > 80) logLines.RemoveAt(0);
        LogLabel.Text = string.Join(Environment.NewLine, logLines);
        RefreshStatus();
    }
}
