using Microsoft.Maui.Controls.Shapes;

namespace SignalSwap.Ui;

public static class Theme
{
    public static readonly Color PageBackground = Color.FromArgb("#F3F6F9");
    public static readonly Color Surface = Colors.White;
    public static readonly Color Outline = Color.FromArgb("#E1E7ED");
    public static readonly Color Track = Color.FromArgb("#E9EEF2");
    public static readonly Color Text = Color.FromArgb("#1F2A33");
    public static readonly Color SubtleText = Color.FromArgb("#5F6B78");
    public static readonly Color Faint = Color.FromArgb("#8C98A5");
    public static readonly Color Primary = Color.FromArgb("#4AA8E8");
    public static readonly Color PrimaryDark = Color.FromArgb("#1E7FBF");
    public static readonly Color PrimaryTint = Color.FromArgb("#E3F2FC");
    public static readonly Color Danger = Color.FromArgb("#C9302C");

    public const string Regular = "OpenSansRegular";
    public const string Semibold = "OpenSansSemibold";

    public static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();

        resources.Add(new Style(typeof(ContentPage))
        {
            ApplyToDerivedTypes = true,
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = PageBackground } }
        });

        resources.Add(new Style(typeof(Label))
        {
            Setters =
            {
                new Setter { Property = Label.TextColorProperty, Value = Text },
                new Setter { Property = Label.FontFamilyProperty, Value = Regular },
                new Setter { Property = Label.FontSizeProperty, Value = 14.0 }
            }
        });

        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new Setter { Property = Entry.TextColorProperty, Value = Text },
                new Setter { Property = Entry.PlaceholderColorProperty, Value = Faint },
                new Setter { Property = Entry.FontFamilyProperty, Value = Regular },
                new Setter { Property = Entry.FontSizeProperty, Value = 15.0 },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Surface },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 48.0 }
            }
        });

        resources.Add(new Style(typeof(Picker))
        {
            Setters =
            {
                new Setter { Property = Picker.TextColorProperty, Value = Text },
                new Setter { Property = Picker.TitleColorProperty, Value = SubtleText },
                new Setter { Property = Picker.FontFamilyProperty, Value = Regular },
                new Setter { Property = Picker.FontSizeProperty, Value = 14.0 },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 44.0 }
            }
        });

        resources.Add(new Style(typeof(Switch))
        {
            Setters =
            {
                new Setter { Property = Switch.OnColorProperty, Value = Primary },
                new Setter { Property = Switch.ThumbColorProperty, Value = Surface }
            }
        });

        resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = Button.TextColorProperty, Value = Colors.White },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Primary },
                new Setter { Property = Button.FontFamilyProperty, Value = Semibold },
                new Setter { Property = Button.FontSizeProperty, Value = 14.0 },
                new Setter { Property = Button.CornerRadiusProperty, Value = 10 },
                new Setter { Property = Button.PaddingProperty, Value = new Thickness(14, 10) },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 44.0 }
            }
        });

        return resources;
    }

    public static Border Card(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        foreach (var child in children)
            stack.Children.Add(child);
        return CardFor(stack);
    }

    public static Border CardFor(View content) => new()
    {
        BackgroundColor = Surface,
        Stroke = Outline,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 14 },
        Padding = new Thickness(16, 14),
        Content = content
    };

    public static Label SectionTitle(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontFamily = Semibold,
        FontSize = 12,
        CharacterSpacing = 0.6,
        TextColor = SubtleText
    };

    public static Label Heading(string text = "") => new()
    {
        Text = text,
        FontFamily = Semibold,
        FontSize = 18,
        TextColor = Text
    };

    public static Label Body(string text = "") => new()
    {
        Text = text,
        FontSize = 14,
        TextColor = Text
    };

    public static Label Caption(string text = "") => new()
    {
        Text = text,
        FontSize = 12.5,
        TextColor = SubtleText
    };

    public static Button PrimaryButton(string text) => new() { Text = text };

    public static Button SecondaryButton(string text) => new()
    {
        Text = text,
        BackgroundColor = PrimaryTint,
        TextColor = PrimaryDark
    };

    public static Button DangerButton(string text) => new()
    {
        Text = text,
        BackgroundColor = Color.FromArgb("#FBEAEA"),
        TextColor = Danger
    };

    public static Grid Row(params View[] views)
    {
        var row = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < views.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.Add(views[i], i, 0);
        }
        return row;
    }

    public static Grid KeyValue(string key, Label value)
    {
        value.HorizontalTextAlignment = TextAlignment.End;
        value.FontFamily = Semibold;
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 12
        };
        grid.Add(new Label { Text = key, TextColor = SubtleText, FontSize = 14 }, 0, 0);
        grid.Add(value, 1, 0);
        return grid;
    }

    public static View Dot(Color color, double size = 10) => new Ellipse
    {
        Fill = new SolidColorBrush(color),
        WidthRequest = size,
        HeightRequest = size,
        VerticalOptions = LayoutOptions.Center
    };

    public static ScrollView PageBody(params View[] children)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(16, 12, 16, 24), Spacing = 12 };
        foreach (var child in children)
            stack.Children.Add(child);
        return new ScrollView { Content = stack };
    }
}
