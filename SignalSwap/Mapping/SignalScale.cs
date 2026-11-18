using SignalSwap.Core;

namespace SignalSwap.Mapping;

public static class SignalScale
{
    public static readonly Color Excellent = Color.FromArgb("#1F8AE0");
    public static readonly Color Good = Color.FromArgb("#7CCBF6");
    public static readonly Color Fair = Color.FromArgb("#F7D154");
    public static readonly Color Weak = Color.FromArgb("#F39237");
    public static readonly Color Poor = Color.FromArgb("#C9302C");
    public static readonly Color Dropped = Color.FromArgb("#3E4852");
    public static readonly Color Unknown = Color.FromArgb("#A9B4BF");

    public static QualityLevel LevelOf(double rssi, int? frequencyMhz) =>
        SignalRater.LevelOf(SignalRater.StrengthScore(rssi, frequencyMhz));

    public static Color ColorOf(QualityLevel level) => level switch
    {
        QualityLevel.Excellent => Excellent,
        QualityLevel.Good => Good,
        QualityLevel.Fair => Fair,
        QualityLevel.Weak => Weak,
        QualityLevel.Poor => Poor,
        _ => Dropped
    };

    public static string LabelOf(QualityLevel level) => level switch
    {
        QualityLevel.Excellent => "Excellent",
        QualityLevel.Good => "Good",
        QualityLevel.Fair => "Fair",
        QualityLevel.Weak => "Weak",
        QualityLevel.Poor => "Poor",
        _ => "No signal"
    };

    public static IReadOnlyList<(string Label, Color Color)> Legend { get; } = new[]
    {
        ("Excellent", Excellent),
        ("Good", Good),
        ("Fair", Fair),
        ("Weak", Weak),
        ("Poor", Poor),
        ("Dropped", Dropped)
    };
}
