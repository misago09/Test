using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PptFigmaDrag
{
    internal sealed class ShapeInfo
    {
        public int Index;        // 1-based index in Slide.Shapes at snapshot time
        public int Id;           // Shape.Id, used to detect stale indexes
        public RectPt Bounds;    // rotation-aware AABB in slide points
        public bool WasSelected; // selected before the drag started
    }

    internal sealed class SlideSnapshot
    {
        public List<ShapeInfo> Shapes;
        public double DownPtX; // mouse-down position in slide points
        public double DownPtY;
        public int SlideIndex;
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

        // How far outside the slide edge (in screen px) a marquee may start.
        private const double CanvasMarginPx = 160.0;
        // Clicks this close (px) to an already-selected shape are treated as a
        // resize/rotate handle grab and left alone.
        private const double HandleMarginPx = 32.0;

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
        // pane, other view, handle grab, ...).
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
                if (!TryGetMapping(win, out sx, out sy, out ox, out oy))
                    return null;

                double ptX = (screenX - ox) / sx;
                double ptY = (screenY - oy) / sy;

                dynamic pres = win.Presentation;
                double slideW = Convert.ToDouble(pres.PageSetup.SlideWidth);
                double slideH = Convert.ToDouble(pres.PageSetup.SlideHeight);

                // The drag must start on the slide or in the gray border right
                // around it - not in the ribbon, thumbnails or notes.
                RectPt slideRect = RectPt.FromCorners(0.0, 0.0, slideW, slideH);
                double canvasMarginPt = CanvasMarginPx / Math.Abs(sx);
                if (!slideRect.Expand(canvasMarginPt).Contains(ptX, ptY))
                    return null;

                // The press must land on the same child window that hosts the
                // slide canvas (filters out sibling panes that overlap the
                // expanded rect at high zoom-out).
                int centerX = (int)Math.Round(ox + (slideW / 2.0) * sx);
                int centerY = (int)Math.Round(oy + (slideH / 2.0) * sy);
                IntPtr slidePane = WindowFromPointXY(centerX, centerY);
                if (slidePane != IntPtr.Zero && hwndAtPoint != slidePane)
                    return null;

                dynamic slide = win.View.Slide;
                int slideIndex = Convert.ToInt32(slide.SlideIndex);

                HashSet<int> selectedIds = GetSelectedShapeIds(win);
                double handleMarginPt = HandleMarginPx / Math.Abs(sx);

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
                        if (IsEmptyPlaceholder(s))
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

                        // Press on a shape: PowerPoint will move/select it - not a marquee.
                        if (info.Bounds.Contains(ptX, ptY))
                            return null;
                        // Press right next to a selected shape: likely a handle grab.
                        if (info.WasSelected && info.Bounds.Expand(handleMarginPt).Contains(ptX, ptY))
                            return null;

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
                return snapshot;
            }
            catch (COMException)
            {
                Invalidate();
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

                dynamic slide = win.View.Slide;
                if (Convert.ToInt32(slide.SlideIndex) != snapshot.SlideIndex)
                    return;

                // Recompute the mapping: the view may have auto-scrolled while the
                // marquee ran. The anchor stays where it was in slide space, the
                // release point is converted with the fresh mapping.
                double sx, sy, ox, oy;
                if (!TryGetMapping(win, out sx, out sy, out ox, out oy))
                    return;
                double upPtX = (upX - ox) / sx;
                double upPtY = (upY - oy) / sy;

                RectPt band = RectPt.FromCorners(snapshot.DownPtX, snapshot.DownPtY, upPtX, upPtY);

                List<ShapeInfo> hits = new List<ShapeInfo>();
                foreach (ShapeInfo s in snapshot.Shapes)
                {
                    if (s.Bounds.Intersects(band) || (shift && s.WasSelected))
                        hits.Add(s);
                }
                if (hits.Count == 0)
                    return; // nothing touched: PowerPoint's native result stands

                dynamic shapes = slide.Shapes;
                int count = Convert.ToInt32(shapes.Count);
                object[] indexes = new object[hits.Count];
                for (int i = 0; i < hits.Count; i++)
                {
                    ShapeInfo s = hits[i];
                    // The slide changed under us (shape added/removed mid-drag):
                    // indexes are stale, do not select the wrong shapes.
                    if (s.Index > count || Convert.ToInt32(shapes[s.Index].Id) != s.Id)
                        return;
                    indexes[i] = s.Index;
                }

                shapes.Range(indexes).Select();
            }
            catch (COMException)
            {
                Invalidate();
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
        private static bool TryGetMapping(dynamic win, out double sx, out double sy, out double ox, out double oy)
        {
            sx = 1.0;
            sy = 1.0;
            ox = 0.0;
            oy = 0.0;

            int x0 = Convert.ToInt32(win.PointsToScreenPixelsX(0.0f));
            int x1 = Convert.ToInt32(win.PointsToScreenPixelsX(1000.0f));
            int y0 = Convert.ToInt32(win.PointsToScreenPixelsY(0.0f));
            int y1 = Convert.ToInt32(win.PointsToScreenPixelsY(1000.0f));

            sx = (x1 - x0) / 1000.0;
            sy = (y1 - y0) / 1000.0;
            ox = x0;
            oy = y0;

            // ~1.33 px/pt at 100% zoom on 96 DPI; anything near zero means the
            // window is minimized or the answer is garbage.
            return Math.Abs(sx) > 0.01 && Math.Abs(sy) > 0.01;
        }

        private static HashSet<int> GetSelectedShapeIds(dynamic win)
        {
            HashSet<int> ids = new HashSet<int>();
            try
            {
                dynamic sel = win.Selection;
                int type = Convert.ToInt32(sel.Type);
                if (type == PpSelectionShapes || type == PpSelectionText)
                {
                    dynamic range = sel.ShapeRange;
                    int count = Convert.ToInt32(range.Count);
                    for (int i = 1; i <= count; i++)
                    {
                        try { ids.Add(Convert.ToInt32(range[i].Id)); }
                        catch { }
                    }
                }
            }
            catch
            {
            }
            return ids;
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
