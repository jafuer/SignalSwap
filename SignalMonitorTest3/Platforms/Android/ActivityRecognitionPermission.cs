namespace SignalMonitorTest3;

public sealed class ActivityRecognitionPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[] { ("android.permission.ACTIVITY_RECOGNITION", true) };
}
