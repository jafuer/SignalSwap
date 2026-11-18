using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SignalSwap.Accounts;
using SignalSwap.Core;
using SignalSwap.Mapping;
using SignalSwap.Ui;
using MauiMap = Microsoft.Maui.Controls.Maps.Map;

namespace SignalSwap.Pages;

public class DeadzonePage : ContentPage
{
    const double EstimateDotMeters = 1.5;
    static readonly Color EstimateColor = Color.FromArgb("#4AA8E8");

    readonly SignalEngine engine = AppServices.Engine;
    readonly AppSettings settings = AppServices.Settings;
    readonly AccountService account = AppServices.Account;
    readonly SyncService sync = AppServices.Sync;
    readonly ReadingStore store = AppServices.Store;
    readonly Dictionary<CellKey, Polygon> cellShapes = new();
    readonly Dictionary<CellKey, (Polygon Shape, CellEstimate? Guess)> guesses = new();
    readonly ObservableCollection<string> networks = new();
    readonly Grid root = new();
    readonly Picker networkPicker = new() { Title = "Network" };
    readonly Button hereButton = Theme.SecondaryButton("Here");
    readonly Button fitButton = Theme.SecondaryButton("Fit");
    readonly Button clearButton = Theme.DangerButton("Clear");
    readonly Label wifiStatus = new() { FontFamily = Theme.Semibold, FontSize = 16 };
    readonly ContentView wifiDot = new() { VerticalOptions = LayoutOptions.Center };
    readonly Label details = Theme.Caption();
    readonly Label cellInfo = new() { FontSize = 13 };
    readonly Border signedOut;

    MauiMap map;
    SignalGrid? grid;
    Circle estimateRing = new();
    Circle estimateDot = new();
    Location? shownEstimate;
    double shownAccuracy;
    int drawnVersion = -1;
    string? drawnNetwork;
    int drawnFloor = int.MinValue;
    double drawnCellSize;
    bool hereArmed;
    bool centered;
    bool listening;
    bool changingPicker;

