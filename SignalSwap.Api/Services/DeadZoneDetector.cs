using System.Data;
using Dapper;

namespace SignalSwap.Api.Services;

public sealed class DeadZoneDetector
{
    public const int MinSamples = 3;
    const double MaxDropShare = 0.5;
    const int UnusableBelowDbm = -82;

    const string SamplesSql = """
        SELECT r.wifi_network_id AS NetworkId, r.signal_strength AS SignalStrength
        FROM dbo.signal_reading r
        WHERE r.recorded_by = @UserId
          AND r.ssid = @Ssid
          AND ISNULL(r.floor, @DefaultFloor) = @Floor
          AND r.latitude >= @South AND r.latitude < @North
          AND r.longitude >= @West AND r.longitude < @East
        """;

    const string ExistingSql = """
        SELECT TOP 1 dz.id AS Id, dz.resolved_at AS ResolvedAt
        FROM dbo.dead_zone dz
        WHERE dz.reported_by = @UserId
          AND dz.source = 'auto'
          AND dz.ssid = @Ssid
          AND ISNULL(dz.floor, @DefaultFloor) = @Floor
          AND dz.latitude = @Latitude
          AND dz.longitude = @Longitude
        """;

    const string InsertSql = """
        INSERT INTO dbo.dead_zone (latitude, longitude, carrier, notes, reported_by, reported_at, wifi_network_id, ssid, floor, resolved_at, source)
        VALUES (@Latitude, @Longitude, NULL, @Notes, @UserId, SYSUTCDATETIME(), @NetworkId, @Ssid, @Floor, NULL, 'auto')
        """;

    const string ReopenSql = """
        UPDATE dbo.dead_zone
        SET resolved_at = NULL, notes = @Notes, wifi_network_id = @NetworkId
        WHERE id = @Id
        """;

    const string ResolveSql = """
        UPDATE dbo.dead_zone
        SET resolved_at = SYSUTCDATETIME(), notes = @Notes
        WHERE id = @Id
        """;

    public async Task<int> UpdateAsync(IDbConnection connection, IDbTransaction transaction, int userId, IEnumerable<CellRef> cells)
    {
        int changed = 0;
        foreach (var cell in cells)
        {
            var (south, north, west, east) = CellGrid.Bounds(cell);
            var samples = (await connection.QueryAsync<SampleRow>(SamplesSql, new
            {
                UserId = userId,
                cell.Ssid,
                cell.Floor,
                CellGrid.DefaultFloor,
                South = south,
                North = north,
                West = west,
                East = east
            }, transaction)).ToList();

            if (samples.Count < MinSamples)
                continue;

            int drops = samples.Count(s => s.SignalStrength == null);
            var strengths = samples.Where(s => s.SignalStrength != null).Select(s => (double)s.SignalStrength!.Value).ToList();
            double? average = strengths.Count > 0 ? strengths.Average() : null;
            bool dead = drops >= samples.Count * MaxDropShare
                || (average is double avg && Math.Round(avg) < UnusableBelowDbm);

            int networkId = samples
                .GroupBy(s => s.NetworkId)
                .OrderByDescending(g => g.Count())
                .First().Key;
            string notes = $"{samples.Count} samples, {drops} drops"
                + (average is double a ? $", average {a:0} dBm" : ", no signal");

            var (latitude, longitude) = CellGrid.Center(cell);
            var existing = await connection.QueryFirstOrDefaultAsync<ZoneRow>(ExistingSql, new
            {
                UserId = userId,
                cell.Ssid,
                cell.Floor,
                CellGrid.DefaultFloor,
                Latitude = latitude,
                Longitude = longitude
            }, transaction);

            if (dead)
            {
                if (existing == null)
                {
                    await connection.ExecuteAsync(InsertSql, new
                    {
                        Latitude = latitude,
                        Longitude = longitude,
                        Notes = notes,
                        UserId = userId,
                        NetworkId = networkId,
                        cell.Ssid,
                        cell.Floor
                    }, transaction);
                    changed++;
                }
                else
                {
                    await connection.ExecuteAsync(ReopenSql, new { existing.Id, Notes = notes, NetworkId = networkId }, transaction);
                    if (existing.ResolvedAt != null)
                        changed++;
                }
            }
            else if (existing is { ResolvedAt: null })
            {
                await connection.ExecuteAsync(ResolveSql, new { existing.Id, Notes = notes }, transaction);
                changed++;
            }
        }
        return changed;
    }

    sealed class SampleRow
    {
        public int NetworkId { get; set; }
        public int? SignalStrength { get; set; }
    }

    sealed class ZoneRow
    {
        public int Id { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
