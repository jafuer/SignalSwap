using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SignalMonitorTest3.Mapping;
using MauiMap = Microsoft.Maui.Controls.Maps.Map;

namespace SignalMonitorTest3;

public class MainPage : ContentPage
{
    enum PositionKind { None, Fix, Walking }

    readonly record struct PositionState(PositionKind Kind, Location? Position, string Text);

    readonly record struct HeldDrop(ReadingDraft Draft, PositionState Position, double East, double North, double Walked);

    const int SamplesPerCell = 3;
    const double MaxGpsErrorMeters = 20;
    const double MaxFixAgeSeconds = 6;
    const double MaxWalkingErrorMeters = 15;
    const double OutdoorAccuracyMeters = 6;
    const double TapAnchorAccuracyMeters = 5;
    const int DefaultFloor = 1;
    const double EstimateDotMeters = 1.5;
    static readonly Color EstimateColor = Colors.Cyan;
    static readonly TimeSpan DropWindow = TimeSpan.FromMinutes(2);
    static readonly double[] CellSizes = { 3, 5, 10, 20 };

    readonly ReadingStore store = new();
    readonly AndroidLocationSource locationSource = new();
    readonly DeadReckoner walker = new();
    readonly FloorTracker floorTracker = new();
    readonly FixFilter fixFilter = new() { MaxAccuracyMeters = MaxGpsErrorMeters };
    readonly GapStitcher<ReadingDraft> stitcher = new();
    readonly Dictionary<(string Ssid, int Floor), SignalGrid> coverage = new();
    readonly Dictionary<CellKey, Polygon> cellShapes = new();
    readonly List<HeldDrop> heldDrops = new();
    readonly ObservableCollection<string> networks = new();
    readonly Grid root = new();
    readonly Picker networkPicker = new() { Title = "Network" };
    readonly Picker cellPicker = new() { Title = "Cell size" };
    readonly Button hereButton = CompactButton("Here");
    readonly Label wifiStatus = new() { FontSize = 20, FontAttributes = FontAttributes.Bold };
    readonly Label details = new() { FontSize = 13 };
    readonly Label cellInfo = new() { FontSize = 13 };

    MauiMap map;
    IDispatcherTimer? timer;
    SignalGrid? grid;
    Location? lastFix;
    Circle estimateRing = new();
    Circle estimateDot = new();
    Location? shownEstimate;
    double shownAccuracy;
    string? locationProblem;
    string? walkingProblem;
    string? lastSsid;
    string? lastBssid;
    string? lastSecurityType;
    string sampleStatus = "";
    DateTimeOffset lastConnectedAt;
    bool locationGranted;
    bool walkingAvailable;
    bool barometerAvailable;
    bool declinationSet;
    bool hereArmed;
    bool sensing = true;
    bool started;
    bool centered;
    bool ticking;

    public MainPage()
    {
        Title = "Dead zone map";

        networkPicker.ItemsSource = networks;
        cellPicker.ItemsSource = CellSizes.Select(s => $"{s:0} m cells").ToList();
        cellPicker.SelectedIndex = 1;

        networkPicker.SelectedIndexChanged += (_, _) => RebuildMap(true);
        cellPicker.SelectedIndexChanged += (_, _) => OnCellSizeChanged();
        hereButton.Clicked += (_, _) => ToggleHere();
        locationSource.Updated += OnLocationUpdated;
        walker.Stepped += () => UpdateEstimateMarker(ResolvePosition());
        floorTracker.FloorChanged += _ => OnFloorChanged();

        var fitButton = CompactButton("Fit");
        fitButton.Clicked += (_, _) => CenterMap();
        var clearButton = CompactButton("Clear");
        clearButton.Clicked += async (_, _) => await ClearNetworkAsync();

        var panel = new VerticalStackLayout
        {
            Padding = 12,
            Spacing = 8,
            Children =
            {
                wifiStatus,
                details,
                Row(hereButton, fitButton, clearButton),
                Row(networkPicker, cellPicker),
                Legend(),
                cellInfo
            }
        };

        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.Add(panel, 0, 1);

        map = CreateMap();
        AddEstimateMarker();
        root.Add(map, 0, 0);
        Content = root;
        UpdateHereButton();
    }

