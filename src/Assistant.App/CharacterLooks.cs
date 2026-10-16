using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path; // not System.IO.Path

namespace Assistant.App;

/// <summary>How a mood looks: body color, label, bounce height and speed, wobble, how open the eyes are.</summary>
public readonly record struct MoodLook(Color Color, string Label, double BobHeight, double BobSeconds, double Tilt, double EyesOpen);

/// <summary>
/// One character's art. The window owns the label, the size and the bounce; a look only draws the figure
/// and shows the mood on it. Built-in looks are drawn here; custom ones play the user's animations (CustomLook).
/// </summary>
public abstract class CharacterLook
{
    public abstract FrameworkElement Figure { get; }

    /// <summary>Whether the window bounces and wobbles the figure. Custom animations bring their own movement.</summary>
    public virtual bool UsesMotion => true;

    /// <param name="overridden">The color is the screensaver's, not the mood's.</param>
    public abstract void Apply(Mood mood, MoodLook look, bool calm, bool overridden);

    /// <summary>Hidden (game mode, hide for an hour): stop anything that keeps running.</summary>
    public virtual void Stop() { }

    /// <summary>Whether it has eyes that can follow the cursor. Custom animations don't.</summary>
    public virtual bool HasEyes => false;

    /// <summary>Between the eyes, in the figure's own coordinates: where the gaze is measured from.</summary>
    public virtual Point EyeCenter => new(48, 45);

    /// <summary>Where the eyes look: (0, 0) straight ahead, up to length 1 toward the cursor.</summary>
    public virtual void LookAt(Vector direction) { }
}

/// <summary>Shared animation helpers, all at the window's frame rate to keep CPU use low.</summary>
static class Motion
{
    public const int FramesPerSecond = 30; // prototype: 0.3% CPU at 30 fps vs 0.9% at 60

    public static void ToColor(SolidColorBrush brush, Color color)
    {
        var change = new ColorAnimation(color, TimeSpan.FromMilliseconds(300));
        Timeline.SetDesiredFrameRate(change, FramesPerSecond);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, change);
    }

    public static void Loop(IAnimatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(animation, FramesPerSecond);
        target.BeginAnimation(property, animation);
    }

    public static void Still(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    public static readonly SolidColorBrush Outline = Frozen(Colors.White);
    public static readonly SolidColorBrush Dark = Frozen(Color.FromRgb(0x22, 0x22, 0x22));

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>A round white eye with a pupil that the gaze moves; the pupil's shape gives each character its own look.</summary>
    public static Grid Eye(double pupilWidth, double pupilHeight, TranslateTransform gaze)
    {
        var eye = new Grid { Width = 18, Height = 18, Margin = new Thickness(5, 0, 5, 0) };
        eye.Children.Add(new Ellipse { Fill = Brushes.White });
        eye.Children.Add(new Ellipse { Width = pupilWidth, Height = pupilHeight, Fill = Dark, RenderTransform = gaze });
        return eye;
    }

    /// <summary>Moves both pupils: up to maxX and maxY pixels from the middle.</summary>
    public static void Gaze(TranslateTransform[] pupils, Vector direction, double maxX, double maxY)
    {
        foreach (TranslateTransform pupil in pupils)
        {
            pupil.X = direction.X * maxX;
            pupil.Y = direction.Y * maxY;
        }
    }

    public static void Place(Canvas canvas, UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }
}

/// <summary>The original character: a round body with two eyes.</summary>
public sealed class BlobLook : CharacterLook
{
    readonly SolidColorBrush body = new(Color.FromRgb(0x4A, 0x90, 0xE2));
    readonly ScaleTransform eyelids = new();
    readonly TranslateTransform[] pupils = [new(), new()];

    public override FrameworkElement Figure { get; }
    public override bool HasEyes => true;
    public override Point EyeCenter => new(48, 41);

    public BlobLook()
    {
        var eyes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            RenderTransform = eyelids,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        eyes.Children.Add(Motion.Eye(8, 8, pupils[0]));
        eyes.Children.Add(Motion.Eye(8, 8, pupils[1]));

        var figure = new Grid { Width = 96, Height = 96 };
        figure.Children.Add(new Ellipse { Width = 96, Height = 96, Fill = body, Stroke = Motion.Outline, StrokeThickness = 3 });
        figure.Children.Add(eyes);
        Figure = figure;
    }

