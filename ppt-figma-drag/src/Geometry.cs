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

        // True when the segment (x0,y0)-(x1,y1) touches this rectangle. Used for
        // straight lines/connectors, whose AABB vastly overstates their real hit area.
        public bool IntersectsSegment(double x0, double y0, double x1, double y1)
        {
            if (Contains(x0, y0) || Contains(x1, y1))
                return true;
            return SegmentsIntersect(x0, y0, x1, y1, X, Y, Right, Y) ||
                   SegmentsIntersect(x0, y0, x1, y1, Right, Y, Right, Bottom) ||
                   SegmentsIntersect(x0, y0, x1, y1, Right, Bottom, X, Bottom) ||
                   SegmentsIntersect(x0, y0, x1, y1, X, Bottom, X, Y);
        }

        public static double DistancePointToSegment(double px, double py,
            double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0;
            double dy = y1 - y0;
            double lengthSq = dx * dx + dy * dy;
            double t = 0.0;
            if (lengthSq > 0.0)
            {
                t = ((px - x0) * dx + (py - y0) * dy) / lengthSq;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;
            }
            double ex = px - (x0 + t * dx);
            double ey = py - (y0 + t * dy);
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static double Cross(double ax, double ay, double bx, double by)
        {
            return ax * by - ay * bx;
        }

        // Proper (non-collinear) segment intersection; collinear grazing contact is
        // treated as a miss, which is fine at marquee scale.
        public static bool SegmentsIntersect(double ax0, double ay0, double ax1, double ay1,
            double bx0, double by0, double bx1, double by1)
        {
            double d1 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
            double d2 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);
            double d3 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
            double d4 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
            return ((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0)) &&
                   ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0));
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
