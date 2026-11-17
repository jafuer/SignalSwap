using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace SignalMonitorTest3;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class RecordingService : Service
{
    const string ChannelId = "recording";
    const int NotificationId = 3001;

    PowerManager.WakeLock? wakeLock;

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(RecordingService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(RecordingService)));
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
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "SignalMonitorTest3:Recording");
            wakeLock?.Acquire();
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (wakeLock?.IsHeld == true)
            wakeLock.Release();
        wakeLock = null;
        base.OnDestroy();
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
                manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low));
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
            builder = new Notification.Builder(this);
        }

        return builder
            .SetContentTitle("Mapping dead zones")
            .SetContentText("Sampling Wi-Fi and following your position.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetContentIntent(tap)
            .Build()!;
    }
}