    public override void Apply(Mood mood, MoodLook look, bool calm, bool overridden)
    {
        Motion.ToColor(body, look.Color);
        eyelids.ScaleY = look.EyesOpen;
    }

    public override void LookAt(Vector direction) => Motion.Gaze(pupils, direction, 4, 4);
}

/// <summary>A little robot: a boxy head with a screen for a face, and an antenna that blinks while it works.</summary>
public sealed class RobotLook : CharacterLook
{
    static readonly SolidColorBrush Screen = Motion.Frozen(Color.FromRgb(0x22, 0x26, 0x2E));
    static readonly SolidColorBrush Glow = Motion.Frozen(Color.FromRgb(0x7F, 0xF0, 0xFF));

    readonly SolidColorBrush body = new(Color.FromRgb(0x4A, 0x90, 0xE2));
    readonly ScaleTransform eyelids = new();
    readonly Ellipse bulb = new() { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Motion.Outline, StrokeThickness = 2 };
    readonly Rectangle mouth = new() { Height = 3, Fill = Glow, RadiusX = 1.5, RadiusY = 1.5 };
    readonly TranslateTransform[] pupils = [new(), new()];

    public override FrameworkElement Figure { get; }
    public override bool HasEyes => true;
    public override Point EyeCenter => new(48, 49);

    public RobotLook()
    {
        var canvas = new Canvas { Width = 96, Height = 96 };

        Motion.Place(canvas, new Line { X1 = 48, Y1 = 10, X2 = 48, Y2 = 22, Stroke = Motion.Outline, StrokeThickness = 3 }, 0, 0);
        Motion.Place(canvas, bulb, 42, 1);
        Motion.Place(canvas, Ear(), 1, 44);
        Motion.Place(canvas, Ear(), 85, 44);
        Motion.Place(canvas, new Rectangle
        {
            Width = 80, Height = 70, RadiusX = 16, RadiusY = 16, Fill = body, Stroke = Motion.Outline, StrokeThickness = 3,
        }, 8, 22);
        Motion.Place(canvas, new Rectangle { Width = 60, Height = 40, RadiusX = 9, RadiusY = 9, Fill = Screen }, 18, 34);

        var eyes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = eyelids,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        eyes.Children.Add(new Rectangle { Width = 12, Height = 14, RadiusX = 4, RadiusY = 4, Fill = Glow, Margin = new Thickness(11, 0, 0, 0), RenderTransform = pupils[0] });
        eyes.Children.Add(new Rectangle { Width = 12, Height = 14, RadiusX = 4, RadiusY = 4, Fill = Glow, Margin = new Thickness(14, 0, 0, 0), RenderTransform = pupils[1] });
        Motion.Place(canvas, eyes, 18, 42);
        Motion.Place(canvas, mouth, 40, 64);

        Figure = canvas;
    }

    Rectangle Ear() => new() { Width = 10, Height = 22, RadiusX = 3, RadiusY = 3, Fill = body, Stroke = Motion.Outline, StrokeThickness = 2 };

    public override void Apply(Mood mood, MoodLook look, bool calm, bool overridden)
    {
        Motion.ToColor(body, look.Color);
        eyelids.ScaleY = look.EyesOpen;

        // A smile when things go well, a small "o" when puzzled.
        mouth.Width = mood switch
        {
            Mood.Happy or Mood.FoundIt or Mood.Speaking => 26,
            Mood.Confused or Mood.TimeUp => 8,
            _ => 16,
        };
        Canvas.SetLeft(mouth, 48 - mouth.Width / 2);

        // The antenna blinks while it works or listens.
        bool busy = mood is Mood.Thinking or Mood.Listening or Mood.Speaking or Mood.HandRaised or Mood.TimeUp;
        if (busy) Motion.Loop(bulb, UIElement.OpacityProperty, 1, 0.15, calm ? 1.6 : 0.6);
        else Motion.Still(bulb, UIElement.OpacityProperty, 1);
    }

    public override void Stop() => Motion.Still(bulb, UIElement.OpacityProperty, 1);

