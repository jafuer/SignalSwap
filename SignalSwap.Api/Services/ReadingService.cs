using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using SignalSwap.Api.Data;
using SignalSwap.Api.Models;

namespace SignalSwap.Api.Services;

public sealed partial class ReadingService(Db db, DeadZoneDetector detector)
{
    public const int MaxBatch = 500;
    static readonly DateTimeOffset Earliest = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    const string FindNetworkSql = """
        SELECT id AS Id, ssid AS Ssid, security_type AS SecurityType
        FROM dbo.wifi_network WITH (UPDLOCK, HOLDLOCK)
        WHERE bssid = @Bssid
        """;

    const string InsertNetworkSql = """
        INSERT INTO dbo.wifi_network (ssid, bssid, security_type, latitude, longitude, signal_strength, discovered_by, last_seen)
        OUTPUT INSERTED.id
        VALUES (@Ssid, @Bssid, @SecurityType, @Latitude, @Longitude, @SignalStrength, @UserId, @RecordedAt)
        """;

    const string TouchNetworkSql = """
        UPDATE dbo.wifi_network
        SET last_seen = CASE WHEN last_seen < @RecordedAt THEN @RecordedAt ELSE last_seen END,
            security_type = COALESCE(security_type, @SecurityType),
            discovered_by = COALESCE(discovered_by, @UserId),
            latitude = CASE WHEN (discovered_by IS NULL OR discovered_by = @UserId)
                             AND @SignalStrength IS NOT NULL
                             AND (signal_strength IS NULL OR @SignalStrength > signal_strength)
                            THEN @Latitude ELSE latitude END,
            longitude = CASE WHEN (discovered_by IS NULL OR discovered_by = @UserId)
                              AND @SignalStrength IS NOT NULL
                              AND (signal_strength IS NULL OR @SignalStrength > signal_strength)
                             THEN @Longitude ELSE longitude END,
            signal_strength = CASE WHEN (discovered_by IS NULL OR discovered_by = @UserId)
                                    AND @SignalStrength IS NOT NULL
                                    AND (signal_strength IS NULL OR @SignalStrength > signal_strength)
                                   THEN @SignalStrength ELSE signal_strength END
        WHERE id = @Id
        """;

    const string InsertReadingSql = """
        INSERT INTO dbo.signal_reading
            (client_id, wifi_network_id, recorded_by, ssid, latitude, longitude, accuracy_m, manual_position,
             signal_strength, frequency_mhz, link_speed_mbps, recorded_at, floor)
        SELECT @ClientId, @NetworkId, @UserId, @Ssid, @Latitude, @Longitude, @AccuracyMeters, @ManualPosition,
               @SignalStrength, @FrequencyMhz, @LinkSpeedMbps, @RecordedAt, @Floor
        WHERE NOT EXISTS (SELECT 1 FROM dbo.signal_reading WITH (UPDLOCK, HOLDLOCK) WHERE client_id = @ClientId)
        """;

    const string ListSql = """
        SELECT r.client_id AS ClientId,
               r.recorded_at AS RecordedAt,
               CAST(r.latitude AS float) AS Latitude,
               CAST(r.longitude AS float) AS Longitude,
               CAST(r.accuracy_m AS float) AS AccuracyMeters,
               r.manual_position AS ManualPosition,
               r.ssid AS Ssid,
               w.bssid AS Bssid,
               w.security_type AS SecurityType,
               r.signal_strength AS SignalStrength,
               r.frequency_mhz AS FrequencyMhz,
               r.link_speed_mbps AS LinkSpeedMbps,
               r.floor AS Floor
        FROM dbo.signal_reading r
        JOIN dbo.wifi_network w ON w.id = r.wifi_network_id
        WHERE r.recorded_by = @UserId
          AND r.ssid = @Ssid
          AND (@Since IS NULL OR r.recorded_at > @Since)
        ORDER BY r.recorded_at
        """;

    const string NetworksSql = """
        SELECT r.ssid AS Ssid,
               COUNT(*) AS Readings,
               COUNT(DISTINCT ISNULL(r.floor, @DefaultFloor)) AS Floors,
               MAX(r.recorded_at) AS LastRecordedAt
        FROM dbo.signal_reading r
        WHERE r.recorded_by = @UserId
        GROUP BY r.ssid
        ORDER BY MAX(r.recorded_at) DESC
        """;

    const string ClearDeadZonesSql = """
        DELETE FROM dbo.dead_zone
        WHERE reported_by = @UserId AND source = 'auto' AND ssid = @Ssid
        """;

    const string ClearReadingsSql = """
        DELETE FROM dbo.signal_reading
        WHERE recorded_by = @UserId AND ssid = @Ssid
        """;

