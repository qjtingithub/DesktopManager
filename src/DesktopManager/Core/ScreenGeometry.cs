namespace DesktopManager.Core;

public readonly record struct ScreenPoint(int X, int Y)
{
    public static ScreenPoint operator +(ScreenPoint point, ScreenDelta delta) =>
        new(point.X + delta.X, point.Y + delta.Y);
}

public readonly record struct ScreenDelta(int X, int Y);

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool Contains(ScreenPoint point) =>
        point.X >= Left && point.X < Right &&
        point.Y >= Top && point.Y < Bottom;

    public ScreenRect MoveBy(ScreenDelta delta) =>
        new(Left + delta.X, Top + delta.Y, Width, Height);
}