    string? SelectedNetwork => networkPicker.SelectedItem as string;

    int CurrentFloor => barometerAvailable ? floorTracker.Floor : DefaultFloor;

    double CellSize => CellSizes[Math.Max(0, cellPicker.SelectedIndex)];

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (started)
            return;
        started = true;

        if (Window is { } window)
            window.Destroying += (_, _) => Shutdown();

        await store.LoadAsync();
        foreach (var ssid in store.Networks)
            networks.Add(ssid);
        if (networks.Count > 0)
            networkPicker.SelectedIndex = 0;

        var status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (status == PermissionStatus.Granted)
        {
            locationGranted = true;
            map.IsShowingUser = true;
            StartLocation();
        }
        else
        {
            locationProblem = "Location permission denied. GPS and Wi-Fi names are unavailable.";
        }

        await StartWalkingAsync();
        barometerAvailable = floorTracker.Start();
        if (barometerAvailable && CurrentFloor != DefaultFloor)
            RebuildMap(true);
        UpdateHereButton();
        await StartBackgroundAsync();

        timer = CreateTimer();
        timer.Start();
        await TickAsync();
    }

    void Shutdown()
    {
        timer?.Stop();
        RecordingService.Stop();
        locationSource.Stop();
        walker.Stop();
        floorTracker.Stop();
        _ = SaveAsync(stitcher.Flush());
    }

    MauiMap CreateMap()
    {
        var created = new MauiMap { MapType = MapType.Hybrid, IsShowingUser = locationGranted };
        created.MapClicked += OnMapClicked;
        return created;
    }

    IDispatcherTimer CreateTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += async (_, _) => await TickAsync();
        return t;
    }

    void StartLocation()
    {
        if (!locationSource.IsLocationEnabled)
        {
            locationProblem = "Location is turned off. Turn it on in Quick Settings, then reopen the app.";
            return;
        }

        try
        {
            locationProblem = locationSource.Start() ? null : "This phone has no location providers.";
        }
        catch (Exception ex)
        {
            locationProblem = $"Location unavailable: {ex.Message}";
        }
    }

    async Task StartWalkingAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29) &&
            await Permissions.RequestAsync<ActivityRecognitionPermission>() != PermissionStatus.Granted)
        {
            walkingAvailable = false;
            walkingProblem = "Physical activity permission denied, so step tracking is off.";
            return;
        }

        walkingAvailable = walker.Start();
        walkingProblem = walkingAvailable ? null : "This phone has no step detector, so step tracking is off.";
    }

    async Task StartBackgroundAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        if (!locationGranted)
            return;

        try
        {
            RecordingService.Start();
        }
        catch (Exception ex)
        {
            cellInfo.Text = $"Background mapping unavailable, so mapping pauses when the app is hidden: {ex.Message}";
        }
    }

    void OnLocationUpdated(Location fix)
    {
        lastFix = fix;
        fixFilter.Offer(fix, CurrentEstimate());
    }

    Location? CurrentEstimate() =>
        walkingAvailable ? stitcher.Estimate(walker.East, walker.North, walker.Walked) : null;

    async Task TickAsync()
    {
        if (ticking)
            return;

        ticking = true;
        try
        {
            var wifi = WifiSampler.Read();
            TrackConnection(wifi);
            ShowWifi(wifi);
            await ResolveHeldDropsAsync(wifi);
            await UpdateSensingAsync(wifi);

            var position = ResolvePosition();
            if (position.Kind == PositionKind.Fix && position.Position is { } fix)
            {
                await AnchorAsync(fix);
                if (fix.Accuracy is double accuracy && accuracy <= OutdoorAccuracyMeters)
                    floorTracker.Rebaseline();
            }
            await SampleAsync(wifi, position);

            UpdateEstimateMarker(position);
            if (!centered && lastFix != null)
                CenterMap();
            ShowDetails(position);
        }
        catch (Exception ex)
        {
            details.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ticking = false;
        }
    }

    async Task AnchorAsync(Location fix)
    {
        if (!declinationSet)
        {
            walker.SetDeclination(fix.Latitude, fix.Longitude, fix.Altitude ?? 0);
            declinationSet = true;
        }

        var result = stitcher.Anchor(fix, walker.East, walker.North, walker.Walked);
        if (result.StepScale is double scale)
            walker.Calibrate(scale);
        await SaveAsync(result.Placed);
    }

    void TrackConnection(WifiSnapshot wifi)
    {
        if (!wifi.Connected || wifi.Ssid == null || wifi.Bssid == null)
            return;

        bool joined = wifi.Ssid != lastSsid || DateTimeOffset.UtcNow - lastConnectedAt > DropWindow;
        lastSsid = wifi.Ssid;
        lastBssid = wifi.Bssid;
        lastSecurityType = wifi.SecurityType;
        lastConnectedAt = DateTimeOffset.UtcNow;

        if (!networks.Contains(wifi.Ssid))
            networks.Add(wifi.Ssid);
        if (joined || networkPicker.SelectedIndex < 0)
            networkPicker.SelectedIndex = networks.IndexOf(wifi.Ssid);
    }

    async Task ResolveHeldDropsAsync(WifiSnapshot wifi)
    {
        if (heldDrops.Count == 0)
            return;

        string ssid = heldDrops[0].Draft.Ssid;
        if (wifi.Connected && wifi.Ssid == ssid)
        {
            var confirmed = heldDrops.ToList();
            heldDrops.Clear();
            foreach (var drop in confirmed)
            {
                if (drop.Position.Kind == PositionKind.Walking)
                    stitcher.Add(drop.Draft, drop.East, drop.North, drop.Walked);
                else if (drop.Position.Position is { } here)
                    await SaveAsync(drop.Draft.Place(here.Latitude, here.Longitude, here.Accuracy, false));
            }
            return;
        }

        if (Attribute(wifi) is not { } network || network.Ssid != ssid)
            heldDrops.Clear();
    }

    async Task UpdateSensingAsync(WifiSnapshot wifi)
    {
        bool shouldSense = Attribute(wifi) != null;
        if (shouldSense == sensing)
            return;

        sensing = shouldSense;
        if (sensing)
        {
            if (locationGranted)
                StartLocation();
            if (walkingAvailable)
                walker.Start();
            if (barometerAvailable)
                floorTracker.Start();
            UpdateHereButton();
            return;
        }

        await SaveAsync(stitcher.Flush());
        stitcher.Reset();
        fixFilter.Reset();
        lastFix = null;
        hereArmed = false;
        UpdateHereButton();
        locationSource.Stop();
        walker.Stop();
        floorTracker.Stop();
    }

    (string Ssid, string Bssid, string? SecurityType)? Attribute(WifiSnapshot wifi)
    {
        if (!wifi.Enabled)
            return null;

        if (wifi.Connected)
        {
            if (wifi.Ssid is { } ssid && wifi.Bssid is { } bssid)
                return (ssid, bssid, wifi.SecurityType);
            return null;
        }

        if (lastSsid != null && lastBssid != null && DateTimeOffset.UtcNow - lastConnectedAt <= DropWindow)
            return (lastSsid, lastBssid, lastSecurityType);
        return null;
    }

    void ShowWifi(WifiSnapshot wifi)
    {
        if (!wifi.Enabled)
        {
            SetStatus("Wi-Fi is off", Colors.Gray);
        }
        else if (wifi.Rssi is int rssi)
        {
            var (label, color) = SignalScale.Rate(rssi);
            SetStatus($"{rssi} dBm, {label}, {wifi.Ssid ?? "name hidden"}", color);
        }
        else if (Attribute(wifi) is { } network)
        {
            SetStatus($"Dropped from {network.Ssid}", SignalScale.DroppedColor);
        }
        else
        {
            SetStatus("Not connected", Colors.Gray);
        }
    }

    async Task SampleAsync(WifiSnapshot wifi, PositionState position)
    {
        if (Attribute(wifi) is not { } network)
        {
            sampleStatus = "";
            return;
        }

        var draft = new ReadingDraft(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            network.Ssid,
            network.Bssid,
            network.SecurityType,
            wifi.Rssi,
            wifi.FrequencyMhz,
            wifi.LinkSpeedMbps,
            CurrentFloor);

        if (!wifi.Connected)
        {
            if (position.Kind != PositionKind.None)
                heldDrops.Add(new HeldDrop(draft, position, walker.East, walker.North, walker.Walked));
            sampleStatus = $"Disconnected from {network.Ssid}. {heldDrops.Count} drop(s) count as a dead zone only if you reconnect to it.";
            return;
        }

        switch (position.Kind)
        {
            case PositionKind.Walking:
                stitcher.Add(draft, walker.East, walker.North, walker.Walked);
                sampleStatus = $"{stitcher.PendingCount} reading(s) will snap into place at the next good fix.";
                break;
            case PositionKind.Fix when position.Position is { } here:
                bool saved = await SaveAsync(draft.Place(here.Latitude, here.Longitude, here.Accuracy, false));
                sampleStatus = saved ? "Mapping this cell." : "This cell is already mapped, skipping.";
                break;
            default:
                sampleStatus = "";
                break;
        }
    }

    async Task SaveAsync(IReadOnlyList<Placed<ReadingDraft>> placed)
    {
        foreach (var p in placed)
            await SaveAsync(p.Item.Place(p.Latitude, p.Longitude, p.AccuracyMeters, false));
    }

    async Task<bool> SaveAsync(SignalReading reading)
    {
        var cells = CoverageFor(reading.Ssid, reading.Floor ?? DefaultFloor, reading.Latitude);
        if (cells.Find(reading.Latitude, reading.Longitude) is { Samples: >= SamplesPerCell })
            return false;

        cells.Add(reading);
        await store.AddAsync(reading);
        OnReadingAdded(reading);
        return true;
    }

    SignalGrid CoverageFor(string ssid, int floor, double latitude)
    {
        if (coverage.TryGetValue((ssid, floor), out var cells))
            return cells;

        cells = new SignalGrid(CellSize, latitude);
        foreach (var reading in store.ForNetwork(ssid).Where(r => (r.Floor ?? DefaultFloor) == floor))
            cells.Add(reading);
        coverage[(ssid, floor)] = cells;
        return cells;
    }

    PositionState ResolvePosition()
    {
        var fix = GoodFix(out string fixText);
        var estimate = CurrentEstimate();
        bool lost = false;
        if (estimate?.Accuracy > MaxWalkingErrorMeters)
        {
            estimate = null;
            lost = true;
        }

        if (fix != null && (estimate == null || (fix.Accuracy ?? double.MaxValue) <= (estimate.Accuracy ?? double.MaxValue)))
            return new(PositionKind.Fix, fix, fixText);

        if (estimate != null)
        {
            string walking = $"GPS is weak, so the cyan dot follows your steps (±{estimate.Accuracy:0} m, {walker.StepLength:0.00} m per step).";
            if (walker.PoseWarning is { } warning)
                walking += " " + warning;
            return new(PositionKind.Walking, estimate, walking);
        }

        string text = lost
            ? "Lost track after walking too far without a good fix. Tap the map where you are standing, or wait for GPS."
            : fixText;
        if (!lost && walkingAvailable)
            text += " Or tap the map where you are standing to start following your steps.";
        else if (!walkingAvailable && walkingProblem != null)
            text += " " + walkingProblem;
        return new(PositionKind.None, null, text);
    }

    Location? GoodFix(out string text)
    {
        if (locationProblem != null)
        {
            text = locationProblem;
            return null;
        }

        if (fixFilter.Trusted is { } trusted && Age(trusted) <= MaxFixAgeSeconds)
        {
            text = $"Location ±{trusted.Accuracy:0} m.";
            return trusted;
        }

        if (lastFix is not { } raw)
        {
            text = "Waiting for a location fix...";
            return null;
        }

        double age = Age(raw);
        if (age > MaxFixAgeSeconds)
            text = $"Location fix is {age:0} s old.";
        else if (raw.Accuracy is not double accuracy)
            text = "Location accuracy unknown.";
        else if (accuracy > MaxGpsErrorMeters)
            text = $"Location ±{accuracy:0} m is too rough to map.";
        else
            text = $"Checking location ±{accuracy:0} m ({fixFilter.Streak} of {fixFilter.Needed} agreeing fixes).";
        return null;
    }

    static double Age(Location fix) => (DateTimeOffset.UtcNow - fix.Timestamp).TotalSeconds;

    List<SignalReading> ReadingsOnFloor() => SelectedNetwork is { } ssid
        ? store.ForNetwork(ssid).Where(r => (r.Floor ?? DefaultFloor) == CurrentFloor).ToList()
        : new List<SignalReading>();

    void OnReadingAdded(SignalReading reading)
    {
        if (!networks.Contains(reading.Ssid))
            networks.Add(reading.Ssid);

        if (SelectedNetwork != reading.Ssid)
        {
            networkPicker.SelectedIndex = networks.IndexOf(reading.Ssid);
            return;
        }

        if ((reading.Floor ?? DefaultFloor) != CurrentFloor)
            return;

        if (grid == null || grid.Cells.Count == 0)
            grid = new SignalGrid(CellSize, reading.Latitude);

        DrawCell(grid.Add(reading));
    }

    void RebuildMap(bool recenter)
    {
        var readings = ReadingsOnFloor();
        double referenceLatitude = readings.Count > 0 ? readings[0].Latitude : lastFix?.Latitude ?? 0;

        grid = new SignalGrid(CellSize, referenceLatitude);
        foreach (var reading in readings)
            grid.Add(reading);

        var region = map.VisibleRegion;
        var estimate = shownEstimate;
        var accuracy = shownAccuracy;
        map.MapClicked -= OnMapClicked;
        root.Remove(map);

        cellShapes.Clear();
        map = CreateMap();
        foreach (var stats in grid.Cells.Values)
            DrawCell(stats);
        AddEstimateMarker();
        if (estimate != null)
            ShowEstimate(estimate, accuracy);
        root.Add(map, 0, 0);

        cellInfo.Text = "";
        if (recenter || region == null)
            CenterMap();
        else
            map.MoveToRegion(region);
    }

    void DrawCell(CellStats stats)
    {
        if (grid == null)
            return;

        var fill = stats.Color.WithAlpha(0.45f);
        var stroke = stats.Color.WithAlpha(0.9f);

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

        if (lastFix is { } here)
            MoveTo(here, 60);
    }

    void MoveTo(Location center, double radiusMeters)
    {
        map.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromMeters(radiusMeters)));
        centered = true;
    }

    async void OnMapClicked(object? sender, MapClickedEventArgs e)
    {
        if (sensing && walkingAvailable && (hereArmed || ResolvePosition().Kind == PositionKind.None))
        {
            hereArmed = false;
            UpdateHereButton();
            var tapped = new Location(e.Location.Latitude, e.Location.Longitude)
            {
                Accuracy = TapAnchorAccuracyMeters,
                Timestamp = DateTimeOffset.UtcNow
            };
            fixFilter.Reset();
            await AnchorAsync(tapped);

            var position = ResolvePosition();
            UpdateEstimateMarker(position);
            ShowDetails(position);
            return;
        }

        ShowCellInfo(e.Location);
    }

    void ToggleHere()
    {
        if (!hereButton.IsEnabled)
            return;
        hereArmed = !hereArmed;
        UpdateHereButton();
        ShowDetails();
    }

    void UpdateHereButton()
    {
        hereButton.IsEnabled = walkingAvailable && sensing;
        if (!hereButton.IsEnabled)
            hereArmed = false;
        hereButton.Text = hereArmed ? "Tap map" : "Here";
    }

    void OnFloorChanged()
    {
        RebuildMap(false);
        ShowDetails();
    }

    void OnCellSizeChanged()
    {
        coverage.Clear();
        RebuildMap(false);
    }

    void AddEstimateMarker()
    {
        estimateRing = new Circle { StrokeWidth = 1 };
        estimateDot = new Circle { StrokeWidth = 2, Radius = Distance.FromMeters(EstimateDotMeters) };
        shownEstimate = null;
        HideEstimate();
        map.MapElements.Add(estimateRing);
        map.MapElements.Add(estimateDot);
    }

    void UpdateEstimateMarker(PositionState position)
    {
        if (position.Kind != PositionKind.Walking || position.Position is not { } here)
        {
            if (shownEstimate != null)
            {
                shownEstimate = null;
                HideEstimate();
            }
            return;
        }

        double accuracy = here.Accuracy ?? TapAnchorAccuracyMeters;
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
            cellInfo.Text = "No readings in this cell yet.";
            return;
        }

        string signal = cell.Average is double avg
            ? $"Average {avg:0} dBm ({SignalScale.Rate((int)Math.Round(avg)).Label}), best {cell.Max}, worst {cell.Min}."
            : "No signal recorded here.";
        string state = cell.Samples >= SamplesPerCell ? "Mapped." : $"Needs {SamplesPerCell - cell.Samples} more sample(s).";
        cellInfo.Text = $"{signal} {cell.Samples} samples, {cell.Drops} drops, {cell.AccessPoints.Count} access point(s). {state}";
    }

    async Task ClearNetworkAsync()
    {
        if (SelectedNetwork is not { } ssid)
            return;

        bool confirmed = await DisplayAlertAsync(
            "Clear readings",
            $"Delete every reading recorded for {ssid} on every floor?",
            "Delete",
            "Cancel");
        if (!confirmed)
            return;

        await store.RemoveNetworkAsync(ssid);
        foreach (var key in coverage.Keys.Where(k => k.Ssid == ssid).ToList())
            coverage.Remove(key);
        RebuildMap(false);
        ShowDetails();
    }

    void ShowDetails() => ShowDetails(ResolvePosition());

    void ShowDetails(PositionState position)
    {
        string where = !sensing
            ? "Not connected to Wi-Fi, so mapping is paused until you join a network."
            : hereArmed ? "Tap the map where you are standing." : position.Text;
        string floor = barometerAvailable ? FloorLabel(CurrentFloor) : "no barometer, so every reading counts as one floor";
        string count = SelectedNetwork is { } ssid && grid != null
            ? $"{grid.Cells.Count(c => c.Value.Samples >= SamplesPerCell)} of {grid.Cells.Count} cells mapped on {ssid}, {floor}."
            : "Join a network to start mapping.";
        details.Text = $"{where} {sampleStatus}\n{count}";
    }

    void SetStatus(string text, Color color)
    {
        wifiStatus.Text = text;
        wifiStatus.TextColor = color;
    }

    static string FloorLabel(int floor) => floor >= 1 ? $"Floor {floor}" : $"Basement {1 - floor}";

    static Button CompactButton(string text) => new() { Text = text, Padding = new Thickness(4, 10) };

    static Grid Row(params View[] views)
    {
        var row = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < views.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.Add(views[i], i, 0);
        }
        return row;
    }

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
                        BackgroundColor = Colors.Transparent,
                        WidthRequest = 12,
                        HeightRequest = 12,
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label { Text = label, FontSize = 12 }
                }
            }, i % columns, i / columns);
        }
        return legend;
    }
}