    public async Task<UploadResult> UploadAsync(int userId, IReadOnlyList<ReadingDto> readings, CancellationToken ct)
    {
        var synced = new List<Guid>();
        var rejected = new List<Rejection>();
        var touched = new HashSet<CellRef>();
        var networkIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var reading in readings)
        {
            if (Problem(reading) is { } reason)
            {
                rejected.Add(new Rejection(reading.ClientId, reason));
                continue;
            }

            string ssid = reading.Ssid!.Trim();
            string bssid = reading.Bssid!.Trim().ToLowerInvariant();
            string? security = string.IsNullOrWhiteSpace(reading.SecurityType) ? null : reading.SecurityType.Trim();

            decimal latitude = CellGrid.Round(reading.Latitude);
            decimal longitude = CellGrid.Round(reading.Longitude);
            var recordedAt = reading.RecordedAt.UtcDateTime;
            var network = new
            {
                Ssid = ssid,
                Bssid = bssid,
                SecurityType = security,
                Latitude = latitude,
                Longitude = longitude,
                reading.SignalStrength,
                UserId = userId,
                RecordedAt = recordedAt
            };

            if (!networkIds.TryGetValue(bssid, out int networkId))
            {
                networkId = await FindOrAddNetworkAsync(connection, transaction, network);
                networkIds[bssid] = networkId;
            }
            int inserted = await connection.ExecuteAsync(InsertReadingSql, new
            {
                reading.ClientId,
                NetworkId = networkId,
                UserId = userId,
                Ssid = ssid,
                Latitude = latitude,
                Longitude = longitude,
                AccuracyMeters = (float?)reading.AccuracyMeters,
                reading.ManualPosition,
                reading.SignalStrength,
                reading.FrequencyMhz,
                reading.LinkSpeedMbps,
                RecordedAt = recordedAt,
                reading.Floor
            }, transaction);

            synced.Add(reading.ClientId);
            if (inserted == 1)
            {
                await connection.ExecuteAsync(TouchNetworkSql, new
                {
                    Id = networkId,
                    network.SecurityType,
                    network.Latitude,
                    network.Longitude,
                    network.SignalStrength,
                    network.UserId,
                    network.RecordedAt
                }, transaction);
                touched.Add(CellGrid.Of(ssid, reading.Floor ?? CellGrid.DefaultFloor, (double)latitude, (double)longitude));
            }
        }

        int changed = await detector.UpdateAsync(connection, transaction, userId, touched);
        await transaction.CommitAsync(ct);
        return new UploadResult(synced, rejected, changed);
    }

    public async Task<IReadOnlyList<ReadingDto>> ListAsync(int userId, string ssid, DateTimeOffset? since, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<ReadingRow>(ListSql, new
        {
            UserId = userId,
            Ssid = ssid,
            Since = since?.UtcDateTime
        });
        return rows.Select(r => new ReadingDto(
            r.ClientId,
            Utc(r.RecordedAt),
            r.Latitude,
            r.Longitude,
            r.AccuracyMeters,
            r.ManualPosition,
            r.Ssid,
            r.Bssid,
            r.SecurityType,
            r.SignalStrength,
            r.FrequencyMhz,
            r.LinkSpeedMbps,
            r.Floor)).ToList();
    }

    public async Task<IReadOnlyList<NetworkSummary>> NetworksAsync(int userId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<NetworkRow>(NetworksSql, new { UserId = userId, CellGrid.DefaultFloor });
        return rows.Select(r => new NetworkSummary(r.Ssid, r.Readings, r.Floors, Utc(r.LastRecordedAt))).ToList();
    }

    public async Task<ClearResult> ClearAsync(int userId, string ssid, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        int zones = await connection.ExecuteAsync(ClearDeadZonesSql, new { UserId = userId, Ssid = ssid }, transaction);
        int readings = await connection.ExecuteAsync(ClearReadingsSql, new { UserId = userId, Ssid = ssid }, transaction);
        await transaction.CommitAsync(ct);
        return new ClearResult(readings, zones);
    }

    static async Task<int> FindOrAddNetworkAsync(IDbConnection connection, IDbTransaction transaction, object network)
    {
        var existing = await connection.QueryFirstOrDefaultAsync<NetworkRecord>(FindNetworkSql, network, transaction);
        return existing?.Id ?? await connection.ExecuteScalarAsync<int>(InsertNetworkSql, network, transaction);
    }

    static string? Problem(ReadingDto r)
    {
        if (r.ClientId == Guid.Empty)
            return "Missing client id.";
        if (!double.IsFinite(r.Latitude) || r.Latitude is < -90 or > 90)
            return "Latitude is out of range.";
        if (!double.IsFinite(r.Longitude) || r.Longitude is < -180 or > 180)
            return "Longitude is out of range.";
        if (r.AccuracyMeters is double accuracy && (!double.IsFinite(accuracy) || accuracy < 0))
            return "Accuracy must be a positive number.";
        if (string.IsNullOrWhiteSpace(r.Ssid) || r.Ssid.Trim().Length > 32)
            return "Network name must be 1 to 32 characters.";
        if (r.Bssid == null || !BssidPattern().IsMatch(r.Bssid.Trim()))
            return "Access point address must look like aa:bb:cc:dd:ee:ff.";
        if (r.SecurityType?.Trim().Length > 20)
            return "Security type is longer than 20 characters.";
        if (r.SignalStrength is < -127 or > 0)
            return "Signal strength must be between -127 and 0 dBm.";
        if (r.Floor is < -10 or > 200)
            return "Floor is out of range.";
        if (r.RecordedAt < Earliest || r.RecordedAt > DateTimeOffset.UtcNow.AddDays(1))
            return "Recorded time is out of range.";
        return null;
    }

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    [GeneratedRegex("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$")]
    private static partial Regex BssidPattern();

    sealed class NetworkRecord
    {
        public int Id { get; set; }
        public string Ssid { get; set; } = "";
        public string? SecurityType { get; set; }
    }

    sealed class ReadingRow
    {
        public Guid ClientId { get; set; }
        public DateTime RecordedAt { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public bool ManualPosition { get; set; }
        public string Ssid { get; set; } = "";
        public string Bssid { get; set; } = "";
        public string? SecurityType { get; set; }
        public int? SignalStrength { get; set; }
        public int? FrequencyMhz { get; set; }
        public int? LinkSpeedMbps { get; set; }
        public int? Floor { get; set; }
    }

    sealed class NetworkRow
    {
        public string Ssid { get; set; } = "";
        public int Readings { get; set; }
        public int Floors { get; set; }
        public DateTime LastRecordedAt { get; set; }
    }
}