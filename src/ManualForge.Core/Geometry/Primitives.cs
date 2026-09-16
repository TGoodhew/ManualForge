namespace ManualForge.Core.Geometry;

/// <summary>A point. Interpretation (image pixels or PDF points) depends on context.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// An axis-aligned rectangle given by its origin plus size. In image space the origin is the
/// top-left corner and Y grows downwards; in PDF user space the origin is the bottom-left corner
/// and Y grows upwards.
/// </summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool IsDegenerate => Width <= 0 || Height <= 0;

    public static RectD FromEdges(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>Smallest rectangle containing all of <paramref name="points"/>.</summary>
    public static RectD Bound(IEnumerable<PointD> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var any = false;
        foreach (var p in points)
        {
            any = true;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return any ? FromEdges(minX, minY, maxX, maxY) : default;
    }
}
