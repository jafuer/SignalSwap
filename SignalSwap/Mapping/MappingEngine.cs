using SignalSwap.Core;

namespace SignalSwap.Mapping;

public enum PositionKind
{
    None,
    Fix,
    Walking
}

public readonly record struct PositionState(PositionKind Kind, Location? Position, string Text);

public sealed class MappingEngine
{
    readonly record struct HeldDrop(ReadingDraft Draft, PositionState Position, double East, double North, double Walked);

    public const int SamplesPerCell = 3;
    public const int DefaultFloor = 1;
    public const double TapAnchorAccuracyMeters = 5;
    const double MaxGpsErrorMeters = 20;
    const double MaxFixAgeSeconds = 6;
    const double MaxWalkingErrorMeters = 15;
    const double OutdoorAccuracyMeters = 6;
    static readonly TimeSpan MinimumJoinGap = TimeSpan.FromSeconds(10);

    readonly AppSettings settings;
    readonly ReadingStore store;
    readonly AndroidLocationSource locationSource = new();
    readonly DeadReckoner walker = new();
    readonly FloorTracker floorTracker = new();
    readonly FixFilter fixFilter = new() { MaxAccuracyMeters = MaxGpsErrorMeters };
    readonly PositionFilter<ReadingDraft> stitcher = new();
    readonly Dictionary<(string Ssid, int Floor), SignalGrid> coverage = new();
    readonly List<HeldDrop> heldDrops = new();

    Location? lastFix;
    DateTimeOffset lastFedFix;
    string? locationProblem;
    string? walkingProblem;
    string? lastSsid;
    string? pausedSsid;
    string? lastBssid;
    string? lastSecurityType;
    DateTimeOffset lastConnectedAt;
    bool initialized;
    bool locationGranted;
    bool walkingAvailable;
    bool barometerAvailable;
    bool declinationSet;
    bool sensing;

    public MappingEngine(AppSettings settings, ReadingStore store)
    {
        this.settings = settings;
        this.store = store;
        locationSource.Updated += OnLocationUpdated;
        walker.Stepped += () => Stepped?.Invoke();
        floorTracker.FloorChanged += _ => FloorChanged?.Invoke();
    }

    public event Action<SignalReading>? ReadingSaved;

    public event Action<string>? NetworkJoined;

    public event Action? FloorChanged;

    public event Action? Stepped;

    public bool Active { get; private set; }

    public bool Sensing => sensing;

    public bool Recording { get; private set; }

    public bool WalkingAvailable => walkingAvailable;

    public bool BarometerAvailable => barometerAvailable;

    public bool CanAnchor => sensing && walkingAvailable;

    public string SampleStatus { get; private set; } = "";

    public string? LocationProblem => locationProblem;

    public string? WalkingProblem => walkingProblem;

    public string? CurrentSsid => lastSsid;

    public string? PausedNetwork => pausedSsid;

    public Location? LastFix => lastFix;

    public int CurrentFloor => barometerAvailable ? floorTracker.Floor : DefaultFloor;

    TimeSpan DropWindow => TimeSpan.FromSeconds(Math.Max(0, settings.DropWindowSeconds));

    double CellSize => settings.CellSize;

    public void Initialize(bool locationGranted, bool activityGranted)
    {
        this.locationGranted = locationGranted;
        locationProblem = locationGranted ? null : "Location permission is off, so positions and Wi-Fi names are unavailable.";

        if (!activityGranted)
        {
            walkingAvailable = false;
            walkingProblem = "Physical activity permission is off, so step tracking is off.";
        }
        else
        {
            walkingAvailable = walker.Start();
            walker.Stop();
            walkingProblem = walkingAvailable ? null : "This phone has no step detector, so step tracking is off.";
        }

        barometerAvailable = floorTracker.Start();
        floorTracker.Stop();
        initialized = true;
    }

