namespace ClashTray.App;

internal readonly record struct ScreenBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

internal static class FlyoutPlacement
{
    // All coordinates here are physical screen pixels, including negative
    // monitor origins. Bottom taskbars dock the panel to the work-area corner.
    public static ScreenBounds Calculate(ScreenBounds monitor, ScreenBounds work, double scale)
    {
        int gap = Math.Max(1, (int)Math.Round(8 * scale));
        gap = Math.Min(gap, Math.Max(0, (Math.Min(work.Width, work.Height) - 1) / 2));
        int width = Math.Min((int)Math.Round(420 * scale), work.Width - 2 * gap);
        int height = Math.Min((int)Math.Round(680 * scale), work.Height - 2 * gap);
        int x = work.Right - gap - width;
        int y = work.Bottom - gap - height;
        if (work.Y > monitor.Y)
        {
            y = work.Y + gap;
        }

        if (work.X > monitor.X)
        {
            x = work.X + gap;
        }

        return new ScreenBounds(x, y, width, height);
    }
}
