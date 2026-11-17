using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SignalSwap.Api.Auth;
using SignalSwap.Api.Data;
using SignalSwap.Api.Endpoints;
using SignalSwap.Api.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options => options.ServiceName = "SignalSwapApi");

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
jwt.Validate();
var connectionString = builder.Configuration.GetConnectionString("Networks")
    ?? throw new InvalidOperationException("ConnectionStrings:Networks is missing from appsettings.json.");

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(new Db(connectionString));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<DeadZoneDetector>();
builder.Services.AddScoped<ReadingService>();
builder.Services.AddProblemDetails();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwt.SigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = TokenService.UsernameClaim
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                if (context.Principal is not { } principal)
                {
                    context.Fail("The token is not valid.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<Db>();
                await using var connection = await db.OpenAsync(context.HttpContext.RequestAborted);
                int? current = await connection.QueryFirstOrDefaultAsync<int?>(
                    "SELECT token_version FROM dbo.app_user WHERE id = @Id",
                    new { Id = principal.UserId() });
                if (current == null || current != principal.TokenVersion())
                    context.Fail("This session has ended. Log in again.");
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthLimits.Policy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = AuthLimits.AttemptsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", async (Db db, CancellationToken ct) =>
{
    await using var connection = await db.OpenAsync(ct);
    await connection.ExecuteScalarAsync<int>("SELECT 1");
    return Results.Ok(new { status = "ok", database = connection.Database });
});

app.MapAuthEndpoints();
app.MapReadingEndpoints();
app.MapDeadZoneEndpoints();

app.Run();
