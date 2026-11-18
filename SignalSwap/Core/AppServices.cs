using SignalSwap.Accounts;
using SignalSwap.Mapping;

namespace SignalSwap.Core;

public static class AppServices
{
    public static AppSettings Settings { get; } = new();

    public static ApiClient Api { get; } = new(Settings);

    public static AccountService Account { get; } = new(Api);

    public static ReadingStore Store { get; } = new();

    public static SyncService Sync { get; } = new(Api, Account, Store);

    public static SignalEngine Engine { get; } = new(Settings, Account, Store, Sync);
}

public static class Startup
{
    public static async Task RunAsync()
    {
        var location = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        bool activity = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            activity = await Permissions.RequestAsync<ActivityRecognitionPermission>() == PermissionStatus.Granted;

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();

        await AppServices.Engine.StartAsync(location == PermissionStatus.Granted, activity);
    }
}
