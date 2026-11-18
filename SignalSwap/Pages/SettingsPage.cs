using SignalSwap.Accounts;
using SignalSwap.Core;
using SignalSwap.Ui;

namespace SignalSwap.Pages;

public class SettingsPage : ContentPage
{
    static readonly (string Label, QualityLevel Value)[] ToggleLevels =
    {
        ("Excellent", QualityLevel.Excellent),
        ("Good", QualityLevel.Good),
        ("Fair", QualityLevel.Fair),
        ("Weak", QualityLevel.Weak)
    };

    static readonly (string Label, int Value)[] ConfirmChoices =
    {
        ("2 seconds", 2),
        ("5 seconds", 5),
        ("10 seconds", 10),
        ("20 seconds", 20),
        ("30 seconds", 30),
        ("1 minute", 60)
    };

    static readonly (string Label, int Value)[] RetryChoices =
    {
        ("1 minute", 1),
        ("2 minutes", 2),
        ("5 minutes", 5),
        ("10 minutes", 10),
        ("15 minutes", 15),
        ("30 minutes", 30)
    };

    static readonly (string Label, double Value)[] CellChoices =
    {
        ("3 m", 3),
        ("5 m", 5),
        ("10 m", 10),
        ("20 m", 20)
    };

    static readonly (string Label, MapStyle Value)[] StyleChoices =
    {
        ("Street", MapStyle.Street),
        ("Satellite", MapStyle.Satellite),
        ("Satellite with labels", MapStyle.Hybrid)
    };

    static readonly (string Label, int Value)[] DropChoices =
    {
        ("Never", 0),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("2 minutes", 120),
        ("5 minutes", 300)
    };

    readonly AppSettings settings = AppServices.Settings;
    readonly ApiClient api = AppServices.Api;
    readonly Label accessibilityStatus = new() { FontFamily = Theme.Semibold, FontSize = 13 };
    readonly Label batteryStatus = new() { FontFamily = Theme.Semibold, FontSize = 13 };
    readonly Entry serverEntry = new() { Keyboard = Keyboard.Url };
    readonly Label serverMessage = new() { FontSize = 13, IsVisible = false };

    public SettingsPage()
    {
        Title = "Settings";

        var openAccessibility = Theme.SecondaryButton("Open");
        openAccessibility.Clicked += (_, _) => DeviceSettings.OpenAccessibility();
        var openBattery = Theme.SecondaryButton("Allow");
        openBattery.Clicked += (_, _) => DeviceSettings.RequestBatteryExemption();
        var openApp = Theme.SecondaryButton("Open");
        openApp.Clicked += (_, _) => DeviceSettings.OpenAppDetails();

        var saveServer = Theme.PrimaryButton("Save");
        saveServer.Clicked += (_, _) => SaveServer();
        var testServer = Theme.SecondaryButton("Test");
        testServer.Clicked += async (_, _) => await TestServerAsync();
        var resetServer = Theme.SecondaryButton("Default");
        resetServer.Clicked += (_, _) =>
        {
            serverEntry.Text = AppSettings.DefaultServerUrl;
            SaveServer();
        };

        Content = Theme.PageBody(
            Section("Automatic Wi-Fi toggling",
                SwitchRow("Automatic Wi-Fi toggling",
                    "Turns Wi-Fi off when the connection is too weak to use, then turns it back on later to check again.",
                    () => settings.AutoToggle, v => settings.AutoToggle = v),
                PickerRow("Turn Wi-Fi off below",
                    "With Good, a rating of Fair or worse turns Wi-Fi off.",
                    ToggleLevels, () => settings.ToggleBelow, v => settings.ToggleBelow = v),
                PickerRow("Weak signal must last",
                    "Short dips are ignored so Wi-Fi doesn't flicker.",
                    ConfirmChoices, () => settings.ConfirmSeconds, v => settings.ConfirmSeconds = v),
                PickerRow("Try Wi-Fi again every",
                    "Wi-Fi turns back on after this long. If the network is still below the line, it goes off again right away.",
                    RetryChoices, () => settings.RetryMinutes, v => settings.RetryMinutes = v),
                SwitchRow("Wake the screen to toggle",
                    "Wi-Fi can only be switched from Quick Settings, which needs the screen on. When this is off, toggles wait until you turn the screen on.",
                    () => settings.WakeScreen, v => settings.WakeScreen = v),
                ActionRow("Accessibility service",
                    "Turn on \"SignalSwap Wi-Fi toggle\" so SignalSwap can tap the Wi-Fi tile.",
                    accessibilityStatus, openAccessibility)),

            Section("Signal monitor",
                SwitchRow("Connection test",
                    "Every 10 seconds, checks that data actually gets through Wi-Fi. Uses a few hundred bytes each time, only on Wi-Fi.",
                    () => settings.ConnectionTest, v => settings.ConnectionTest = v)),

            Section("Deadzone map",
                SwitchRow("Automatic mapping",
                    "Records signal strength and your position while you are on Wi-Fi and logged in.",
                    () => settings.Mapping, v => settings.Mapping = v),
                SwitchRow("Guess nearby cells",
                    "Fills empty cells next to measured ones with a faded estimate. Guesses are never saved or uploaded and are replaced as soon as you walk through them.",
                    () => settings.ShowEstimates, v => settings.ShowEstimates = v),
                PickerRow("Map cell size",
                    "Smaller cells show more detail but take longer to fill in.",
                    CellChoices, () => settings.CellSize, v => settings.CellSize = v),
                PickerRow("Map style",
                    null,
                    StyleChoices, () => settings.MapStyle, v => settings.MapStyle = v),
                PickerRow("Count a drop as a dead zone if you reconnect within",
                    "Drops where you never reconnect to the same network are thrown away.",
                    DropChoices, () => settings.DropWindowSeconds, v => settings.DropWindowSeconds = v)),

            Section("Background",
                SwitchRow("Keep running after closing the app",
                    "Toggling and mapping keep going after you swipe SignalSwap away. Turn this off to stop everything when you close it.",
                    () => settings.KeepRunning, v => settings.KeepRunning = v),
                ActionRow("Battery optimization",
                    "Some phones pause background apps. Allowing SignalSwap to run unrestricted keeps the 5 minute checks on time.",
                    batteryStatus, openBattery),
                ActionRow("App permissions",
                    "Location, physical activity and notifications.",
                    null, openApp)),

            Section("Server",
                serverEntry,
                Theme.Row(saveServer, testServer, resetServer),
                serverMessage),

            Theme.Caption($"SignalSwap {AppInfo.Current.VersionString}"));

        Unloaded += (_, _) => AppServices.Engine.Updated -= RefreshStatus;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        serverEntry.Text = settings.ServerUrl;
        RefreshStatus();
        AppServices.Engine.Updated -= RefreshStatus;
        AppServices.Engine.Updated += RefreshStatus;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppServices.Engine.Updated -= RefreshStatus;
    }

