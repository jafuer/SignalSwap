using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using SignalSwap.Core;

namespace SignalSwap;

public static class WifiSampler
{
    static readonly Dictionary<string, string> SecurityByBssid = new(StringComparer.OrdinalIgnoreCase);

    public static WifiSnapshot Read()
    {
        var context = global::Android.App.Application.Context;
        var wifi = context.GetSystemService(Context.WifiService) as WifiManager;
        if (wifi == null || !wifi.IsWifiEnabled)
            return WifiSnapshot.Off;

        var info = wifi.ConnectionInfo;
        if (info == null || info.Rssi <= -127)
            return WifiSnapshot.Disconnected;

        var ssid = info.SSID?.Trim('"');
        if (string.IsNullOrEmpty(ssid) || ssid == "<unknown ssid>")
            ssid = null;

        var bssid = info.BSSID;
        if (string.IsNullOrEmpty(bssid) || bssid == "02:00:00:00:00:00")
            bssid = null;

        int? rx = null;
        int? tx = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            rx = Positive(info.RxLinkSpeedMbps);
            tx = Positive(info.TxLinkSpeedMbps);
        }

        int? generation = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            generation = Generation((int)info.WifiStandard);

        return new WifiSnapshot(
            true,
            true,
            ssid,
            bssid,
            SecurityOf(wifi, bssid),
            info.Rssi,
            Positive(info.Frequency),
            Positive(info.LinkSpeed),
            rx,
            tx,
            generation,
            IsValidated(context));
    }

    public static Network? WifiNetwork(ConnectivityManager manager)
    {
        foreach (var network in manager.GetAllNetworks())
        {
            var capabilities = manager.GetNetworkCapabilities(network);
            if (capabilities != null && capabilities.HasTransport(TransportType.Wifi))
                return network;
        }
        return null;
    }

    static bool? IsValidated(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager)
                return null;
            if (WifiNetwork(manager) is not { } network)
                return null;
            return manager.GetNetworkCapabilities(network)?.HasCapability(NetCapability.Validated);
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    static int? Positive(int value) => value > 0 ? value : null;

    static int? Generation(int standard) => standard switch
    {
        4 => 4,
        5 => 5,
        6 => 6,
        8 => 7,
        _ => null
    };

    static string? SecurityOf(WifiManager wifi, string? bssid)
    {
        if (bssid == null)
            return null;
        if (SecurityByBssid.TryGetValue(bssid, out var cached))
            return cached;

        try
        {
            var match = wifi.ScanResults?.FirstOrDefault(r => string.Equals(r.Bssid, bssid, StringComparison.OrdinalIgnoreCase));
            if (match?.Capabilities is not { } capabilities)
                return null;

            var type = Classify(capabilities);
            SecurityByBssid[bssid] = type;
            return type;
        }
        catch (Java.Lang.SecurityException)
        {
            return null;
        }
    }

    static string Classify(string capabilities)
    {
        var caps = capabilities.ToUpperInvariant();
        bool sae = caps.Contains("SAE");
        bool psk = caps.Contains("PSK");

        if (caps.Contains("EAP")) return "WPA-Enterprise";
        if (sae && psk) return "WPA2/WPA3";
        if (sae) return "WPA3";
        if (psk) return caps.Contains("RSN") || caps.Contains("WPA2") ? "WPA2" : "WPA";
        if (caps.Contains("OWE")) return "OWE";
        if (caps.Contains("WEP")) return "WEP";
        return "Open";
    }
}
