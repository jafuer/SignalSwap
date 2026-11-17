namespace SignalMonitorTest3.Mapping;

public static class SignalScale
{
    public static readonly Color DroppedColor = Colors.Purple;

    public static (string Label, Color Color) Rate(int rssi) => rssi switch
    {
        >= -55 => ("Excellent", Colors.Green),
        >= -67 => ("Good", Colors.LimeGreen),
        >= -75 => ("Fair", Colors.Orange),
        >= -82 => ("Weak", Colors.OrangeRed),
        _ => ("Unusable", Colors.Red)
    };

    public static IReadOnlyList<(string Label, Color Color)> Legend { get; } = new[]
    {
        Rate(-50),
        Rate(-60),
        Rate(-70),
        Rate(-80),
        Rate(-90),
        ("Dropped", DroppedColor)
    };
}
