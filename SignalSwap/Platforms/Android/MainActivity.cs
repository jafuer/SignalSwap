using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace SignalSwap;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Window is not { } window)
            return;

        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            window.SetStatusBarColor(global::Android.Graphics.Color.White);
            window.SetNavigationBarColor(global::Android.Graphics.Color.White);
        }

        var bars = WindowCompat.GetInsetsController(window, window.DecorView);
        bars.AppearanceLightStatusBars = true;
        bars.AppearanceLightNavigationBars = true;
    }
}