    public async Task TickAsync(WifiSnapshot wifi, bool active, bool keepTracking = false)
    {
        if (!initialized)
            return;

        if (active != Active)
        {
            Active = active;
            if (!active)
                await StopSensingAsync();
        }

        if (!active)
        {
            SampleStatus = "";
            Recording = false;
            return;
        }

        TrackConnection(wifi);
        await ResolveHeldDropsAsync(wifi);
        await UpdateSensingAsync(wifi, keepTracking);

        if (sensing && GoodFix(out _) is { } fix && fix.Timestamp != lastFedFix)
        {
            lastFedFix = fix.Timestamp;
            if (walkingAvailable)
                await AnchorAsync(fix, false);
            if (fix.Accuracy is double accuracy && accuracy <= OutdoorAccuracyMeters)
                floorTracker.Rebaseline();
        }

        await SampleAsync(wifi, ResolvePosition());
    }

    public async Task ShutdownAsync()
    {
        Active = false;
        await StopSensingAsync();
    }

    public async Task AnchorAtAsync(double latitude, double longitude)
    {
        var tapped = new Location(latitude, longitude)
        {
            Accuracy = TapAnchorAccuracyMeters,
            Timestamp = DateTimeOffset.UtcNow
        };
        fixFilter.Reset();
        await AnchorAsync(tapped, true);
    }

    public void ResetCoverage() => coverage.Clear();

    public void NetworkCleared(string ssid)
    {
        stitcher.Discard(draft => draft.Ssid == ssid);
        heldDrops.RemoveAll(drop => drop.Draft.Ssid == ssid);
        ResetCoverage(new[] { ssid });
        if (lastSsid == ssid)
            pausedSsid = ssid;
    }

    public void ResetCoverage(IEnumerable<string> networks)
    {
        var names = networks.ToHashSet();
        foreach (var key in coverage.Keys.Where(k => names.Contains(k.Ssid)).ToList())
            coverage.Remove(key);
    }

    public PositionState ResolvePosition()
    {
        var fix = GoodFix(out string fixText);
        var estimate = CurrentEstimate();
        bool lost = false;
        if (estimate?.Accuracy > MaxWalkingErrorMeters)
        {
            estimate = null;
            lost = fix == null;
        }

        if (fix != null)
        {
            var blended = estimate ?? fix;
            string text = estimate != null
                ? $"Location ±{blended.Accuracy:0} m (GPS ±{fix.Accuracy:0} m blended with your steps)."
                : fixText;
            return new(PositionKind.Fix, blended, text);
        }

        if (estimate != null)
        {
            string walking = $"GPS is weak, so the blue dot follows your steps (±{estimate.Accuracy:0} m, {walker.StepLength:0.00} m per step).";
            if (walker.PoseWarning is { } warning)
                walking += " " + warning;
            return new(PositionKind.Walking, estimate, walking);
        }

        string lostText = lost
            ? "Lost track after walking too far without a good fix. Tap Here, then tap the map where you are standing, or wait for GPS."
            : fixText;
        if (!lost && walkingAvailable && sensing)
            lostText += " Or tap the map where you are standing to start following your steps.";
        else if (!walkingAvailable && walkingProblem != null)
            lostText += " " + walkingProblem;
        return new(PositionKind.None, null, lostText);
    }

    void OnLocationUpdated(Location fix)
    {
        lastFix = fix;
        fixFilter.Offer(fix, CurrentEstimate());
    }

    Location? CurrentEstimate() =>
        walkingAvailable ? stitcher.Estimate(walker.East, walker.North, walker.Walked) : null;

    async Task AnchorAsync(Location fix, bool force)
    {
        if (!declinationSet)
        {
            walker.SetDeclination(fix.Latitude, fix.Longitude, fix.Altitude ?? 0);
            declinationSet = true;
        }

        var result = stitcher.Anchor(fix, walker.East, walker.North, walker.Walked, force);
        if (result.StepScale is double scale)
        {
            double before = walker.StepLength;
            walker.Calibrate(scale);
            stitcher.Rescale(walker.StepLength / before);
        }
        await SaveAsync(result.Placed);
    }

