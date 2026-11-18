using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using SignalSwap.Core;

namespace SignalSwap;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class SignalSwapService : Service
{
    const string ChannelId = "signalswap";
    const int NotificationId = 3001;

    static SignalSwapService? running;
    static string statusText = "Starting...";

    PowerManager.WakeLock? wakeLock;

    public static bool IsRunning => running != null;

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(SignalSwapService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(SignalSwapService)));
    }

    public static void UpdateStatus(string text)
    {
        if (text == statusText)
            return;
        statusText = text;
        running?.Publish();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
        else
            StartForeground(NotificationId, notification);

        if (wakeLock == null && GetSystemService(PowerService) is PowerManager power)
        {
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "SignalSwap:Engine");
            wakeLock?.Acquire();
        }

        running = this;
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (!AppServices.Settings.KeepRunning)
        {
            AppServices.Engine.Stop();
            StopSelf();
        }
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (running == this)
            running = null;
        if (wakeLock?.IsHeld == true)
            wakeLock.Release();
        wakeLock = null;
        base.OnDestroy();
    }

    void Publish()
    {
        if (GetSystemService(NotificationService) is NotificationManager manager)
            manager.Notify(NotificationId, BuildNotification());
    }

    Notification BuildNotification()
    {
        var launch = PackageName == null ? null : PackageManager?.GetLaunchIntentForPackage(PackageName);
        var tap = launch == null
            ? null
            : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            if (GetSystemService(NotificationService) is NotificationManager manager)
                manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Signal monitoring", NotificationImportance.Low));
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
            builder = new Notification.Builder(this);
        }

        int icon = Resources?.GetIdentifier("tab_signal", "drawable", PackageName) ?? 0;
        return builder
            .SetContentTitle("SignalSwap")
            .SetContentText(statusText)
            .SetSmallIcon(icon != 0 ? icon : global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(tap)
            .Build()!;
    }
}
