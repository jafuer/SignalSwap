using System.Security.Claims;
using SignalSwap.Api.Auth;
using SignalSwap.Api.Models;
using SignalSwap.Api.Services;

namespace SignalSwap.Api.Endpoints;

public static class ReadingEndpoints
{
    public static void MapReadingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/readings").RequireAuthorization();
        group.MapPost("", UploadAsync);
        group.MapGet("", ListAsync);
        group.MapDelete("", ClearAsync);

        app.MapGet("/api/networks", async (ClaimsPrincipal user, ReadingService readings, CancellationToken ct) =>
            Results.Ok(await readings.NetworksAsync(user.UserId(), ct)))
            .RequireAuthorization();
    }

    static async Task<IResult> UploadAsync(List<ReadingDto> batch, ClaimsPrincipal user, ReadingService readings, CancellationToken ct)
    {
        if (batch.Count == 0)
            return Results.Ok(new UploadResult(Array.Empty<Guid>(), Array.Empty<Rejection>(), 0));
        if (batch.Count > ReadingService.MaxBatch)
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"Send at most {ReadingService.MaxBatch} readings at a time.");

        return Results.Ok(await readings.UploadAsync(user.UserId(), batch, ct));
    }

    static async Task<IResult> ListAsync(string? ssid, DateTimeOffset? since, ClaimsPrincipal user, ReadingService readings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ssid))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Pass the network name as ?ssid=.");

        return Results.Ok(await readings.ListAsync(user.UserId(), ssid.Trim(), since, ct));
    }

    static async Task<IResult> ClearAsync(string? ssid, ClaimsPrincipal user, ReadingService readings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ssid))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Pass the network name as ?ssid=.");

        return Results.Ok(await readings.ClearAsync(user.UserId(), ssid.Trim(), ct));
    }
}
