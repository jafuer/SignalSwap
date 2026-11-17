using System.Security.Claims;
using Dapper;
using SignalSwap.Api.Auth;
using SignalSwap.Api.Data;
using SignalSwap.Api.Models;
using SignalSwap.Api.Services;

namespace SignalSwap.Api.Endpoints;

public static class DeadZoneEndpoints
{
    const string ListSql = """
        SELECT dz.id AS Id,
               COALESCE(dz.ssid, w.ssid) AS Ssid,
               CAST(dz.latitude AS float) AS Latitude,
               CAST(dz.longitude AS float) AS Longitude,
               ISNULL(dz.floor, @DefaultFloor) AS Floor,
               dz.notes AS Notes,
               dz.source AS Source,
               dz.reported_at AS ReportedAt,
               dz.resolved_at AS ResolvedAt
        FROM dbo.dead_zone dz
        LEFT JOIN dbo.wifi_network w ON w.id = dz.wifi_network_id
        WHERE dz.reported_by = @UserId
          AND (@Ssid IS NULL OR COALESCE(dz.ssid, w.ssid) = @Ssid)
          AND (@Floor IS NULL OR ISNULL(dz.floor, @DefaultFloor) = @Floor)
          AND (@IncludeResolved = 1 OR dz.resolved_at IS NULL)
        ORDER BY dz.reported_at DESC
        """;

    public static void MapDeadZoneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/deadzones", ListAsync).RequireAuthorization();
    }

    static async Task<IResult> ListAsync(
        string? ssid,
        int? floor,
        bool? includeResolved,
        ClaimsPrincipal user,
        Db db,
        CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<ZoneRow>(ListSql, new
        {
            UserId = user.UserId(),
            Ssid = string.IsNullOrWhiteSpace(ssid) ? null : ssid.Trim(),
            Floor = floor,
            IncludeResolved = includeResolved == true,
            CellGrid.DefaultFloor
        });

        return Results.Ok(rows.Select(r => new DeadZoneDto(
            r.Id,
            r.Ssid,
            r.Latitude,
            r.Longitude,
            r.Floor,
            r.Notes,
            r.Source,
            Utc(r.ReportedAt),
            r.ResolvedAt is DateTime resolved ? Utc(resolved) : null)).ToList());
    }

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    sealed class ZoneRow
    {
        public int Id { get; set; }
        public string? Ssid { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int Floor { get; set; }
        public string? Notes { get; set; }
        public string Source { get; set; } = "";
        public DateTime ReportedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
