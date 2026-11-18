using Android.Content;
using Android.OS;
using SignalSwap.Core;

namespace SignalSwap;

public sealed class AccessibilityWifiSwitch : IWifiSwitch
{
    public bool IsAvailable => WifiToggleAccessibilityService.Instance != null;

    public bool IsWifiEnabled => WifiToggleAccessibilityService.IsWifiEnabled();

    public bool IsScreenOn =>
        (global::Android.App.Application.Context.GetSystemService(Context.PowerService) as PowerManager)?.IsInteractive ?? true;

    public Task<bool> SetWifiAsync(bool enabled) =>
        WifiToggleAccessibilityService.Instance is { } service
            ? Task.Run(() => service.SetWifiAsync(enabled))
            : Task.FromResult(false);
}
