using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Pm.App.Design;

public static class Ui
{
    public static readonly SolidColorBrush Surface = new(Colors.White);
    public static readonly SolidColorBrush Well = new(Color.Parse("#F6F7F9"));
    public static readonly SolidColorBrush Line = new(Color.Parse("#E2E4E9"));
    public static readonly SolidColorBrush LineFaint = new(Color.Parse("#EDEEF1"));
    public static readonly SolidColorBrush TextStrong = new(Color.Parse("#15171C"));
    public static readonly SolidColorBrush Text = new(Color.Parse("#3E434C"));
    public static readonly SolidColorBrush Faint = new(Color.Parse("#646A74"));
    public static readonly SolidColorBrush Accent = new(Color.Parse("#2563EB"));
    public static readonly SolidColorBrush AccentHover = new(Color.Parse("#1D4ED8"));
    public static readonly SolidColorBrush AccentPressed = new(Color.Parse("#1E40AF"));
    public static readonly SolidColorBrush AccentSoft = new(Color.Parse("#E7EEFF"));
    public static readonly SolidColorBrush ControlHover = new(Color.Parse("#F0F2F5"));
    public static readonly SolidColorBrush ControlPressed = new(Color.Parse("#E6E8EC"));
    public static readonly SolidColorBrush Danger = new(Color.Parse("#C93636"));
    public static readonly SolidColorBrush DangerSoft = new(Color.Parse("#FDECEC"));
    public static readonly SolidColorBrush Dim = new(Color.Parse("#50000000"));

    public static readonly Easing Ease = new QuinticEaseOut();
    public static readonly Easing EaseIn = new CubicEaseIn();

    // shortcut: global brushes assume one theme per process; move to DynamicResource if per-window themes appear.
    public static void Apply(ThemeVariant variant)
    {
        var dark = variant == ThemeVariant.Dark;
        Set(Well, dark ? "#141416" : "#F6F7F9");
        Set(Surface, dark ? "#1F1F22" : "#FFFFFF");
        Set(Line, dark ? "#3A3A3E" : "#E2E4E9");
        Set(LineFaint, dark ? "#2C2C30" : "#EDEEF1");
        Set(TextStrong, dark ? "#F5F5F7" : "#15171C");
        Set(Text, dark ? "#D1D1D6" : "#3E434C");
        Set(Faint, dark ? "#8E8E93" : "#646A74");
        Set(AccentSoft, dark ? "#1E2A44" : "#E7EEFF");
        Set(ControlHover, dark ? "#2A2A2E" : "#F0F2F5");
        Set(ControlPressed, dark ? "#333338" : "#E6E8EC");
        Set(Danger, dark ? "#FF6B6B" : "#C93636");
        Set(DangerSoft, dark ? "#3A2020" : "#FDECEC");
        Set(Dim, dark ? "#70000000" : "#50000000");
    }

    private static void Set(SolidColorBrush brush, string hex) => brush.Color = Color.Parse(hex);

    // Fluent's Button theme paints the template presenter on hover/pressed, ignoring local Background.
    // shortcut: targets the Fluent template part; revisit on Avalonia upgrade.
    public static void Install(Styles styles)
    {
        static Style State(string cls, string state, IBrush bg, IBrush border, IBrush fg) =>
            new(x => x.OfType<Button>().Class(cls).Class(state).Template().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, bg),
                    new Setter(ContentPresenter.BorderBrushProperty, border),
                    new Setter(ContentPresenter.ForegroundProperty, fg),
                },
            };

        styles.Add(new Style(x => x
            .OfType<TextBox>()
            .Class(":pointerover")
            .Not(y => y.Class(":focus"))
            .Template()
            .Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Border.BorderBrushProperty, Line),
                new Setter(Border.BackgroundProperty, Surface),
                new Setter(Border.BorderThicknessProperty, new Thickness(1)),
            },
        });

        styles.Add(State("primary", ":pointerover", AccentHover, AccentHover, Brushes.White));
        styles.Add(State("primary", ":pressed", AccentPressed, AccentPressed, Brushes.White));
        styles.Add(State("secondary", ":pointerover", ControlHover, Line, TextStrong));
        styles.Add(State("secondary", ":pressed", ControlPressed, Line, TextStrong));
        styles.Add(State("ghost", ":pointerover", ControlHover, Brushes.Transparent, Faint));
        styles.Add(State("ghost", ":pressed", ControlPressed, Brushes.Transparent, Faint));
        styles.Add(State("danger", ":pointerover", DangerSoft, Danger, Danger));
        styles.Add(State("danger", ":pressed", DangerSoft, Danger, Danger));
    }

    public static Button Button(string text, Action click, bool primary = false, bool small = false, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = small ? 12 : 13,
            Padding = new Thickness(small ? 10 : 14, small ? 5 : 8),
            CornerRadius = new CornerRadius(7),
            Background = primary ? Accent : danger ? Brushes.Transparent : Surface,
            Foreground = primary ? Brushes.White : danger ? Danger : TextStrong,
            BorderBrush = primary ? Accent : danger ? Danger : Line,
            BorderThickness = new Thickness(1),
            MinHeight = small ? 28 : 34,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add(primary ? "primary" : danger ? "danger" : "secondary");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Ghost(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(8, 4),
            Background = Brushes.Transparent,
            Foreground = Faint,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            MinWidth = 28,
            MinHeight = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("ghost");
        button.Click += (_, _) => click();
        return button;
    }

    public static TextBox Input(string watermark)
    {
        var box = new TextBox
        {
            PlaceholderText = watermark,
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            FontSize = 13,
            MinHeight = 36,
            Foreground = TextStrong,
        };
        AutomationProperties.SetName(box, watermark);
        return box;
    }

    public static Control Field(string label, Control input)
    {
        if (input is TextBox box) AutomationProperties.SetName(box, label);
        var stack = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 12),
        };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Faint,
        });
        stack.Children.Add(input);
        return stack;
    }

    public static Border Card(Control content, double width = 0) =>
        new()
        {
            Background = Surface,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Width = width > 0 ? width : double.NaN,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 18,
                Blur = 44,
                Color = Color.FromArgb(46, 0, 0, 0),
            }),
            Child = content,
        };
}
