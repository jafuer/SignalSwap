using System.Runtime.Versioning;
#if ANDROID
using Android.Content;
using Android.Net.Wifi;
using Android.Telephony;
#endif

namespace SignalMontiorTest2;

public class MainPage : ContentPage
{
    readonly Label wifiStatus = Big();
    readonly Label wifiDetails = Small();
    readonly Label cellStatus = Big();
    readonly Label cellDetails = Small();
    readonly Label locationDetails = Small();
    readonly Label history = Mono();
    readonly Label updated = new() { FontSize = 12, TextColor = Colors.Gray };
    readonly List<string> historyLines = new();

    IDispatcherTimer? timer;
    Location? lastLocation;
    string? locationProblem;
    int? lastWifiRssi;
    int? lastCellDbm;

    public MainPage()
    {
        Title = "Signal Monitor";
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    Card("WI-FI", wifiStatus, wifiDetails),
                    Card("CELLULAR", cellStatus, cellDetails),
                    Card("LOCATION", locationDetails),
                    Card("RECENT READINGS (dBm)", history),
                    updated
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (status == PermissionStatus.Granted)
            await StartLocationAsync();
        else
            locationProblem = "Location permission denied. Wi-Fi names and position are hidden.";

        timer ??= CreateTimer();
        Refresh();
        timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        timer?.Stop();
        Geolocation.Default.LocationChanged -= OnLocationChanged;
        if (Geolocation.Default.IsListeningForeground)
            Geolocation.Default.StopListeningForeground();
    }

    IDispatcherTimer CreateTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += (_, _) => Refresh();
        return t;
    }

    async Task StartLocationAsync()
    {
        try
        {
            Geolocation.Default.LocationChanged -= OnLocationChanged;
            Geolocation.Default.LocationChanged += OnLocationChanged;
            if (!Geolocation.Default.IsListeningForeground)
                await Geolocation.Default.StartListeningForegroundAsync(
                    new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2)));
            locationProblem = null;
        }
        catch (FeatureNotEnabledException)
        {
            locationProblem = "Location is turned off. Turn it on in Quick Settings, then reopen the app.";
        }
        catch (Exception ex)
        {
            locationProblem = $"Location unavailable: {ex.Message}";
        }
    }

    void OnLocationChanged(object? sender, GeolocationLocationChangedEventArgs e) => lastLocation = e.Location;

    void Refresh()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        ShowWifi(context);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            ShowCell(context);
        else
            SetStatus(cellStatus, "Needs Android 10+", Colors.Gray);
#endif
        ShowLocation();
        AddHistory();
        updated.Text = $"Updated {DateTime.Now:HH:mm:ss} · refreshes every 2 s";
    }

