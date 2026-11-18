namespace SignalSwap.Ui;

public sealed class GaugeDrawable : IDrawable
{
    const float StrokeWidth = 14;
    const int Segments = 72;

    public int? Score { get; set; }

    public Color Color { get; set; } = Theme.Primary;

    public int? Threshold { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        float radius = Math.Min(rect.Width / 2, rect.Height) - StrokeWidth;
        if (radius <= 0)
            return;

        var center = new PointF(rect.Center.X, rect.Bottom - StrokeWidth / 2 - 2);
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeSize = StrokeWidth;

        canvas.StrokeColor = Theme.Track;
        canvas.DrawPath(Arc(center, radius, 1));

        if (Score is int score && score > 0)
        {
            canvas.StrokeColor = Color;
            canvas.DrawPath(Arc(center, radius, Math.Clamp(score / 100f, 0.01f, 1f)));
        }

        if (Threshold is int threshold)
        {
            double angle = Math.PI * (1 - threshold / 100.0);
            float inner = radius - StrokeWidth / 2 - 3;
            float outer = radius + StrokeWidth / 2 + 3;
            canvas.StrokeLineCap = LineCap.Butt;
            canvas.StrokeSize = 2;
            canvas.StrokeColor = Theme.SubtleText;
            var from = Point(center, inner, angle);
            var to = Point(center, outer, angle);
            canvas.DrawLine(from.X, from.Y, to.X, to.Y);
        }
    }

    static PathF Arc(PointF center, float radius, float fraction)
    {
        var path = new PathF();
        int steps = Math.Max(2, (int)(Segments * fraction));
        for (int i = 0; i <= steps; i++)
        {
            double angle = Math.PI * (1 - fraction * i / steps);
            var point = Point(center, radius, angle);
            if (i == 0)
                path.MoveTo(point);
            else
                path.LineTo(point);
        }
        return path;
    }

    static PointF Point(PointF center, float radius, double angle) =>
        new(center.X + radius * (float)Math.Cos(angle), center.Y - radius * (float)Math.Sin(angle));
}

public sealed class SparklineDrawable : IDrawable
{
    public IReadOnlyList<int?> Points { get; set; } = Array.Empty<int?>();

    public int Capacity { get; set; } = 60;

    public int? Threshold { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        const float pad = 6;
        var plot = new RectF(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);

        canvas.StrokeSize = 1;
        canvas.StrokeColor = Theme.Outline;
        canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        if (Threshold is int threshold)
        {
            float y = Y(plot, threshold);
            canvas.StrokeColor = Theme.Faint;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
        }

        if (Points.Count == 0)
            return;

        float step = plot.Width / Math.Max(1, Capacity - 1);
        float startX = plot.Right - step * (Points.Count - 1);
        var segments = new List<List<PointF>>();
        List<PointF>? current = null;
        for (int i = 0; i < Points.Count; i++)
        {
            if (Points[i] is not int score)
            {
                current = null;
                continue;
            }
            if (current == null)
            {
                current = new List<PointF>();
                segments.Add(current);
            }
            current.Add(new PointF(startX + step * i, Y(plot, score)));
        }

        foreach (var segment in segments)
        {
            var area = new PathF();
            area.MoveTo(segment[0].X, plot.Bottom);
            foreach (var p in segment)
                area.LineTo(p);
            area.LineTo(segment[^1].X, plot.Bottom);
            area.Close();
            canvas.FillColor = Theme.Primary.WithAlpha(0.1f);
            canvas.FillPath(area);

            if (segment.Count > 1)
            {
                var line = new PathF();
                line.MoveTo(segment[0]);
                foreach (var p in segment.Skip(1))
                    line.LineTo(p);
                canvas.StrokeColor = Theme.Primary;
                canvas.StrokeSize = 2;
                canvas.StrokeLineCap = LineCap.Round;
                canvas.StrokeLineJoin = LineJoin.Round;
                canvas.DrawPath(line);
            }
        }

        if (Points[^1] is int last)
        {
            var end = new PointF(startX + step * (Points.Count - 1), Y(plot, last));
            canvas.FillColor = Theme.Surface;
            canvas.FillCircle(end.X, end.Y, 6);
            canvas.FillColor = Theme.Primary;
            canvas.FillCircle(end.X, end.Y, 4);
        }
    }

    static float Y(RectF plot, int score) => plot.Bottom - plot.Height * Math.Clamp(score, 0, 100) / 100f;
}

public sealed class MeterBar : ContentView
{
    readonly BoxView fill = new() { CornerRadius = 4, HeightRequest = 8 };
    readonly ColumnDefinition filled = new(new GridLength(0, GridUnitType.Star));
    readonly ColumnDefinition empty = new(new GridLength(100, GridUnitType.Star));

    public MeterBar()
    {
        var grid = new Grid
        {
            ColumnSpacing = 0,
            HeightRequest = 8,
            ColumnDefinitions = { filled, empty }
        };
        var track = new BoxView { Color = Theme.Track, CornerRadius = 4, HeightRequest = 8 };
        grid.Add(track, 0, 0);
        Grid.SetColumnSpan(track, 2);
        grid.Add(fill, 0, 0);
        Content = grid;
    }

    public void Set(int? score, Color color)
    {
        int value = Math.Clamp(score ?? 0, 0, 100);
        filled.Width = new GridLength(value, GridUnitType.Star);
        empty.Width = new GridLength(100 - value, GridUnitType.Star);
        fill.Color = color;
        fill.IsVisible = score is > 0;
    }
}
