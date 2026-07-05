using System;

namespace PptFigmaDrag
{
    // Rectangle in PowerPoint slide coordinates (points, 1pt = 1/72 inch).
    internal struct RectPt
    {
        public double X;
        public double Y;
        public double W;
        public double H;

        public double Right { get { return X + W; } }
        public double Bottom { get { return Y + H; } }

        public static RectPt FromCorners(double x1, double y1, double x2, double y2)
        {
            RectPt r;
            r.X = Math.Min(x1, x2);
            r.Y = Math.Min(y1, y2);
            r.W = Math.Abs(x2 - x1);
            r.H = Math.Abs(y2 - y1);
            return r;
        }

        public bool Contains(double px, double py)
        {
            return px >= X && px <= Right && py >= Y && py <= Bottom;
        }

        public bool Intersects(RectPt o)
        {
            return X <= o.Right && o.X <= Right && Y <= o.Bottom && o.Y <= Bottom;
        }

        public RectPt Expand(double margin)
        {
            RectPt r;
            r.X = X - margin;
            r.Y = Y - margin;
            r.W = W + margin * 2.0;
            r.H = H + margin * 2.0;
            return r;
        }

        // Axis-aligned bounding box of a shape frame rotated around its center.
        public static RectPt RotatedAabb(double left, double top, double width, double height, double rotationDeg)
        {
            if (rotationDeg == 0.0)
                return FromCorners(left, top, left + width, top + height);

            double rad = rotationDeg * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad));
            double sin = Math.Abs(Math.Sin(rad));
            double halfW = width / 2.0;
            double halfH = height / 2.0;
            double cx = left + halfW;
            double cy = top + halfH;
            double extentW = halfW * cos + halfH * sin;
            double extentH = halfW * sin + halfH * cos;
            return FromCorners(cx - extentW, cy - extentH, cx + extentW, cy + extentH);
        }
    }
}
