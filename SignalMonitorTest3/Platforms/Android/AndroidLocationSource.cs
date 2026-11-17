using Android.Content;
using Android.Locations;
using Android.OS;
using AndroidLocation = Android.Locations.Location;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace SignalMonitorTest3;

public sealed class AndroidLocationSource : Java.Lang.Object, ILocationListener
{
    const long UpdateMillis = 1000;
    const long StaleMillis = 3000;
    static readonly string[] Preferred = { "fused", LocationManager.GpsProvider, LocationManager.NetworkProvider };

    readonly LocationManager manager =
        (LocationManager)global::Android.App.Application.Context.GetSystemService(Context.LocationService)!;
    AndroidLocation? best;

    public event Action<MauiLocation>? Updated;

    public bool IsLocationEnabled => OperatingSystem.IsAndroidVersionAtLeast(28)
        ? manager.IsLocationEnabled
        : manager.IsProviderEnabled(LocationManager.GpsProvider) || manager.IsProviderEnabled(LocationManager.NetworkProvider);

    public bool Start()
    {
        Stop();

        var available = manager.AllProviders ?? new List<string>();
        var providers = Preferred.Where(available.Contains).ToList();
        foreach (var provider in providers)
            manager.RequestLocationUpdates(provider, UpdateMillis, 0, this, Looper.MainLooper);

        foreach (var provider in providers)
            if (manager.GetLastKnownLocation(provider) is { } last)
                Consider(last);

        return providers.Count > 0;
    }

    public void Stop()
    {
        manager.RemoveUpdates(this);
        best = null;
    }

    public void OnLocationChanged(AndroidLocation location) => Consider(location);

    public void OnProviderDisabled(string provider)
    {
    }

    public void OnProviderEnabled(string provider)
    {
    }

    public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
    {
    }

    void Consider(AndroidLocation location)
    {
        if (best != null && !IsBetter(location, best))
            return;

        best = location;
        Updated?.Invoke(ToMaui(location));
    }

    static bool IsBetter(AndroidLocation candidate, AndroidLocation current)
    {
        long newerMillis = (candidate.ElapsedRealtimeNanos - current.ElapsedRealtimeNanos) / 1_000_000;
        if (newerMillis > StaleMillis)
            return true;
        if (newerMillis < -StaleMillis)
            return false;

        float candidateAccuracy = candidate.HasAccuracy ? candidate.Accuracy : float.MaxValue;
        float currentAccuracy = current.HasAccuracy ? current.Accuracy : float.MaxValue;
        if (candidateAccuracy <= currentAccuracy)
            return true;
        return newerMillis > 0 && candidateAccuracy <= currentAccuracy * 1.5f + 5;
    }

    static MauiLocation ToMaui(AndroidLocation location)
    {
        double ageMillis = (SystemClock.ElapsedRealtimeNanos() - location.ElapsedRealtimeNanos) / 1_000_000.0;
        return new MauiLocation(location.Latitude, location.Longitude)
        {
            Accuracy = location.HasAccuracy ? location.Accuracy : null,
            Altitude = location.HasAltitude ? location.Altitude : null,
            Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, ageMillis))
        };
    }
}
