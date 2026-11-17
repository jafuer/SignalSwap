namespace SignalSwap.Api.Services;

public readonly record struct CellRef(string Ssid, int Floor, int Band, long X, long Y);

public static class CellGrid
{
    public const double SizeMeters = 5;
    public const int DefaultFloor = 1;
    const double MetersPerDegreeLatitude = 111_320;

    public static CellRef Of(string ssid, int floor, double latitude, double longitude)
    {
        int band = (int)Math.Round(latitude);
        return new CellRef(
            ssid,
            floor,
            band,
            (long)Math.Floor(longitude * MetersPerDegreeLongitude(band) / SizeMeters),
            (long)Math.Floor(latitude * MetersPerDegreeLatitude / SizeMeters));
    }

    public static (decimal South, decimal North, decimal West, decimal East) Bounds(CellRef cell)
    {
        double perLongitude = MetersPerDegreeLongitude(cell.Band);
        return (
            (decimal)(cell.Y * SizeMeters / MetersPerDegreeLatitude),
            (decimal)((cell.Y + 1) * SizeMeters / MetersPerDegreeLatitude),
            (decimal)(cell.X * SizeMeters / perLongitude),
            (decimal)((cell.X + 1) * SizeMeters / perLongitude));
    }

    public static (decimal Latitude, decimal Longitude) Center(CellRef cell)
    {
        var (south, north, west, east) = Bounds(cell);
        return (Math.Round((south + north) / 2, 6), Math.Round((west + east) / 2, 6));
    }

    public static decimal Round(double degrees) => Math.Round((decimal)degrees, 6);

    static double MetersPerDegreeLongitude(int band) => MetersPerDegreeLatitude * Math.Cos(band * Math.PI / 180);
}