#if ANDROID
    void ShowWifi(Context context)
    {
        var wifi = context.GetSystemService(Context.WifiService) as WifiManager;
        if (wifi == null || !wifi.IsWifiEnabled)
        {
            SetStatus(wifiStatus, "Wi-Fi is off", Colors.Gray);
            wifiDetails.Text = "";
            lastWifiRssi = null;
            return;
        }

        var info = wifi.ConnectionInfo;
        if (info == null || info.Rssi <= -127)
        {
            SetStatus(wifiStatus, "Not connected", Colors.Gray);
            wifiDetails.Text = "";
            lastWifiRssi = null;
            return;
        }

        int rssi = info.Rssi;
        lastWifiRssi = rssi;
        var (quality, color) = RateWifi(rssi);
        SetStatus(wifiStatus, $"{rssi} dBm · {quality}", color);

        var ssid = info.SSID?.Trim('"');
        if (string.IsNullOrEmpty(ssid) || ssid == "<unknown ssid>")
            ssid = "(hidden until location is allowed and on)";

        string band = info.Frequency switch
        {
            >= 5925 => "6 GHz",
            >= 4900 => "5 GHz",
            > 0 => "2.4 GHz",
            _ => "unknown"
        };

        string bars = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? $"{wifi.CalculateSignalLevel(rssi)} of {wifi.MaxSignalLevel}"
            : $"{WifiManager.CalculateSignalLevel(rssi, 5)} of 4";

        wifiDetails.Text =
            $"Network: {ssid}\n" +
            $"Access point: {info.BSSID}\n" +
            $"Band: {band} ({info.Frequency} MHz)\n" +
            $"Link speed: {info.LinkSpeed} Mbps\n" +
            $"Bars: {bars}";
    }

    [SupportedOSPlatform("android29.0")]
    void ShowCell(Context context)
    {
        var phone = context.GetSystemService(Context.TelephonyService) as TelephonyManager;
        if (phone == null)
        {
            SetStatus(cellStatus, "No cellular hardware", Colors.Gray);
            cellDetails.Text = "";
            lastCellDbm = null;
            return;
        }

        var carrier = string.IsNullOrWhiteSpace(phone.NetworkOperatorName) ? "unknown" : phone.NetworkOperatorName;
        var strengths = phone.SignalStrength?.CellSignalStrengths;

        if (strengths == null || strengths.Count == 0)
        {
            SetStatus(cellStatus, "No signal", Colors.Red);
            cellDetails.Text = $"Carrier: {carrier}";
            lastCellDbm = null;
            return;
        }

        var primary = strengths[0];
        lastCellDbm = Valid(primary.Dbm);
        var (quality, color) = RateCell(primary);
        SetStatus(cellStatus, $"{Show(primary.Dbm)} dBm · {quality}", color);

        var lines = new List<string> { $"Carrier: {carrier}" };
        foreach (var s in strengths)
            lines.Add(Describe(s));
        cellDetails.Text = string.Join("\n", lines);
    }

    [SupportedOSPlatform("android29.0")]
    static string Describe(CellSignalStrength s) => s switch
    {
        CellSignalStrengthNr nr =>
            $"5G: RSRP {Show(nr.SsRsrp)} dBm, RSRQ {Show(nr.SsRsrq)} dB, SINR {Show(nr.SsSinr)} dB, bars {nr.Level} of 4",
        CellSignalStrengthLte lte =>
            $"LTE: RSRP {Show(lte.Rsrp)} dBm, RSRQ {Show(lte.Rsrq)} dB, SNR {Show(lte.Rssnr)} dB, bars {lte.Level} of 4",
        CellSignalStrengthWcdma w => $"3G: {Show(w.Dbm)} dBm, bars {w.Level} of 4",
        CellSignalStrengthGsm g => $"2G: {Show(g.Dbm)} dBm, bars {g.Level} of 4",
        _ => $"{s.GetType().Name}: {Show(s.Dbm)} dBm, bars {s.Level} of 4"
    };

    [SupportedOSPlatform("android29.0")]
    static (string, Color) RateCell(CellSignalStrength s)
    {
        int? rsrp = s switch
        {
            CellSignalStrengthLte lte => Valid(lte.Rsrp),
            CellSignalStrengthNr nr => Valid(nr.SsRsrp),
            _ => null
        };

        if (rsrp is int r)
            return r switch
            {
                >= -80 => ("Excellent", Colors.Green),
                >= -90 => ("Good", Colors.LimeGreen),
                >= -100 => ("Fair", Colors.Orange),
                >= -110 => ("Weak", Colors.OrangeRed),
                _ => ("Unusable", Colors.Red)
            };

        return s.Level switch
        {
            >= 4 => ("Excellent", Colors.Green),
            3 => ("Good", Colors.LimeGreen),
            2 => ("Fair", Colors.Orange),
            1 => ("Weak", Colors.OrangeRed),
            _ => ("Unusable", Colors.Red)
        };
    }
#endif

    void ShowLocation()
    {
        if (locationProblem != null)
        {
            locationDetails.Text = locationProblem;
            return;
        }

        if (lastLocation is not { } loc)
        {
            locationDetails.Text = "Waiting for a GPS fix...";
            return;
        }

        string accuracy = loc.Accuracy is double a ? $"±{a:0} m" : "unknown";
        bool precise = loc.Accuracy is double b && b <= 25;
        double age = (DateTimeOffset.UtcNow - loc.Timestamp).TotalSeconds;

        locationDetails.Text =
            $"{loc.Latitude:0.00000}, {loc.Longitude:0.00000}\n" +
            $"Accuracy: {accuracy}{(precise ? "" : " (too rough to map)")}\n" +
            $"Fix age: {age:0} s";
    }

    void AddHistory()
    {
        string wifi = lastWifiRssi is int w ? w.ToString().PadLeft(4) : "   -";
        string cell = lastCellDbm is int c ? c.ToString().PadLeft(5) : "    -";
        historyLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  Wi-Fi {wifi}  Cell {cell}");
        if (historyLines.Count > 12)
            historyLines.RemoveAt(historyLines.Count - 1);
        history.Text = string.Join("\n", historyLines);
    }

    static (string, Color) RateWifi(int rssi) => rssi switch
    {
        >= -55 => ("Excellent", Colors.Green),
        >= -67 => ("Good", Colors.LimeGreen),
        >= -75 => ("Fair", Colors.Orange),
        >= -82 => ("Weak", Colors.OrangeRed),
        _ => ("Unusable", Colors.Red)
    };

    static int? Valid(int value) => value == int.MaxValue || value == int.MinValue ? null : value;

    static string Show(int value) => Valid(value)?.ToString() ?? "n/a";

    static void SetStatus(Label label, string text, Color color)
    {
        label.Text = text;
        label.TextColor = color;
    }

    static Label Big() => new() { FontSize = 24, FontAttributes = FontAttributes.Bold };

    static Label Small() => new() { FontSize = 14 };

    static Label Mono() => new() { FontSize = 13, FontFamily = "monospace" };

    static Border Card(string title, params View[] views)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(new Label
        {
            Text = title,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.Gray
        });
        foreach (var view in views)
            stack.Children.Add(view);

        return new Border
        {
            Padding = 14,
            Stroke = Colors.Gray,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = stack
        };
    }
}
