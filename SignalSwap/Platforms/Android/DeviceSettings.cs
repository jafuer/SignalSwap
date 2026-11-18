using Android.Content;
using Android.OS;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

namespace SignalSwap;

public static class DeviceSettings
{
    static Context Context => global::Android.App.Application.Context;

    public static bool AccessibilityServiceOn => WifiToggleAccessibilityService.Instance != null;

    public static bool IgnoringBatteryOptimizations =>
        Context.GetSystemService(Context.PowerService) is PowerManager power &&
        power.IsIgnoringBatteryOptimizations(Context.PackageName);

    public static void OpenAccessibility() => Launch(new Intent(AndroidSettings.ActionAccessibilitySettings));

    public static void RequestBatteryExemption() =>
        Launch(new Intent(AndroidSettings.ActionRequestIgnoreBatteryOptimizations, AndroidUri.Parse("package:" + Context.PackageName)));

    public static void OpenAppDetails() =>
        Launch(new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + Context.PackageName)));

    static void Launch(Intent intent)
    {
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
            {
                activity.StartActivity(intent);
                return;
            }
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            OpenFallback();
        }
    }

    static void OpenFallback()
    {
        var intent = new Intent(AndroidSettings.ActionSettings);
        intent.AddFlags(ActivityFlags.NewTask);
        Context.StartActivity(intent);
    }
}
