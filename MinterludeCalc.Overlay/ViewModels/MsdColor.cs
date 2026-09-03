using Avalonia.Media;

namespace MinterludeCalc.Overlay.ViewModels;

public static class MsdColor
{
    private static readonly Color[] Gradient =
    {
        Color.FromRgb(0, 255, 0),
        Color.FromRgb(255, 255, 0),
        Color.FromRgb(255, 165, 0),
        Color.FromRgb(255, 0, 0),
        Color.FromRgb(255, 0, 255)
    };

    public static IBrush ForValue(double msd)
    {
        msd = Math.Clamp(msd, 0, 35);

        double position = msd / 35.0 * (Gradient.Length - 1);
        int index = Math.Min((int)position, Gradient.Length - 2);
        double t = position - index;

        var a = Gradient[index];
        var b = Gradient[index + 1];

        var color = Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

        return new SolidColorBrush(color);
    }
}
