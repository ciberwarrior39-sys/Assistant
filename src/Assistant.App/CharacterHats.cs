using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Assistant.App;

public enum HatKind { None, Pumpkin, Santa, Party }

/// <summary>
/// Seasonal hats for the drawn characters, each 40 × 40 with its bottom middle (20, 40) resting on the head:
/// a pumpkin for Halloween, a Santa hat for Christmas, a party hat for New Year and birthdays.
/// </summary>
static class CharacterHats
{
    public const double Size = 40;

    /// <summary>Which hat today calls for, if any. The birthday wins.</summary>
    public static HatKind For(DateTime today, bool birthday) => birthday ? HatKind.Party : (today.Month, today.Day) switch
    {
        (10, 31) => HatKind.Pumpkin,
        (12, >= 20 and <= 26) => HatKind.Santa,
        (12, 31) or (1, 1) => HatKind.Party,
        _ => HatKind.None,
    };

    public static FrameworkElement Create(HatKind kind)
    {
        var hat = new Canvas { Width = Size, Height = Size, IsHitTestVisible = false };
        switch (kind)
        {
            case HatKind.Pumpkin:
                SolidColorBrush orange = Motion.Frozen(Color.FromRgb(0xF2, 0x8C, 0x28));
                SolidColorBrush rib = Motion.Frozen(Color.FromRgb(0xD9, 0x6F, 0x10));
                Motion.Place(hat, new Rectangle { Width = 4, Height = 8, RadiusX = 1.5, RadiusY = 1.5, Fill = Motion.Frozen(Color.FromRgb(0x3E, 0x8E, 0x3A)) }, 18, 8);
                Motion.Place(hat, new Ellipse { Width = 34, Height = 26, Fill = orange, Stroke = Motion.Outline, StrokeThickness = 2 }, 3, 14);
                Motion.Place(hat, new Ellipse { Width = 12, Height = 24, Stroke = rib, StrokeThickness = 1.5 }, 14, 15);
                break;

            case HatKind.Santa:
                SolidColorBrush red = Motion.Frozen(Color.FromRgb(0xD6, 0x2B, 0x2B));
                hat.Children.Add(new Polygon { Points = PointCollection.Parse("6,32 32,32 30,6"), Fill = red, Stroke = Motion.Outline, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
                Motion.Place(hat, new Rectangle { Width = 36, Height = 9, RadiusX = 4.5, RadiusY = 4.5, Fill = Brushes.White }, 2, 30);
                Motion.Place(hat, new Ellipse { Width = 9, Height = 9, Fill = Brushes.White }, 26, 1);
                break;

            case HatKind.Party:
                SolidColorBrush purple = Motion.Frozen(Color.FromRgb(0x8E, 0x4C, 0xE6));
                SolidColorBrush yellow = Motion.Frozen(Color.FromRgb(0xFF, 0xD1, 0x3B));
                hat.Children.Add(new Polygon { Points = PointCollection.Parse("8,38 32,38 20,4"), Fill = purple, Stroke = Motion.Outline, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
                hat.Children.Add(new Polygon { Points = PointCollection.Parse("12.5,26 27.5,26 26,22 14,22"), Fill = yellow });
                hat.Children.Add(new Polygon { Points = PointCollection.Parse("16.4,15 23.6,15 22.4,11.5 17.6,11.5"), Fill = yellow });
                Motion.Place(hat, new Ellipse { Width = 8, Height = 8, Fill = yellow, Stroke = Motion.Outline, StrokeThickness = 1 }, 16, 0);
                break;
        }
        return hat;
    }
}
