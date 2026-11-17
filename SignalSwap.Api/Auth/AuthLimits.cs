namespace SignalSwap.Api.Auth;

public static class AuthLimits
{
    public const string Policy = "auth";
    public const int AttemptsPerMinute = 10;
    public const int FailuresBeforeLock = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
}
