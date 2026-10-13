using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;
using NodeInfo = Android.Views.Accessibility.AccessibilityNodeInfo;
using A11yAction = Android.Views.Accessibility.Action;

namespace SignalMonitorTest1;

[Service(Label = "Signal Monitor Wi-Fi test",
         Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
         Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/wifi_toggle_accessibility_config")]
public class WifiToggleAccessibilityService : AccessibilityService
{
    const string LogTag = "WifiToggle";

    static readonly string[] ExcludedWords = { "hotspot", "calling", "direct", "sharing", "tethering" };

    static readonly SemaphoreSlim Busy = new(1, 1);

    public static WifiToggleAccessibilityService? Instance { get; private set; }

    public static event System.Action<string>? LogMessage;

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
        Write("Accessibility service connected.");
    }

    public override bool OnUnbind(Intent? intent)
    {
        Instance = null;
        Write("Accessibility service disconnected.");
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Instance = null;
        base.OnDestroy();
    }

    public override void OnAccessibilityEvent(Android.Views.Accessibility.AccessibilityEvent? e) { }
    public override void OnInterrupt() { }

    public static bool IsWifiEnabled()
    {
        var wifi = global::Android.App.Application.Context.GetSystemService(Context.WifiService) as WifiManager;
        return wifi?.IsWifiEnabled ?? false;
    }

    public Task<bool> ToggleWifiAsync() => SetWifiAsync(!IsWifiEnabled());

    public void ToggleAfterDelay(TimeSpan delay)
    {
        var power = GetSystemService(PowerService) as PowerManager;
        var wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "SignalMonitor:WifiTest");
        wakeLock?.Acquire((long)(delay + TimeSpan.FromSeconds(30)).TotalMilliseconds);

        Write($"Toggling Wi-Fi in {delay.TotalSeconds:0} s. Turn the screen off now.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await ToggleWifiAsync();
            }
            finally
            {
                if (wakeLock?.IsHeld == true) wakeLock.Release();
            }
        });
    }

    public async Task<bool> SetWifiAsync(bool enable)
    {
        if (!await Busy.WaitAsync(0))
        {
            Write("A toggle is already running, ignoring this one.");
            return false;
        }

        bool wokeScreen = false;
        bool lockedWhenWoken = false;
        string target = enable ? "on" : "off";
        try
        {
            if (IsWifiEnabled() == enable)
            {
                Write($"Wi-Fi is already {target}.");
                return true;
            }

            Write($"Turning Wi-Fi {target}...");

            if (!IsScreenOn())
            {
                lockedWhenWoken = IsKeyguardLocked();
                wokeScreen = await WakeScreenAsync();
                if (!wokeScreen)
                {
                    Write("FAILED: could not wake the screen.");
                    return false;
                }
            }

            bool tapped = await TapWifiControlAsync(enable);
            bool ok = await WaitForWifiStateAsync(enable, TimeSpan.FromSeconds(5));

            if (ok) Write($"SUCCESS: Wi-Fi is now {target}.");
            else if (tapped) Write("FAILED: tapped something, but Wi-Fi did not change.");
            else Write("FAILED: could not find the Wi-Fi control.");
            return ok;
        }
        catch (Exception ex)
        {
            Write($"ERROR: {ex}");
            return false;
        }
        finally
        {
            CloseShade();
            if (wokeScreen)
            {
                await Task.Delay(400);
                WakeActivity.FinishCurrent();
                await Task.Delay(300);
                if (lockedWhenWoken && !IsKeyguardLocked())
                {
                    Write("You unlocked the phone, leaving the screen on.");
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    PerformGlobalAction(GlobalAction.LockScreen);
                    Write("Screen locked again.");
                }
            }
            Busy.Release();
        }
    }

    bool IsKeyguardLocked() => (GetSystemService(KeyguardService) as KeyguardManager)?.IsKeyguardLocked ?? true;

    bool IsScreenOn() => (GetSystemService(PowerService) as PowerManager)?.IsInteractive ?? true;

    async Task<bool> WakeScreenAsync()
    {
        Write("Screen is off, waking it...");
        var intent = new Intent(this, typeof(WakeActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoAnimation | ActivityFlags.ExcludeFromRecents);
        StartActivity(intent);

        for (int i = 0; i < 30; i++)
        {
            if (IsScreenOn())
            {
                await Task.Delay(300);
                WakeActivity.FinishCurrent();
                await Task.Delay(700);
                Write(IsScreenOn() ? "Screen is on, wake activity closed." : "Screen turned off again after closing the wake activity.");
                return IsScreenOn();
            }
            await Task.Delay(100);
        }
        return false;
    }

    async Task<bool> TapWifiControlAsync(bool enable)
    {
        if (!PerformGlobalAction(GlobalAction.QuickSettings))
        {
            Write("Could not open Quick Settings.");
            return false;
        }

        NodeInfo? tile = null;
        bool isInternetTile = false;
        for (int attempt = 0; attempt < 4 && tile == null; attempt++)
        {
            await Task.Delay(500);
            (tile, isInternetTile) = FindWifiTile();
        }

        if (tile == null)
        {
            Write("No Wi-Fi tile found. Labels visible on screen:");
            DumpVisibleLabels();
            return false;
        }

        Write($"Tapping \"{Describe(tile)}\"" + (isInternetTile ? " (Internet tile, expecting a panel)" : ""));
        if (!Click(tile))
        {
            Write("Tap failed.");
            return false;
        }

        if (!isInternetTile && await WaitForWifiStateAsync(enable, TimeSpan.FromSeconds(3)))
            return true;

        await Task.Delay(800);
        var wifiSwitch = FindWifiSwitch();
        if (wifiSwitch == null)
        {
            Write("The tap opened something, but no Wi-Fi switch was found. Labels visible on screen:");
            DumpVisibleLabels();
            return false;
        }

        Write($"Tapping switch \"{Describe(wifiSwitch)}\"");
        bool clicked = Click(wifiSwitch);
        await Task.Delay(500);
        PerformGlobalAction(GlobalAction.Back);
        return clicked;
    }

    (NodeInfo? node, bool isInternetTile) FindWifiTile()
    {
        NodeInfo? internetTile = null;
        foreach (var node in AllNodes())
        {
            foreach (var raw in new[] { node.Text, node.ContentDescription })
            {
                var label = Normalize(raw);
                if (label.Length == 0 || ExcludedWords.Any(w => label.Contains(w))) continue;

                if (label.StartsWith("internet"))
                {
                    internetTile ??= ClickableAncestor(node);
                }
                else if (label.StartsWith("wifi") || label.StartsWith("wlan"))
                {
                    var tile = TileContainer(node);
                    var toggle = PickToggle(tile);
                    if (toggle != null)
                    {
                        LogTileParts(tile, toggle);
                        return (toggle, false);
                    }
                }
            }
        }
        return (internetTile, internetTile != null);
    }

    static NodeInfo TileContainer(NodeInfo node)
    {
        var best = node;
        var current = node.Parent;
        for (int level = 0; level < 4 && current != null; level++, current = current.Parent)
        {
            if (Descendants(current).Count(n => n.Clickable) > 3) break;
            best = current;
        }
        return best;
    }

    static NodeInfo? PickToggle(NodeInfo tile)
    {
        var parts = ClickableParts(tile);
        if (parts.Count == 0) return ClickableAncestor(tile);
        return parts
            .OrderBy(n => BoundsOf(n).Left)
            .ThenBy(n => BoundsOf(n).Width())
            .First();
    }

    static List<NodeInfo> ClickableParts(NodeInfo tile) =>
        Descendants(tile)
            .Where(n => n.Clickable && !Descendants(n).Skip(1).Any(d => d.Clickable))
            .ToList();

    static void LogTileParts(NodeInfo tile, NodeInfo chosen)
    {
        Write("Wi-Fi tile parts:");
        foreach (var part in ClickableParts(tile))
        {
            var b = BoundsOf(part);
            var mark = part.Equals(chosen) ? "-> " : "   ";
            Write($"{mark}\"{Describe(part)}\" {part.ClassName?.Split('.').Last()} x {b.Left}-{b.Right}{(part.Checkable ? " checkable" : "")}");
        }
    }

    static IEnumerable<NodeInfo> Descendants(NodeInfo root, int maxDepth = 5)
    {
        var stack = new Stack<(NodeInfo node, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            yield return node;
            if (depth >= maxDepth) continue;
            for (int i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is { } child) stack.Push((child, depth + 1));
        }
    }

    static global::Android.Graphics.Rect BoundsOf(NodeInfo node)
    {
        var bounds = new global::Android.Graphics.Rect();
        node.GetBoundsInScreen(bounds);
        return bounds;
    }

    NodeInfo? FindWifiSwitch()
    {
        foreach (var node in AllNodes())
        {
            bool isSwitch = node.Checkable || (node.ClassName?.Contains("Switch") ?? false);
            if (!isSwitch) continue;

            var scope = node;
            for (int level = 0; level < 3 && scope != null; level++, scope = scope.Parent)
            {
                var text = Normalize(SubtreeText(scope, 3));
                if (text.Contains("mobiledata") || ExcludedWords.Any(w => text.Contains(w))) break;
                if (text.Contains("wifi") || text.Contains("wlan")) return node;
            }
        }
        return null;
    }

    IEnumerable<NodeInfo> AllNodes()
    {
        var roots = new List<NodeInfo>();
        var windows = Windows;
        if (windows != null)
            foreach (var window in windows)
                if (window.Root is { } root) roots.Add(root);
        if (roots.Count == 0 && RootInActiveWindow is { } active) roots.Add(active);

        var queue = new Queue<NodeInfo>(roots);
        int visited = 0;
        while (queue.Count > 0 && visited++ < 3000)
        {
            var node = queue.Dequeue();
            yield return node;
            for (int i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is { } child) queue.Enqueue(child);
        }
    }

    bool Click(NodeInfo node)
    {
        if (node.PerformAction(A11yAction.Click)) return true;

        var bounds = new global::Android.Graphics.Rect();
        node.GetBoundsInScreen(bounds);
        return Tap(bounds.CenterX(), bounds.CenterY());
    }

    bool Tap(float x, float y)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(24)) return false;
        var path = new global::Android.Graphics.Path();
        path.MoveTo(x, y);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, 60))
            .Build();
        return DispatchGesture(gesture, null, null);
    }

    void CloseShade()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            PerformGlobalAction(GlobalAction.DismissNotificationShade);
        else
            PerformGlobalAction(GlobalAction.Back);
    }

    static async Task<bool> WaitForWifiStateAsync(bool enabled, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (IsWifiEnabled() == enabled) return true;
            await Task.Delay(200);
        }
        return IsWifiEnabled() == enabled;
    }

    static NodeInfo? ClickableAncestor(NodeInfo node)
    {
        var current = node;
        for (int i = 0; i < 6 && current != null; i++, current = current.Parent)
            if (current.Clickable) return current;
        return null;
    }

    static string SubtreeText(NodeInfo node, int depth)
    {
        var text = LabelOf(node);
        if (depth > 0)
            for (int i = 0; i < node.ChildCount; i++)
                if (node.GetChild(i) is { } child) text += " " + SubtreeText(child, depth - 1);
        return text;
    }

    static string LabelOf(NodeInfo node) => $"{node.Text} {node.ContentDescription}".Trim();

    static string Normalize(string? s) =>
        new string((s ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());

    static string Describe(NodeInfo node)
    {
        var label = LabelOf(node);
        if (label.Length == 0) label = node.ClassName ?? "unlabeled";
        return label.Length > 60 ? label[..60] + "…" : label;
    }

    void DumpVisibleLabels()
    {
        foreach (var label in AllNodes().Select(LabelOf).Where(l => l.Length > 0).Distinct().Take(40))
            Write("  • " + (label.Length > 60 ? label[..60] + "…" : label));
    }

    static void Write(string message)
    {
        Log.Info(LogTag, message);
        LogMessage?.Invoke(message);
    }
}