    // The screen-eyes slide around inside the face screen.
    public override void LookAt(Vector direction) => Motion.Gaze(pupils, direction, 6, 4);
}

/// <summary>A cat: round head, pointy ears, whiskers and a tail that swishes faster when it's happy.</summary>
public sealed class CatLook : CharacterLook
{
    static readonly SolidColorBrush Pink = Motion.Frozen(Color.FromRgb(0xF7, 0xA8, 0xC0));

    readonly SolidColorBrush body = new(Color.FromRgb(0x4A, 0x90, 0xE2));
    readonly ScaleTransform eyelids = new();
    readonly RotateTransform swish = new(0, 74, 86);
    readonly TranslateTransform[] pupils = [new(), new()];

    public override FrameworkElement Figure { get; }
    public override bool HasEyes => true;
    public override Point EyeCenter => new(48, 49);

    public CatLook()
    {
        var canvas = new Canvas { Width = 96, Height = 96 };

        // The tail, behind everything: a white outline under a body-colored stroke.
        var tail = new Canvas { RenderTransform = swish };
        Geometry curve = Geometry.Parse("M 74,86 C 98,82 98,54 88,42");
        tail.Children.Add(new Path { Data = curve, Stroke = Motion.Outline, StrokeThickness = 13, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        tail.Children.Add(new Path { Data = curve, Stroke = body, StrokeThickness = 8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        canvas.Children.Add(tail);

        canvas.Children.Add(Ear("12,40 20,6 42,26"));
        canvas.Children.Add(Ear("84,40 76,6 54,26"));
        canvas.Children.Add(new Polygon { Points = PointCollection.Parse("20,30 23,15 34,25"), Fill = Pink });
        canvas.Children.Add(new Polygon { Points = PointCollection.Parse("76,30 73,15 62,25"), Fill = Pink });

        Motion.Place(canvas, new Ellipse { Width = 80, Height = 66, Fill = body, Stroke = Motion.Outline, StrokeThickness = 3 }, 8, 24);

        var eyes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            RenderTransform = eyelids,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        eyes.Children.Add(Motion.Eye(5, 13, pupils[0]));
        eyes.Children.Add(Motion.Eye(5, 13, pupils[1]));
        Motion.Place(canvas, eyes, 20, 40); // two 28-wide eyes, centered on the head at 48

        canvas.Children.Add(new Polygon { Points = PointCollection.Parse("44,62 52,62 48,67"), Fill = Pink });
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 42,69 Q 45,72 48,67 Q 51,72 54,69"),
            Stroke = Motion.Dark,
            StrokeThickness = 1.5,
        });
        foreach ((double x1, double y1, double x2, double y2) in new[] { (6.0, 60.0, 30.0, 63.0), (6.0, 70.0, 30.0, 67.0), (90.0, 60.0, 66.0, 63.0), (90.0, 70.0, 66.0, 67.0) })
            canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Motion.Outline, StrokeThickness = 1.5 });

        Figure = canvas;
    }

    Polygon Ear(string points) => new()
    {
        Points = PointCollection.Parse(points),
        Fill = body,
        Stroke = Motion.Outline,
        StrokeThickness = 3,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public override void Apply(Mood mood, MoodLook look, bool calm, bool overridden)
    {
        Motion.ToColor(body, look.Color);
        eyelids.ScaleY = look.EyesOpen;

        // Still while asleep; a quick swish when happy or something was found; a lazy one otherwise.
        double seconds = mood switch
        {
            Mood.Sleepy => 0,
            Mood.Happy or Mood.FoundIt or Mood.TimeUp => 0.5,
            Mood.Thinking or Mood.Listening => 1.0,
            _ => 2.2,
        };
        if (seconds == 0) Motion.Still(swish, RotateTransform.AngleProperty, 0);
        else Motion.Loop(swish, RotateTransform.AngleProperty, calm ? -4 : -10, calm ? 4 : 12, calm ? seconds * 1.5 : seconds);
    }

    public override void Stop() => Motion.Still(swish, RotateTransform.AngleProperty, 0);

    // Tall slit pupils: they move further sideways than up and down.
    public override void LookAt(Vector direction) => Motion.Gaze(pupils, direction, 5, 2);
}
