using System.Drawing;

namespace DuoClick
{
    /// <summary>Pure geometry helpers for desktop selection and focused tests.</summary>
    internal static class SelectionGeometry
    {
        internal static Rectangle GetRectangle(Rectangle desktopBounds, Point start, Point end)
        {
            int left = System.Math.Min(start.X, end.X);
            int top = System.Math.Min(start.Y, end.Y);
            int right = System.Math.Max(start.X, end.X);
            int bottom = System.Math.Max(start.Y, end.Y);
            return Rectangle.Intersect(desktopBounds, Rectangle.FromLTRB(left, top, right, bottom));
        }

        internal static bool IsLargeEnough(Rectangle rectangle)
        {
            return rectangle.Width >= 2 && rectangle.Height >= 2;
        }
    }
}
