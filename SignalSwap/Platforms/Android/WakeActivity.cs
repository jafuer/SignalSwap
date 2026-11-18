using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace SignalSwap;

[Activity(Theme = "@android:style/Theme.Translucent.NoTitleBar",
          Exported = false,
          ExcludeFromRecents = true,
          NoHistory = true,
          LaunchMode = LaunchMode.SingleInstance,
          TaskAffinity = "")]
public class WakeActivity : Activity
{
    static WakeActivity? current;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetShowWhenLocked(true);
            SetTurnScreenOn(true);
        }
        else
        {
            Window?.AddFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
        }

        current = this;
    }

    protected override void OnDestroy()
    {
        if (current == this) current = null;
        base.OnDestroy();
    }

    public static void FinishCurrent()
    {
        var activity = current;
        activity?.RunOnUiThread(() => activity.Finish());
    }
}