    public DeadzonePage()
    {
        Title = "Deadzone Map";

        networkPicker.ItemsSource = networks;
        networkPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!changingPicker)
                RebuildMap(true);
        };
        hereButton.Clicked += (_, _) => ToggleHere();
        fitButton.Clicked += (_, _) => CenterMap();
        clearButton.Clicked += async (_, _) => await ClearNetworkAsync();

        var goToAccount = Theme.PrimaryButton("Go to Account");
        goToAccount.Clicked += async (_, _) => await AppShell.OpenTabAsync(AppShell.AccountRoute);
        signedOut = Theme.Card(
            Theme.Heading("Log in to map dead zones"),
            Theme.Caption("Mapping runs automatically while you are on Wi-Fi and saves each network's map to your account."),
            goToAccount);
        signedOut.Margin = new Thickness(16);
        signedOut.VerticalOptions = LayoutOptions.Center;

        var panel = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12, 16, 12),
            Spacing = 8,
            BackgroundColor = Theme.Surface,
            Children =
            {
                new HorizontalStackLayout { Spacing = 8, Children = { wifiDot, wifiStatus } },
                details,
                Theme.Row(hereButton, fitButton, clearButton),
                networkPicker,
                Legend(),
                new Label { Text = "Faded cells are guesses from nearby readings until you walk through them.", FontSize = 12, TextColor = Theme.SubtleText },
                cellInfo
            }
        };

        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.Add(panel, 0, 1);

        map = CreateMap();
        AddEstimateMarker();
        root.Add(map, 0, 0);
        root.Add(signedOut, 0, 0);
        Content = root;
        Unloaded += (_, _) => Listen(false);
    }

    string? SelectedNetwork => networkPicker.SelectedItem as string;

    MappingEngine Mapper => engine.Mapper;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        engine.MapOnScreen = true;
        Listen(true);
        ReloadNetworks();
        if (NeedsRebuild())
            RebuildMap(!centered);
        RefreshStatus();
        try
        {
            await sync.RefreshMapsAsync();
        }
        catch (Exception)
        {
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        engine.MapOnScreen = false;
        Listen(false);
    }

    void Listen(bool on)
    {
        if (on == listening)
            return;
        listening = on;
        if (on)
        {
            engine.Updated += RefreshStatus;
            engine.StoreReloaded += OnStoreReloaded;
            Mapper.ReadingSaved += OnReadingSaved;
            Mapper.NetworkJoined += OnNetworkJoined;
            Mapper.FloorChanged += OnFloorChanged;
            Mapper.Stepped += OnStepped;
            sync.ReadingsMerged += OnReadingsMerged;
            sync.NetworkCleared += OnNetworkCleared;
            settings.Changed += OnSettingChanged;
        }
        else
        {
            engine.Updated -= RefreshStatus;
            engine.StoreReloaded -= OnStoreReloaded;
            Mapper.ReadingSaved -= OnReadingSaved;
            Mapper.NetworkJoined -= OnNetworkJoined;
            Mapper.FloorChanged -= OnFloorChanged;
            Mapper.Stepped -= OnStepped;
            sync.ReadingsMerged -= OnReadingsMerged;
            sync.NetworkCleared -= OnNetworkCleared;
            settings.Changed -= OnSettingChanged;
        }
    }

    bool NeedsRebuild() =>
        drawnVersion != store.Version ||
        drawnNetwork != SelectedNetwork ||
        drawnFloor != Mapper.CurrentFloor ||
        drawnCellSize != settings.CellSize;

    MauiMap CreateMap()
    {
        var created = new MauiMap { MapType = MapTypeFor(settings.MapStyle), IsShowingUser = false };
        created.MapClicked += OnMapClicked;
        return created;
    }

    static MapType MapTypeFor(MapStyle style) => style switch
    {
        MapStyle.Street => MapType.Street,
        MapStyle.Satellite => MapType.Satellite,
        _ => MapType.Hybrid
    };

    void ReloadNetworks()
    {
        var current = SelectedNetwork;
        var names = store.Networks.ToList();
        if (Mapper.CurrentSsid is { } connected && account.IsSignedIn && !names.Contains(connected))
            names.Insert(0, connected);

        changingPicker = true;
        networks.Clear();
        foreach (var name in names)
            networks.Add(name);

        string? pick = current != null && networks.Contains(current)
            ? current
            : Mapper.CurrentSsid is { } ssid && networks.Contains(ssid) ? ssid : networks.FirstOrDefault();
        networkPicker.SelectedIndex = pick == null ? -1 : networks.IndexOf(pick);
        changingPicker = false;
    }

    void SelectNetwork(string ssid)
    {
        if (!networks.Contains(ssid))
            networks.Insert(0, ssid);
        if (SelectedNetwork != ssid)
            networkPicker.SelectedIndex = networks.IndexOf(ssid);
    }

    void OnStoreReloaded()
    {
        ReloadNetworks();
        RebuildMap(true);
        RefreshStatus();
    }

    void OnNetworkJoined(string ssid)
    {
        if (account.IsSignedIn)
            SelectNetwork(ssid);
    }

    void OnFloorChanged()
    {
        RebuildMap(false);
        RefreshStatus();
    }

    void OnStepped() => UpdateEstimateMarker(Mapper.ResolvePosition());

    void OnReadingsMerged(IReadOnlyCollection<string> changed)
    {
        ReloadNetworks();
        if (drawnNetwork != SelectedNetwork || (SelectedNetwork is { } ssid && changed.Contains(ssid)))
            RebuildMap(!centered);
    }

    void OnNetworkCleared(string ssid)
    {
        ReloadNetworks();
        RebuildMap(false);
        RefreshStatus();
    }

    void OnSettingChanged(string key)
    {
        if (key == nameof(AppSettings.MapStyle))
            map.MapType = MapTypeFor(settings.MapStyle);
        else if (key == nameof(AppSettings.CellSize) || key == nameof(AppSettings.ShowEstimates))
            RebuildMap(false);
    }

    void OnReadingSaved(SignalReading reading)
    {
        if (!networks.Contains(reading.Ssid))
            networks.Add(reading.Ssid);
        if (networkPicker.SelectedIndex < 0)
        {
            networkPicker.SelectedIndex = networks.IndexOf(reading.Ssid);
            return;
        }

        if (SelectedNetwork != reading.Ssid || (reading.Floor ?? MappingEngine.DefaultFloor) != Mapper.CurrentFloor)
            return;

        if (grid == null || grid.Cells.Count == 0)
            grid = new SignalGrid(settings.CellSize, reading.Latitude);

        var stats = grid.Add(reading);
        DrawCell(stats);
        RefreshGuesses(new[] { stats.Key });
        drawnVersion = store.Version;
    }

    List<SignalReading> ReadingsOnFloor() => SelectedNetwork is { } ssid
        ? store.ForNetwork(ssid).Where(r => (r.Floor ?? MappingEngine.DefaultFloor) == Mapper.CurrentFloor).ToList()
        : new List<SignalReading>();

    void RebuildMap(bool recenter)
    {
        var readings = ReadingsOnFloor();
        double referenceLatitude = readings.Count > 0 ? readings[0].Latitude : Mapper.LastFix?.Latitude ?? 0;

        grid = new SignalGrid(settings.CellSize, referenceLatitude);
        foreach (var reading in readings)
            grid.Add(reading);

        var region = map.VisibleRegion;
        var estimate = shownEstimate;
        var accuracy = shownAccuracy;

        bool fresh = drawnVersion == -1;
        if (!fresh)
        {
            map.MapClicked -= OnMapClicked;
            root.Remove(map);
            map = CreateMap();
        }

        cellShapes.Clear();
        guesses.Clear();
        foreach (var stats in grid.Cells.Values)
            DrawCell(stats);
        RefreshGuesses(grid.Cells.Keys.ToList());
        AddEstimateMarker();
        if (estimate != null)
            ShowEstimate(estimate, accuracy);

        if (!fresh)
        {
            root.Insert(0, map);
            Grid.SetRow(map, 0);
        }

        drawnVersion = store.Version;
        drawnNetwork = SelectedNetwork;
        drawnFloor = Mapper.CurrentFloor;
        drawnCellSize = settings.CellSize;
        cellInfo.Text = "";

        if (recenter || region == null)
            CenterMap();
        else if (!fresh)
            map.MoveToRegion(region);
    }

    void DrawCell(CellStats stats)
    {
        if (grid == null)
            return;

        var fill = stats.Color.WithAlpha(0.5f);
        var stroke = stats.Color.WithAlpha(0.95f);

        if (cellShapes.TryGetValue(stats.Key, out var existing))
        {
            if (!Equals(existing.FillColor, fill))
            {
                existing.FillColor = fill;
                existing.StrokeColor = stroke;
            }
            return;
        }

        var polygon = new Polygon { FillColor = fill, StrokeColor = stroke, StrokeWidth = 1 };
        foreach (var corner in grid.Corners(stats.Key))
            polygon.Geopath.Add(corner);

        map.MapElements.Add(polygon);
        cellShapes[stats.Key] = polygon;
    }

    void RefreshGuesses(IReadOnlyCollection<CellKey> changed)
    {
        if (grid == null)
            return;

        foreach (var key in CellEstimator.CandidatesNear(grid.Cells, changed).ToList())
        {
            var guess = settings.ShowEstimates ? CellEstimator.Estimate(grid.Cells, key) : null;
            guesses.TryGetValue(key, out var existing);

            if (guess is not { } found)
            {
                if (existing.Shape != null && existing.Guess != null)
                {
                    existing.Shape.FillColor = Colors.Transparent;
                    guesses[key] = (existing.Shape, null);
                }
                continue;
            }

            var color = SignalScale.ColorOf(found.Level).WithAlpha(0.22f);
            if (existing.Shape != null)
            {
                if (existing.Guess?.Level != found.Level)
                    existing.Shape.FillColor = color;
                guesses[key] = (existing.Shape, found);
                continue;
            }

            var polygon = new Polygon { FillColor = color, StrokeColor = Colors.Transparent, StrokeWidth = 0 };
            foreach (var corner in grid.Corners(key))
                polygon.Geopath.Add(corner);
            map.MapElements.Add(polygon);
            guesses[key] = (polygon, found);
        }
    }

    void CenterMap()
    {
        var readings = ReadingsOnFloor();
        if (readings.Count == 0 && SelectedNetwork is { } ssid)
            readings = store.ForNetwork(ssid).ToList();

        if (readings.Count > 0)
        {
            double south = readings.Min(r => r.Latitude);
            double north = readings.Max(r => r.Latitude);
            double west = readings.Min(r => r.Longitude);
            double east = readings.Max(r => r.Longitude);
            double spanMeters = Location.CalculateDistance(south, west, north, east, DistanceUnits.Kilometers) * 1000;
            MoveTo(new Location((south + north) / 2, (west + east) / 2), Math.Max(40, spanMeters / 2 + 15));
            return;
        }

        if (Mapper.LastFix is { } here)
            MoveTo(here, 60);
    }

    void MoveTo(Location center, double radiusMeters)
    {
        map.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromMeters(radiusMeters)));
        centered = true;
    }

    async void OnMapClicked(object? sender, MapClickedEventArgs e)
    {
        if (Mapper.CanAnchor && (hereArmed || Mapper.ResolvePosition().Kind == PositionKind.None))
        {
            hereArmed = false;
            await Mapper.AnchorAtAsync(e.Location.Latitude, e.Location.Longitude);
            var position = Mapper.ResolvePosition();
            UpdateEstimateMarker(position);
            RefreshStatus();
            return;
        }

        ShowCellInfo(e.Location);
    }

    void ToggleHere()
    {
        if (!Mapper.CanAnchor)
            return;
        hereArmed = !hereArmed;
        RefreshStatus();
    }

    void AddEstimateMarker()
    {
        var hidden = Mapper.LastFix ?? new Location(0, 0);
        estimateRing = new Circle { StrokeWidth = 1, Center = hidden, Radius = Distance.FromMeters(EstimateDotMeters * 2) };
        estimateDot = new Circle { StrokeWidth = 2, Center = hidden, Radius = Distance.FromMeters(EstimateDotMeters) };
        shownEstimate = null;
        HideEstimate();
        map.MapElements.Add(estimateRing);
        map.MapElements.Add(estimateDot);
    }

    void UpdateEstimateMarker(PositionState position)
    {
        if (position.Kind == PositionKind.None || position.Position is not { } here)
        {
            if (shownEstimate != null)
            {
                shownEstimate = null;
                HideEstimate();
            }
            return;
        }

        double accuracy = here.Accuracy ?? MappingEngine.TapAnchorAccuracyMeters;
        if (shownEstimate != null &&
            Location.CalculateDistance(shownEstimate, here, DistanceUnits.Kilometers) < 0.0003 &&
            Math.Abs(accuracy - shownAccuracy) < 0.5)
            return;

        ShowEstimate(here, accuracy);
    }

    void ShowEstimate(Location here, double accuracy)
    {
        shownEstimate = here;
        shownAccuracy = accuracy;

        estimateRing.Center = here;
        estimateRing.Radius = Distance.FromMeters(Math.Max(EstimateDotMeters * 2, accuracy));
        estimateRing.StrokeColor = EstimateColor.WithAlpha(0.7f);
        estimateRing.FillColor = EstimateColor.WithAlpha(0.15f);

        estimateDot.Center = here;
        estimateDot.StrokeColor = Colors.White;
        estimateDot.FillColor = EstimateColor;
    }

    void HideEstimate()
    {
        estimateRing.StrokeColor = Colors.Transparent;
        estimateRing.FillColor = Colors.Transparent;
        estimateDot.StrokeColor = Colors.Transparent;
        estimateDot.FillColor = Colors.Transparent;
    }

    void ShowCellInfo(Location location)
    {
        if (grid?.Find(location.Latitude, location.Longitude) is not { } cell)
        {
            cellInfo.Text = grid != null && guesses.TryGetValue(grid.KeyOf(location.Latitude, location.Longitude), out var guessed) && guessed.Guess is { } shown
                ? $"Guessed {SignalScale.LabelOf(shown.Level)} (about {shown.Rssi:0} dBm) from {shown.Sources} nearby cell(s). Not measured yet."
                : "No readings in this cell yet.";
            return;
        }

        string signal = cell.Average is double avg
            ? $"{SignalScale.LabelOf(cell.Level)}: average {avg:0} dBm, best {cell.Max}, worst {cell.Min}."
            : "No signal recorded here.";
        string state = cell.Samples >= MappingEngine.SamplesPerCell
            ? "Mapped."
            : $"Needs {MappingEngine.SamplesPerCell - cell.Samples} more sample(s).";
        cellInfo.Text = $"{signal} {cell.Samples} samples, {cell.Drops} drops, {cell.AccessPoints.Count} access point(s). {state}";
    }

    async Task ClearNetworkAsync()
    {
        if (SelectedNetwork is not { } ssid)
            return;

        bool confirmed = await DisplayAlertAsync(
            "Clear map",
            $"Delete every reading and dead zone for {ssid} on every floor, on this phone and in your account? If you are connected to it, recording on it starts again the next time you join it.",
            "Delete",
            "Cancel");
        if (!confirmed)
            return;

        clearButton.IsEnabled = false;
        try
        {
            await sync.ClearNetworkAsync(ssid);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not clear the map", $"{ApiClient.Describe(ex)} Nothing was deleted.", "OK");
        }
        finally
        {
            clearButton.IsEnabled = true;
        }
    }

    void RefreshStatus()
    {
        signedOut.IsVisible = !account.IsSignedIn;
        UpdateEstimateMarker(Mapper.ResolvePosition());
        ShowWifi(engine.Monitor.Current);
        ShowDetails();
        hereButton.IsEnabled = Mapper.CanAnchor;
        hereButton.Text = hereArmed && Mapper.CanAnchor ? "Tap map" : "Here";
        clearButton.IsEnabled = account.IsSignedIn && SelectedNetwork != null;
        if (!centered && Mapper.LastFix != null)
            CenterMap();
    }

    void ShowWifi(SignalStatus status)
    {
        var wifi = status.Wifi;
        if (!wifi.Enabled)
            SetStatus(engine.Toggler.HoldingWifiOff ? "Wi-Fi is off (weak signal)" : "Wi-Fi is off", SignalScale.Unknown);
        else if (wifi.Rssi is int rssi && wifi.Connected && status.Report.Level != QualityLevel.None)
            SetStatus($"{wifi.Ssid ?? "Name hidden"} · {rssi} dBm · {SignalScale.LabelOf(status.Report.Level)}",
                SignalScale.ColorOf(status.Report.Level));
        else if (Mapper.Recording && Mapper.CurrentSsid is { } ssid)
            SetStatus($"Dropped from {ssid}", SignalScale.Dropped);
        else
            SetStatus("Not connected", SignalScale.Unknown);
    }

    void ShowDetails()
    {
        string where;
        if (!account.IsSignedIn)
            where = "Log in on the Account tab to start mapping.";
        else if (!settings.Mapping)
            where = "Automatic mapping is off in Settings.";
        else if (!engine.LocationGranted)
            where = "Location permission is off, so mapping is paused.";
        else if (!Mapper.Sensing)
            where = "Not on Wi-Fi, so mapping is paused until you join a network.";
        else if (!Mapper.Recording)
            where = (engine.Toggler.HoldingWifiOff
                ? "Wi-Fi is off for now, so nothing is recorded, but your position is still tracked. "
                : "Not on Wi-Fi, so nothing is recorded, but your position is tracked while the map is open. ")
                + Mapper.ResolvePosition().Text;
        else if (hereArmed)
            where = "Tap the map where you are standing.";
        else
            where = Mapper.ResolvePosition().Text;

        string floor = Mapper.BarometerAvailable
            ? FloorLabel(Mapper.CurrentFloor)
            : "no barometer, so every reading counts as one floor";
        string count = SelectedNetwork is { } ssid && grid != null
            ? $"{grid.Cells.Count(c => c.Value.Samples >= MappingEngine.SamplesPerCell)} of {grid.Cells.Count} cells mapped on {ssid}, {floor}."
            : "No map yet. Join a Wi-Fi network to start one.";
        string upload = store.PendingCount > 0 ? $" {store.PendingCount} reading(s) waiting to upload." : "";
        details.Text = $"{where} {Mapper.SampleStatus}\n{count}{upload}".Trim();
    }

    void SetStatus(string text, Color color)
    {
        wifiStatus.Text = text;
        wifiDot.Content = Theme.Dot(color);
    }

    static string FloorLabel(int floor) => floor >= 1 ? $"floor {floor}" : $"basement {1 - floor}";

    static Grid Legend()
    {
        const int columns = 3;
        var legend = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        for (int c = 0; c < columns; c++)
            legend.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int i = 0; i < SignalScale.Legend.Count; i++)
        {
            var (label, color) = SignalScale.Legend[i];
            if (i % columns == 0)
                legend.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            legend.Add(new HorizontalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new BoxView
                    {
                        Color = color,
                        CornerRadius = 3,
                        WidthRequest = 12,
                        HeightRequest = 12,
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label { Text = label, FontSize = 12, TextColor = Theme.SubtleText }
                }
            }, i % columns, i / columns);
        }
        return legend;
    }
}
