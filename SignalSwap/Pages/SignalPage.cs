using SignalSwap.Core;
using SignalSwap.Mapping;
using SignalSwap.Ui;

namespace SignalSwap.Pages;

public class SignalPage : ContentPage
{
    readonly SignalEngine engine = AppServices.Engine;
    readonly AppSettings settings = AppServices.Settings;
    readonly GaugeDrawable gauge = new();
    readonly GraphicsView gaugeView;
    readonly SparklineDrawable sparkline = new();
    readonly GraphicsView sparkView;
    readonly Label scoreLabel = new() { FontFamily = Theme.Semibold, FontSize = 40, HorizontalTextAlignment = TextAlignment.Center };
    readonly Label levelLabel = new() { FontFamily = Theme.Semibold, FontSize = 16, VerticalTextAlignment = TextAlignment.Center };
    readonly ContentView levelDot = new() { VerticalOptions = LayoutOptions.Center };
    readonly Label networkLabel = Theme.Heading();
    readonly Label networkCaption = Theme.Caption();
    readonly Label strengthValue = Theme.Caption();
    readonly Label linkValue = Theme.Caption();
    readonly Label connectionValue = Theme.Caption();
    readonly MeterBar strengthBar = new();
    readonly MeterBar linkBar = new();
    readonly MeterBar connectionBar = new();
    readonly Label breakdownNote = Theme.Caption();
    readonly Label toggleTitle = new() { FontFamily = Theme.Semibold, FontSize = 15 };
    readonly Label toggleDetail = Theme.Caption();
    readonly Label toggleLog = new() { FontSize = 12, TextColor = Theme.SubtleText, LineBreakMode = LineBreakMode.WordWrap };
    readonly Button accessibilityButton = Theme.SecondaryButton("Turn on the accessibility service");
    readonly Label historyCaption = Theme.Caption();
    readonly Label accessPoint = new();
    readonly Label channel = new();
    readonly Label linkRates = new();
    readonly Label latency = new();
    readonly Label internet = new();
    readonly Label security = new();
    readonly Label cellHeadline = new() { FontFamily = Theme.Semibold, FontSize = 15 };
    readonly ContentView cellDot = new() { VerticalOptions = LayoutOptions.Center };
    readonly Label cellDetails = Theme.Caption();
    readonly Label problems = new() { FontSize = 13, TextColor = Theme.Danger, IsVisible = false };
    bool listening;

