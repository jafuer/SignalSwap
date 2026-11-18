using System.Diagnostics;
using Android.Content;
using Android.Net;
using Java.Net;

namespace SignalSwap;

public static class ConnectionProbe
{
    const string Address = "http://connectivitycheck.gstatic.com/generate_204";
    const int TimeoutMillis = 3000;

    public static double? Run()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager)
                return null;
            if (WifiSampler.WifiNetwork(manager) is not { } network)
                return null;

            var watch = Stopwatch.StartNew();
            var connection = (HttpURLConnection)network.OpenConnection(new URL(Address));
            try
            {
                connection.ConnectTimeout = TimeoutMillis;
                connection.ReadTimeout = TimeoutMillis;
                connection.UseCaches = false;
                connection.InstanceFollowRedirects = false;
                connection.SetRequestProperty("Connection", "close");
                int code = (int)connection.ResponseCode;
                watch.Stop();
                return code == 204 ? watch.Elapsed.TotalMilliseconds : null;
            }
            finally
            {
                connection.Disconnect();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