    void TrackConnection(WifiSnapshot wifi)
    {
        if (!wifi.Connected || wifi.Ssid == null || wifi.Bssid == null)
            return;

        var gap = DropWindow > MinimumJoinGap ? DropWindow : MinimumJoinGap;
        bool joined = wifi.Ssid != lastSsid || DateTimeOffset.UtcNow - lastConnectedAt > gap;
        lastSsid = wifi.Ssid;
        lastBssid = wifi.Bssid;
        lastSecurityType = wifi.SecurityType;
        lastConnectedAt = DateTimeOffset.UtcNow;

        if (joined && pausedSsid == wifi.Ssid)
            pausedSsid = null;

        if (joined)
            NetworkJoined?.Invoke(wifi.Ssid);
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

    async Task UpdateSensingAsync(WifiSnapshot wifi, bool keepTracking)
    {
        Recording = Attribute(wifi) != null;
        bool shouldSense = keepTracking || Recording;
        if (shouldSense == sensing)
            return;

        if (shouldSense)
        {
            sensing = true;
            StartLocation();
            if (walkingAvailable)
                walker.Start();
            if (barometerAvailable)
                floorTracker.Start();
            return;
        }

        await StopSensingAsync();
    }

    async Task StopSensingAsync()
    {
        bool wasSensing = sensing;
        sensing = false;
        heldDrops.Clear();
        SampleStatus = "";
        if (!wasSensing)
            return;

        await SaveAsync(stitcher.Flush());
        stitcher.Reset();
        fixFilter.Reset();
        lastFix = null;
        locationSource.Stop();
        walker.Stop();
        floorTracker.Stop();
    }

    void StartLocation()
    {
        if (!locationGranted)
            return;

        if (!locationSource.IsLocationEnabled)
        {
            locationProblem = "Location is turned off. Turn it on in Quick Settings.";
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

    async Task SampleAsync(WifiSnapshot wifi, PositionState position)
    {
        if (Attribute(wifi) is not { } network)
        {
            SampleStatus = "";
            return;
        }

        if (network.Ssid == pausedSsid)
        {
            SampleStatus = $"Map cleared. Recording on {network.Ssid} starts again the next time you join it.";
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
            SampleStatus = $"Disconnected from {network.Ssid}. {heldDrops.Count} drop(s) count as a dead zone only if you reconnect to it.";
            return;
        }

        switch (position.Kind)
        {
            case PositionKind.Walking:
                stitcher.Add(draft, walker.East, walker.North, walker.Walked);
                SampleStatus = $"{stitcher.PendingCount} reading(s) will snap into place at the next good fix.";
                break;
            case PositionKind.Fix when position.Position is { } here:
                bool saved = await SaveAsync(draft.Place(here.Latitude, here.Longitude, here.Accuracy, false));
                SampleStatus = saved ? "Mapping this cell." : "This cell is already mapped, skipping.";
                break;
            default:
                SampleStatus = "";
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
        if (store.UserId == null)
            return false;

        var cells = CoverageFor(reading.Ssid, reading.Floor ?? DefaultFloor, reading.Latitude);
        if (cells.Find(reading.Latitude, reading.Longitude) is { Samples: >= SamplesPerCell })
            return false;

        cells.Add(reading);
        await store.AddAsync(reading);
        ReadingSaved?.Invoke(reading);
        return true;
    }

    SignalGrid CoverageFor(string ssid, int floor, double latitude)
    {
        if (coverage.TryGetValue((ssid, floor), out var cells) && cells.CellSize == CellSize)
            return cells;

        cells = new SignalGrid(CellSize, latitude);
        foreach (var reading in store.ForNetwork(ssid).Where(r => (r.Floor ?? DefaultFloor) == floor))
            cells.Add(reading);
        coverage[(ssid, floor)] = cells;
        return cells;
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
            text = sensing ? "Waiting for a location fix..." : "";
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
}