    public SignalPage()
    {
        Title = "Signal";

        gaugeView = new GraphicsView { Drawable = gauge, HeightRequest = 130, WidthRequest = 240, HorizontalOptions = LayoutOptions.Center };
        sparkView = new GraphicsView { Drawable = sparkline, HeightRequest = 90 };
        accessibilityButton.Clicked += (_, _) => DeviceSettings.OpenAccessibility();

        var scoreStack = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 0,
            Children =
            {
                scoreLabel,
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    HorizontalOptions = LayoutOptions.Center,
                    Children = { levelDot, levelLabel }
                }
            }
        };
        var gaugeHost = new Grid { HeightRequest = 140 };
        gaugeHost.Add(gaugeView);
        gaugeHost.Add(scoreStack);

        var hero = Theme.Card(
            gaugeHost,
            new VerticalStackLayout
            {
                Spacing = 2,
                Children = { networkLabel, networkCaption }
            });
        networkLabel.HorizontalTextAlignment = TextAlignment.Center;
        networkCaption.HorizontalTextAlignment = TextAlignment.Center;

        var breakdown = Theme.Card(
            Theme.SectionTitle("What the rating is based on"),
            Meter("Signal strength", strengthValue, strengthBar),
            Meter("Link speed", linkValue, linkBar),
            Meter("Connection test", connectionValue, connectionBar),
            breakdownNote);

        var toggling = Theme.Card(
            Theme.SectionTitle("Automatic Wi-Fi toggling"),
            toggleTitle,
            toggleDetail,
            accessibilityButton,
            toggleLog);

        var history = Theme.Card(
            Theme.SectionTitle("Rating, last 2 minutes"),
            sparkView,
            historyCaption);

        var details = Theme.Card(
            Theme.SectionTitle("Connection details"),
            Theme.KeyValue("Access point", accessPoint),
            Theme.KeyValue("Band and channel", channel),
            Theme.KeyValue("Link down / up", linkRates),
            Theme.KeyValue("Test reply time", latency),
            Theme.KeyValue("Internet", internet),
            Theme.KeyValue("Security", security));

        var cellular = Theme.Card(
            Theme.SectionTitle("Cellular"),
            new HorizontalStackLayout { Spacing = 8, Children = { cellDot, cellHeadline } },
            cellDetails);

        Content = Theme.PageBody(problems, hero, toggling, breakdown, history, details, cellular);
        Unloaded += (_, _) => Listen(false);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Listen(true);
        Refresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Listen(false);
    }

    void Listen(bool on)
    {
        if (on == listening)
            return;
        listening = on;
        if (on)
            engine.Updated += Refresh;
        else
            engine.Updated -= Refresh;
    }

    void Refresh()
    {
        var status = engine.Monitor.Current;
        ShowHero(status);
        ShowBreakdown(status);
        ShowToggling(status);
        ShowHistory();
        ShowDetails(status);
        ShowCellular();
        ShowProblems();
    }

    void ShowHero(SignalStatus status)
    {
        var wifi = status.Wifi;
        var report = status.Report;
        gauge.Threshold = settings.AutoToggle ? ThresholdScore() : null;

        if (!wifi.Enabled || !wifi.Connected || report.Level == QualityLevel.None)
        {
            gauge.Score = null;
            scoreLabel.Text = "--";
            levelDot.Content = Theme.Dot(SignalScale.Unknown);
            levelLabel.Text = !wifi.Enabled ? "Wi-Fi is off" : "Not connected";
            networkLabel.Text = engine.Toggler.HoldingWifiOff ? "Waiting for a better signal" : "No Wi-Fi network";
            networkCaption.Text = engine.Toggler.HoldingWifiOff && engine.Toggler.RetryAt is { } at
                ? $"SignalSwap will try Wi-Fi again at {at.ToLocalTime():h:mm tt}"
                : "Using mobile data if it is available";
            gaugeView.Invalidate();
            return;
        }

        var color = SignalScale.ColorOf(report.Level);
        gauge.Score = report.Score;
        gauge.Color = color;
        scoreLabel.Text = report.Score.ToString();
        levelDot.Content = Theme.Dot(color);
        levelLabel.Text = report.NoInternet
            ? "No internet"
            : report.Settling ? $"{SignalScale.LabelOf(report.Level)} (settling)" : SignalScale.LabelOf(report.Level);
        networkLabel.Text = wifi.Ssid ?? "Network name hidden";
        string generation = wifi.WifiGeneration is int g ? $" · Wi-Fi {g}" : "";
        networkCaption.Text = $"{wifi.Band}{generation} · {wifi.Rssi} dBm";
        gaugeView.Invalidate();
    }

    void ShowBreakdown(SignalStatus status)
    {
        var report = status.Report;
        bool connected = status.Wifi.Connected && report.Level != QualityLevel.None;

        strengthValue.Text = connected && report.Rssi is double rssi ? $"{rssi:0} dBm (smoothed)" : "--";
        strengthBar.Set(connected ? report.StrengthScore : null, ColorFor(report.StrengthScore));

        linkValue.Text = connected && report.LinkMbps is int mbps ? $"{mbps} Mbps" : "--";
        linkBar.Set(connected ? report.LinkScore : null, ColorFor(report.LinkScore));

        if (!settings.ConnectionTest)
            connectionValue.Text = "Off in Settings";
        else if (!connected || report.ProbeCount == 0)
            connectionValue.Text = connected ? "Testing..." : "--";
        else if (report.ProbeFailures == report.ProbeCount)
            connectionValue.Text = "No replies";
        else
            connectionValue.Text = report.ProbeFailures > 0
                ? $"{report.LatencyMs:0} ms, {report.ProbeFailures} of {report.ProbeCount} failed"
                : $"{report.LatencyMs:0} ms";
        connectionBar.Set(connected ? report.ConnectionScore : null, ColorFor(report.ConnectionScore));

        breakdownNote.Text = report.NoInternet
            ? "Android says this network has no internet, so the rating is capped at Poor."
            : "The rating blends all three, and the weakest one pulls it down, so strong bars with failing tests still rate low.";
    }

    void ShowToggling(SignalStatus status)
    {
        var toggler = engine.Toggler;
        string target = SignalScale.LabelOf(settings.ToggleBelow);
        accessibilityButton.IsVisible = settings.AutoToggle && !DeviceSettings.AccessibilityServiceOn;

        (toggleTitle.Text, toggleDetail.Text) = toggler.State switch
        {
            ToggleState.Disabled => ("Off", "Turn it on in Settings to drop weak Wi-Fi automatically."),
            ToggleState.NeedsAccessibility => ("Needs the accessibility service",
                "Android only lets SignalSwap switch Wi-Fi by tapping Quick Settings, which needs the SignalSwap Wi-Fi toggle accessibility service."),
            ToggleState.Confirming when toggler.BadSince is { } since => ("Signal is below " + target,
                $"Wi-Fi turns off if it stays below {target} for {settings.ConfirmSeconds} s ({(DateTimeOffset.UtcNow - since).TotalSeconds:0} s so far)."),
            ToggleState.TurningOff => ("Turning Wi-Fi off", "Tapping the Wi-Fi tile in Quick Settings."),
            ToggleState.WaitingToRetry => ("Wi-Fi is off", toggler.RetryAt is { } at
                ? $"Checking again at {at.ToLocalTime():h:mm tt}. If the network is still below {target}, Wi-Fi goes off for another {settings.RetryMinutes} minutes."
                : "Checking again soon."),
            ToggleState.TurningOn => ("Turning Wi-Fi on", "Tapping the Wi-Fi tile in Quick Settings."),
            ToggleState.Checking => ("Checking the signal", $"Wi-Fi stays on only if the network is {target} or better."),
            _ => ("Watching", status.Wifi.Connected
                ? $"Wi-Fi turns off when the rating stays below {target} for {settings.ConfirmSeconds} s."
                : "Waiting for a Wi-Fi connection.")
        };

        if (toggler.WaitingForScreen)
            toggleDetail.Text += " Waiting for the screen to turn on, because waking the screen is off in Settings.";

        toggleLog.Text = string.Join("\n", toggler.Log.Take(4));
        toggleLog.IsVisible = toggler.Log.Count > 0;
    }

    void ShowHistory()
    {
        sparkline.Points = engine.History.Points.Select(p => p.Score).ToList();
        sparkline.Threshold = settings.AutoToggle ? ThresholdScore() : null;
        historyCaption.Text = settings.AutoToggle
            ? $"0 to 100, sampled every 2 s. The gray line is where {SignalScale.LabelOf(settings.ToggleBelow)} starts."
            : "0 to 100, sampled every 2 s.";
        sparkView.Invalidate();
    }

    void ShowDetails(SignalStatus status)
    {
        var wifi = status.Wifi;
        var report = status.Report;
        accessPoint.Text = wifi.Bssid ?? "--";
        channel.Text = wifi.FrequencyMhz is int mhz
            ? wifi.Channel is int ch ? $"{wifi.Band}, channel {ch}" : $"{mhz} MHz"
            : "--";
        string down = wifi.RxLinkMbps is int rx ? $"{rx}" : wifi.LinkSpeedMbps is int l ? $"{l}" : "--";
        string up = wifi.TxLinkMbps is int tx ? $"{tx}" : "--";
        linkRates.Text = wifi.Connected ? $"{down} / {up} Mbps" : "--";
        latency.Text = report.LatencyMs is double ms ? $"{ms:0} ms" : "--";
        internet.Text = wifi.Validated switch
        {
            true => "Working",
            false when wifi.Connected => "Not confirmed",
            _ => "--"
        };
        security.Text = wifi.SecurityType ?? "--";
    }

    void ShowCellular()
    {
        var cell = CellSampler.Read();
        cellHeadline.Text = cell.Level == QualityLevel.None ? cell.Headline : $"{cell.Headline} · {SignalScale.LabelOf(cell.Level)}";
        cellDot.Content = Theme.Dot(cell.Level == QualityLevel.None ? SignalScale.Unknown : SignalScale.ColorOf(cell.Level));
        cellDetails.Text = cell.Details;
    }

    void ShowProblems()
    {
        var lines = new[] { engine.BackgroundProblem, engine.LastError is { } e ? $"Error: {e}" : null }
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
        problems.Text = string.Join("\n", lines);
        problems.IsVisible = lines.Count > 0;
    }

    int ThresholdScore() => settings.ToggleBelow switch
    {
        QualityLevel.Excellent => SignalRater.ExcellentAt,
        QualityLevel.Good => SignalRater.GoodAt,
        QualityLevel.Fair => SignalRater.FairAt,
        _ => SignalRater.WeakAt
    };

    static Color ColorFor(int? score) => score is int s ? SignalScale.ColorOf(SignalRater.LevelOf(s)) : SignalScale.Unknown;

    static View Meter(string name, Label value, MeterBar bar)
    {
        value.HorizontalTextAlignment = TextAlignment.End;
        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        header.Add(new Label { Text = name, FontSize = 14 }, 0, 0);
        header.Add(value, 1, 0);
        return new VerticalStackLayout { Spacing = 6, Children = { header, bar } };
    }
}