    void RefreshStatus()
    {
        bool accessibility = DeviceSettings.AccessibilityServiceOn;
        accessibilityStatus.Text = accessibility ? "On" : "Off";
        accessibilityStatus.TextColor = accessibility ? Theme.PrimaryDark : Theme.Danger;

        bool unrestricted = DeviceSettings.IgnoringBatteryOptimizations;
        batteryStatus.Text = unrestricted ? "Unrestricted" : "Optimized";
        batteryStatus.TextColor = unrestricted ? Theme.PrimaryDark : Theme.SubtleText;
    }

    void SaveServer()
    {
        string text = serverEntry.Text?.Trim() ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ShowServerMessage("Enter a full https:// address, for example https://api.signalswap.net:5080", true);
            return;
        }

        settings.ServerUrl = text;
        serverEntry.Text = settings.ServerUrl;
        ShowServerMessage("Saved.", false);
    }

    async Task TestServerAsync()
    {
        SaveServer();
        ShowServerMessage("Checking...", false);
        try
        {
            await api.CheckHealthAsync();
            ShowServerMessage($"{api.ServerHost} is up and its database answered.", false);
        }
        catch (Exception ex)
        {
            ShowServerMessage(ApiClient.Describe(ex), true);
        }
    }

    void ShowServerMessage(string message, bool error)
    {
        serverMessage.Text = message;
        serverMessage.TextColor = error ? Theme.Danger : Theme.PrimaryDark;
        serverMessage.IsVisible = true;
    }

    static View Section(string title, params View[] rows)
    {
        var stack = new VerticalStackLayout { Spacing = 14 };
        stack.Children.Add(Theme.SectionTitle(title));
        foreach (var row in rows)
            stack.Children.Add(row);
        return Theme.CardFor(stack);
    }

    static View Describe(string title, string? subtitle)
    {
        var stack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        stack.Children.Add(new Label { Text = title, FontSize = 15 });
        return stack;
    }

    static View SwitchRow(string title, string? subtitle, Func<bool> get, Action<bool> set)
    {
        var toggle = new Switch { IsToggled = get(), VerticalOptions = LayoutOptions.Center };
        toggle.Toggled += (_, e) => set(e.Value);

        var row = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        row.Add(Describe(title, subtitle), 0, 0);
        row.Add(toggle, 1, 0);
        return row;
    }

    static View PickerRow<T>(string title, string? subtitle, (string Label, T Value)[] choices, Func<T> get, Action<T> set)
    {
        var picker = new Picker
        {
            Title = title,
            ItemsSource = choices.Select(c => c.Label).ToList()
        };
        var current = get();
        int index = Array.FindIndex(choices, c => EqualityComparer<T>.Default.Equals(c.Value, current));
        picker.SelectedIndex = index;
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
                set(choices[picker.SelectedIndex].Value);
        };

        return new VerticalStackLayout
        {
            Spacing = 2,
            Children = { Describe(title, subtitle), picker }
        };
    }

    static View ActionRow(string title, string subtitle, Label? status, Button button)
    {
        button.VerticalOptions = LayoutOptions.Center;
        button.MinimumWidthRequest = 84;

        var right = new HorizontalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center };
        if (status != null)
        {
            status.VerticalOptions = LayoutOptions.Center;
            right.Children.Add(status);
        }
        right.Children.Add(button);

        var row = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        row.Add(Describe(title, subtitle), 0, 0);
        row.Add(right, 1, 0);
        return row;
    }
}
