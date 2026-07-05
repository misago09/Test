using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PptFigmaDrag
{
    internal sealed class ShapeInfo
    {
        public int Index;              // 1-based index in Slide.Shapes at snapshot time
        public int Id;                 // Shape.Id, used to detect stale indexes
        public RectPt Bounds;          // rotation-aware AABB in slide points
        public bool WasSelected;       // selected before the drag started
        public bool IntersectSelectable; // false for prompt-only placeholders
        public bool IsSegment;         // straight line/connector: use real geometry
        public double SegX0, SegY0, SegX1, SegY1;
    }

    internal sealed class SlideSnapshot
    {
        public List<ShapeInfo> Shapes;
        public double DownPtX; // mouse-down position in slide points
        public double DownPtY;
        public int SlideIndex;
        public int TotalShapeCount;    // Slide.Shapes.Count at mouse-down
        public string PresentationName;
    }

    // Talks to a running PowerPoint instance over COM (late binding, so no Office
    // PIA is needed). Only ever called from the single worker thread.
    internal sealed class PowerPointSession
    {
        private const string PptFrameClass = "PPTFrameClass";
        private const int MsoFalse = 0;
        private const int MsoPlaceholder = 14;       // msoShapeType.msoPlaceholder
        private const int PpViewNormal = 9;
        private const int PpViewSlide = 1;
        private const int PpSelectionShapes = 2;
        private const int PpSelectionText = 3;
        private const int PpGuideHorizontal = 1;     // PpGuideOrientation
        private const int PpGuideVertical = 2;

        // How far outside the slide edge (in screen px) a marquee may start.
        private const double CanvasMarginPx = 160.0;
        // Clicks this close (px) to an already-selected shape are treated as a
        // resize/rotate handle grab and left alone.
        private const double HandleMarginPx = 32.0;
        // PowerPoint grabs shapes a few px around their outline (important for
        // 0-height/0-width straight lines whose exact AABB is unhittable).
        private const double HitPadPx = 4.0;
        // Grab tolerance around a straight line/connector segment.
        private const double LineGrabPx = 6.0;
        private const int MsoLine = 9;               // msoShapeType.msoLine
        private const int MsoConnectorStraight = 1;  // msoConnectorType
        // Clicks this close (px) to an alignment guide are guide drags, not marquees.
        private const double GuideMarginPx = 6.0;
        // If PowerPoint's own (contained-only) marquee result lands after ours it
        // overwrites our selection; wait this long, then re-apply once if needed.
        private const int ReassertDelayMs = 90;

        private object _app;

        #region Win32

        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        // Maps a point reported in a window's own (possibly DPI-virtualized)
        // coordinate space to physical pixels; identity when the window is
        // per-monitor DPI aware like this process. Windows 8.1+.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr hwnd, ref POINT point);

        private static bool _logicalToPhysicalAvailable = true;

        private static IntPtr WindowFromPointXY(int x, int y)
        {
            POINT p;
            p.X = x;
            p.Y = y;
            return WindowFromPoint(p);
        }

        private static bool ClassNameIs(IntPtr hwnd, string name)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            StringBuilder sb = new StringBuilder(128);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0)
                return false;
            return string.Equals(sb.ToString(), name, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        // Called on left-button-down. Returns a snapshot when the press starts a
        // marquee drag on the slide canvas of the active PowerPoint window, or
        // null when we should not interfere (click on a shape, other app, other
        // pane, other view, handle grab, guide drag, ...).
        public SlideSnapshot TryBeginDrag(int screenX, int screenY)
        {
            IntPtr hwndAtPoint = WindowFromPointXY(screenX, screenY);
            IntPtr root = GetAncestor(hwndAtPoint, GA_ROOT);
            if (root == IntPtr.Zero || !ClassNameIs(root, PptFrameClass))
            {
                ReleaseIfPowerPointGone();
                return null;
            }

            // A click that is only now activating the PowerPoint window would race
            // against ActiveWindow/Selection state; skip that first drag.
            if (GetForegroundWindow() != root)
                return null;

            dynamic app = GetApp();
            if (app == null)
                return null;

            try
            {
                dynamic win = app.ActiveWindow;
                int viewType = Convert.ToInt32(win.ViewType);
                if (viewType != PpViewNormal && viewType != PpViewSlide)
                    return null;

                double sx, sy, ox, oy;
                if (!TryGetMapping(win, root, out sx, out sy, out ox, out oy))
                    return null;

                double ptX = (screenX - ox) / sx;
                double ptY = (screenY - oy) / sy;

                dynamic pres = win.Presentation;
                double slideW = Convert.ToDouble(pres.PageSetup.SlideWidth);
                double slideH = Convert.ToDouble(pres.PageSetup.SlideHeight);

                // The mapped slide must overlap the real (physical) window rect;
                // if not, the mapping is garbage (e.g. unconvertible DPI setup) -
                // doing nothing beats selecting the wrong shapes.
                RECT windowRect;
                if (GetWindowRect(root, out windowRect))
                {
                    RectPt slidePx = RectPt.FromCorners(ox, oy, ox + slideW * sx, oy + slideH * sy);
                    RectPt winPx = RectPt.FromCorners(windowRect.Left, windowRect.Top,
                        windowRect.Right, windowRect.Bottom);
                    if (!slidePx.Intersects(winPx))
                        return null;
                }

                // The drag must start on the slide or in the gray border right
                // around it - not in the ribbon or far-away panes. (Drags that do
                // start in the thumbnail/notes pane are also rejected at release
                // time via the active-pane check in CompleteDrag.)
                RectPt slideRect = RectPt.FromCorners(0.0, 0.0, slideW, slideH);
                double canvasMarginPt = CanvasMarginPx / Math.Abs(sx);
                if (!slideRect.Expand(canvasMarginPt).Contains(ptX, ptY))
                    return null;

                // Dragging an alignment guide must not be mistaken for a marquee.
                if (IsNearGuide(pres, ptX, ptY, GuideMarginPx / Math.Abs(sx)))
                    return null;

                dynamic slide = win.View.Slide;
                int slideIndex = Convert.ToInt32(slide.SlideIndex);

                int selectionType;
                HashSet<int> selectedIds = GetSelectedShapeIds(win, out selectionType);
                bool textEditing = selectionType == PpSelectionText;
                double handleMarginPt = HandleMarginPx / Math.Abs(sx);
                double hitPadPt = HitPadPx / Math.Abs(sx);
                double lineGrabPt = LineGrabPx / Math.Abs(sx);

                dynamic shapes = slide.Shapes;
                int count = Convert.ToInt32(shapes.Count);
                List<ShapeInfo> list = new List<ShapeInfo>(count);

                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic s = shapes[i];
                        if (Convert.ToInt32(s.Visible) == MsoFalse)
                            continue;

                        double left = Convert.ToDouble(s.Left);
                        double top = Convert.ToDouble(s.Top);
                        double width = Convert.ToDouble(s.Width);
                        double height = Convert.ToDouble(s.Height);
                        double rotation = 0.0;
                        try { rotation = Convert.ToDouble(s.Rotation); }
                        catch { }

                        ShapeInfo info = new ShapeInfo();
                        info.Index = i;
                        info.Id = Convert.ToInt32(s.Id);
                        info.Bounds = RectPt.RotatedAabb(left, top, width, height, rotation);
                        info.WasSelected = selectedIds.Contains(info.Id);
                        info.IntersectSelectable = !IsEmptyPlaceholder(s);

                        // Straight lines/connectors get real segment geometry: their
                        // AABB is a huge, mostly-empty rectangle.
                        if (rotation == 0.0 && IsStraightLine(s))
                        {
                            bool hFlip = false;
                            bool vFlip = false;
                            try { hFlip = Convert.ToInt32(s.HorizontalFlip) != MsoFalse; }
                            catch { }
                            try { vFlip = Convert.ToInt32(s.VerticalFlip) != MsoFalse; }
                            catch { }
                            info.IsSegment = true;
                            // Flips pick which frame diagonal the line occupies.
                            if (hFlip == vFlip)
                            {
                                info.SegX0 = left; info.SegY0 = top;
                                info.SegX1 = left + width; info.SegY1 = top + height;
                            }
                            else
                            {
                                info.SegX0 = left + width; info.SegY0 = top;
                                info.SegX1 = left; info.SegY1 = top + height;
                            }
                        }

                        // While editing text with autofit off, the text can render far
                        // below the frame; drags there select text, not a marquee.
                        if (textEditing && info.WasSelected && rotation == 0.0)
                        {
                            try
                            {
                                double textH = Convert.ToDouble(s.TextFrame.TextRange.BoundHeight);
                                double extBottom = top + Math.Max(height, textH);
                                if (extBottom > info.Bounds.Bottom)
                                    info.Bounds = RectPt.FromCorners(info.Bounds.X, info.Bounds.Y,
                                        info.Bounds.Right, extBottom);
                            }
                            catch
                            {
                            }
                        }

                        // Press on (or within grab tolerance of) a shape: PowerPoint
                        // will move/select it - not a marquee. Prompt-only placeholders
                        // are transparent to marquees unless currently selected.
                        if (info.IntersectSelectable || info.WasSelected)
                        {
                            bool pressOnShape = info.IsSegment
                                ? RectPt.DistancePointToSegment(ptX, ptY,
                                      info.SegX0, info.SegY0, info.SegX1, info.SegY1) <= lineGrabPt
                                : info.Bounds.Expand(hitPadPt).Contains(ptX, ptY);
                            if (pressOnShape)
                                return null;
                        }
                        // Press right next to a selected shape: likely a handle grab.
                        if (info.WasSelected)
                        {
                            bool nearHandle = info.IsSegment
                                ? RectPt.DistancePointToSegment(ptX, ptY,
                                      info.SegX0, info.SegY0, info.SegX1, info.SegY1) <= handleMarginPt
                                : info.Bounds.Expand(handleMarginPt).Contains(ptX, ptY);
                            if (nearHandle)
                                return null;
                        }

                        list.Add(info);
                    }
                    catch
                    {
                        // Late binding can throw COMException or binder errors on
                        // exotic shape types; skip those shapes, keep the rest.
                    }
                }

                SlideSnapshot snapshot = new SlideSnapshot();
                snapshot.Shapes = list;
                snapshot.DownPtX = ptX;
                snapshot.DownPtY = ptY;
                snapshot.SlideIndex = slideIndex;
                snapshot.TotalShapeCount = count;
                snapshot.PresentationName = Convert.ToString(pres.Name);
                return snapshot;
            }
            catch (COMException)
            {
                InvalidateIfDead();
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // Called on left-button-up after a real drag. Selects every snapshot shape
        // whose bounds intersect the marquee rectangle (Figma semantics), instead
        // of PowerPoint's fully-contained-only rule.
        public void CompleteDrag(SlideSnapshot snapshot, int upX, int upY, bool shift)
        {
            IntPtr foreground = GetForegroundWindow();
            if (!ClassNameIs(foreground, PptFrameClass))
                return;

            dynamic app = GetApp();
            if (app == null)
                return;

            try
            {
                dynamic win = app.ActiveWindow;
                int viewType = Convert.ToInt32(win.ViewType);
                if (viewType != PpViewNormal && viewType != PpViewSlide)
                    return;

                // By now PowerPoint has processed the click, so a drag that really
                // happened in the thumbnail strip or notes pane has activated that
                // pane - reject it.
                try
                {
                    if (Convert.ToInt32(win.ActivePane.ViewType) != PpViewSlide)
                        return;
                }
                catch
                {
                }

                if (!string.Equals(Convert.ToString(win.Presentation.Name),
                        snapshot.PresentationName, StringComparison.Ordinal))
                    return;

                dynamic slide = win.View.Slide;
                if (Convert.ToInt32(slide.SlideIndex) != snapshot.SlideIndex)
                    return;

                dynamic shapes = slide.Shapes;
                int count = Convert.ToInt32(shapes.Count);
                // Shape count changed: the drag DREW something (shape tool, text
                // box, ink) or shapes were added/removed mid-drag. Not a marquee.
                if (count != snapshot.TotalShapeCount)
                    return;

                // Everything PowerPoint itself ended up selecting must be a shape
                // we knew about at mouse-down; otherwise the drag was something
                // else entirely (e.g. a fast shape move that outran our snapshot).
                HashSet<int> knownIds = new HashSet<int>();
                foreach (ShapeInfo s in snapshot.Shapes)
                    knownIds.Add(s.Id);
                int selectionType;
                foreach (int id in GetSelectedShapeIds(win, out selectionType))
                {
                    if (!knownIds.Contains(id))
                        return;
                }

                // Recompute the mapping: the view may have auto-scrolled while the
                // marquee ran. The anchor stays where it was in slide space, the
                // release point is converted with the fresh mapping.
                double sx, sy, ox, oy;
                if (!TryGetMapping(win, foreground, out sx, out sy, out ox, out oy))
                    return;
                double upPtX = (upX - ox) / sx;
                double upPtY = (upY - oy) / sy;

                RectPt band = RectPt.FromCorners(snapshot.DownPtX, snapshot.DownPtY, upPtX, upPtY);

                List<ShapeInfo> hits = new List<ShapeInfo>();
                foreach (ShapeInfo s in snapshot.Shapes)
                {
                    bool touched = s.IsSegment
                        ? band.IntersectsSegment(s.SegX0, s.SegY0, s.SegX1, s.SegY1)
                        : s.Bounds.Intersects(band);
                    if ((s.IntersectSelectable && touched) || (shift && s.WasSelected))
                        hits.Add(s);
                }
                if (hits.Count == 0)
                    return; // nothing touched: PowerPoint's native result stands

                object[] indexes = new object[hits.Count];
                HashSet<int> desiredIds = new HashSet<int>();
                for (int i = 0; i < hits.Count; i++)
                {
                    ShapeInfo s = hits[i];
                    // Stale index (shape replaced under us): do not select wrongly.
                    if (s.Index > count || Convert.ToInt32(shapes[s.Index].Id) != s.Id)
                        return;
                    indexes[i] = s.Index;
                    desiredIds.Add(s.Id);
                }

                shapes.Range(indexes).Select();

                // If PowerPoint was still finishing its own marquee handling, its
                // native (contained-only) result can land after ours and overwrite
                // it. That case shows up as the selection shrinking to a strict
                // subset of what we just set - apply once more.
                Thread.Sleep(ReassertDelayMs);
                HashSet<int> after = GetSelectedShapeIds(win, out selectionType);
                if (after.Count != desiredIds.Count && after.IsSubsetOf(desiredIds))
                    shapes.Range(indexes).Select();
            }
            catch (COMException)
            {
                InvalidateIfDead();
            }
            catch (InvalidCastException)
            {
            }
            catch (FormatException)
            {
            }
        }

        private dynamic GetApp()
        {
            if (_app != null)
            {
                try
                {
                    dynamic cached = _app;
                    string name = Convert.ToString(cached.Name); // liveness probe
                    if (name != null)
                        return _app;
                }
                catch
                {
                    Invalidate();
                }
            }

            try
            {
                _app = Marshal.GetActiveObject("PowerPoint.Application");
                return _app;
            }
            catch
            {
                _app = null;
                return null;
            }
        }

        // Screen px = offset + slide points * scale, sampled from the live window.
        // PointsToScreenPixels answers in POWERPOINT'S DPI-awareness context; when
        // PowerPoint runs DPI-virtualized (MSI Office 2016, "optimize for
        // compatibility" mode) those are not physical pixels, so both sample
        // points are pushed through LogicalToPhysicalPointForPerMonitorDPI to
        // line up with the physical coordinates our hook delivers.
        private static bool TryGetMapping(dynamic win, IntPtr rootHwnd, out double sx, out double sy, out double ox, out double oy)
        {
            sx = 1.0;
            sy = 1.0;
            ox = 0.0;
            oy = 0.0;

            POINT p0;
            p0.X = Convert.ToInt32(win.PointsToScreenPixelsX(0.0f));
            p0.Y = Convert.ToInt32(win.PointsToScreenPixelsY(0.0f));
            POINT p1;
            p1.X = Convert.ToInt32(win.PointsToScreenPixelsX(1000.0f));
            p1.Y = Convert.ToInt32(win.PointsToScreenPixelsY(1000.0f));

            if (rootHwnd != IntPtr.Zero && _logicalToPhysicalAvailable)
            {
                try
                {
                    POINT c0 = p0;
                    POINT c1 = p1;
                    if (LogicalToPhysicalPointForPerMonitorDPI(rootHwnd, ref c0) &&
                        LogicalToPhysicalPointForPerMonitorDPI(rootHwnd, ref c1) &&
                        c1.X != c0.X && c1.Y != c0.Y)
                    {
                        p0 = c0;
                        p1 = c1;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    _logicalToPhysicalAvailable = false; // pre-8.1 Windows
                }
            }

            sx = (p1.X - p0.X) / 1000.0;
            sy = (p1.Y - p0.Y) / 1000.0;
            ox = p0.X;
            oy = p0.Y;

            // ~1.33 px/pt at 100% zoom on 96 DPI; anything near zero means the
            // window is minimized or the answer is garbage.
            return Math.Abs(sx) > 0.01 && Math.Abs(sy) > 0.01;
        }

        private static bool IsNearGuide(dynamic pres, double ptX, double ptY, double tolerancePt)
        {
            try
            {
                dynamic guides = pres.Guides;
                int count = Convert.ToInt32(guides.Count);
                for (int i = 1; i <= count; i++)
                {
                    dynamic guide = guides[i];
                    int orientation = Convert.ToInt32(guide.Orientation);
                    double position = Convert.ToDouble(guide.Position);
                    if (orientation == PpGuideHorizontal && Math.Abs(ptY - position) <= tolerancePt)
                        return true;
                    if (orientation == PpGuideVertical && Math.Abs(ptX - position) <= tolerancePt)
                        return true;
                }
            }
            catch
            {
                // Guides API not available (older PowerPoint) - nothing to check.
            }
            return false;
        }

        // Selected shape Ids, normalized to top-level shapes: a selection inside a
        // group (or text edited within a grouped shape) reports the child shape,
        // whose Id never appears in Slide.Shapes.
        private static HashSet<int> GetSelectedShapeIds(dynamic win, out int selectionType)
        {
            HashSet<int> ids = new HashSet<int>();
            selectionType = 0;
            try
            {
                dynamic sel = win.Selection;
                int type = Convert.ToInt32(sel.Type);
                selectionType = type;
                if (type == PpSelectionShapes || type == PpSelectionText)
                {
                    dynamic range = sel.ShapeRange;
                    int count = Convert.ToInt32(range.Count);
                    for (int i = 1; i <= count; i++)
                    {
                        try { ids.Add(TopLevelShapeId(range[i])); }
                        catch { }
                    }
                }
            }
            catch
            {
            }
            return ids;
        }

        private static int TopLevelShapeId(dynamic shape)
        {
            dynamic current = shape;
            for (int depth = 0; depth < 8; depth++)
            {
                dynamic parent;
                try { parent = current.ParentGroup; } // throws for top-level shapes
                catch { break; }
                if (parent == null)
                    break;
                current = parent;
            }
            return Convert.ToInt32(current.Id);
        }

        // Straight line, or connector whose ConnectorFormat says straight. Elbow
        // and curved connectors fall back to the AABB path.
        private static bool IsStraightLine(dynamic shape)
        {
            try
            {
                if (Convert.ToInt32(shape.Type) == MsoLine)
                    return true;
                if (Convert.ToInt32(shape.Connector) != MsoFalse &&
                    Convert.ToInt32(shape.ConnectorFormat.Type) == MsoConnectorStraight)
                    return true;
            }
            catch
            {
            }
            return false;
        }

        // Prompt-only placeholders ("Click to add title") cover big slide areas
        // but render as nothing; Figma-style select should pass through them,
        // and PowerPoint's own marquee lets drags start on them too.
        private static bool IsEmptyPlaceholder(dynamic shape)
        {
            try
            {
                if (Convert.ToInt32(shape.Type) != MsoPlaceholder)
                    return false;
                try
                {
                    if (Convert.ToInt32(shape.PlaceholderFormat.ContainedType) != MsoPlaceholder)
                        return false; // holds a picture/table/chart - real content
                }
                catch
                {
                }
                try
                {
                    if (Convert.ToInt32(shape.TextFrame.HasText) != MsoFalse)
                        return false;
                }
                catch
                {
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // When the user clicks outside PowerPoint and no PowerPoint window exists
        // anymore, drop the cached COM reference so a closed PowerPoint process
        // is not kept alive by our RCW.
        private void ReleaseIfPowerPointGone()
        {
            if (_app == null)
                return;
            if (FindWindow(PptFrameClass, null) == IntPtr.Zero)
                Invalidate();
        }

        // A COMException may just mean "PowerPoint is busy right now"; only tear
        // the cached reference down (with its GC cost) when the app really died.
        public void InvalidateIfDead()
        {
            if (_app == null)
                return;
            try
            {
                dynamic probe = _app;
                Convert.ToString(probe.Name);
            }
            catch
            {
                Invalidate();
            }
        }

        public void Invalidate()
        {
            if (_app != null)
            {
                try { Marshal.FinalReleaseComObject(_app); }
                catch { }
                _app = null;
            }
            // Flush RCWs created by dynamic dispatch so PowerPoint can fully exit.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
