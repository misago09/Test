using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PptFigmaDrag
{
    // One-shot self-diagnosis shown as a copyable message box. v2 measures the
    // viewport over COM before/after each injected gesture, so recognition of
    // pan/pinch and the real zoom anchor are established with numbers instead of
    // the user's eyes. It temporarily forces 200% zoom so pan probes always have
    // somewhere to move, and restores everything afterwards.
    internal static class Diagnostics
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO
        {
            public uint Size;
            public uint Mask;
            public int Min;
            public int Max;
            public uint Page;
            public int Pos;
            public int TrackPos;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        private const int SM_REMOTESESSION = 0x1000;
        private const uint GA_ROOT = 2;
        private const uint WM_VSCROLL = 0x0115;
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;
        private const uint SMTO_ABORTIFHUNG = 2;
        private const string PptFrameClass = "PPTFrameClass";
        private const string SlideCanvasClass = "mdiClass";

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);

        private const int SB_VERT = 1;
        private const uint SIF_ALL = 0x17;

        private sealed class Vp
        {
            public double Ox, Oy, Sx, Sy;
            public int Zoom;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "(null)";
            StringBuilder sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0)
                return "(unknown)";
            return sb.ToString();
        }

        private static Vp ReadVp(PowerPointSession session)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                double ox, oy, sx, sy, w, h;
                int zoom, slide;
                if (session.TryGetViewport(out ox, out oy, out sx, out sy, out w, out h,
                        out zoom, out slide))
                {
                    Vp v = new Vp();
                    v.Ox = ox;
                    v.Oy = oy;
                    v.Sx = sx;
                    v.Sy = sy;
                    v.Zoom = zoom;
                    return v;
                }
                Thread.Sleep(120);
            }
            return null;
        }

        private static double Dist(double ax, double ay, double bx, double by)
        {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Step trail so a COM hang can be located: only one diagnostic runs at a
        // time (TrayContext._diagRunning), so plain reassignment is safe.
        private static volatile string _progress = "";

        private static void Step(string name)
        {
            _progress = _progress + " → " + name;
        }

        // Runs the measurement on its own STA thread with a hard timeout, so a
        // wedged COM call to PowerPoint can never make the diagnostic vanish
        // without a trace. Also persists the report next to the viewport log.
        public static string RunWithTimeout(GestureEngine engine, MouseHook hook, int timeoutMs)
        {
            _progress = "(시작)";
            string[] result = new string[1];
            Thread inner = new Thread(delegate()
            {
                try { result[0] = Run(engine, hook); }
                catch (Exception ex) { result[0] = "진단 중 오류: " + ex; }
            });
            inner.SetApartmentState(ApartmentState.STA);
            inner.IsBackground = true;
            inner.Name = "PptFigmaDrag.DiagMeasure";
            inner.Start();

            string report;
            if (!inner.Join(timeoutMs))
            {
                report = "⚠ 진단이 " + (timeoutMs / 1000) + "초 안에 끝나지 않아 중단했습니다.\r\n" +
                         "PowerPoint COM 호출이 응답하지 않는 것으로 보입니다.\r\n" +
                         "마지막으로 진행된 단계:\r\n" + _progress + "  ← 여기서 멈춤";
            }
            else
            {
                report = result[0];
            }

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ReportPath));
                System.IO.File.WriteAllText(ReportPath, report);
            }
            catch
            {
            }
            return report;
        }

        public static string ReportPath
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PptFigmaDrag", "diag-report.txt");
            }
        }

        public static string Run(GestureEngine engine, MouseHook hook)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder r = new StringBuilder();
            r.Append("=== PPT Figma Drag 진단 v2 (자동 측정) ===\r\n");
            r.Append("OS 버전: ").Append(Environment.OSVersion.Version.ToString())
             .Append(Environment.Is64BitProcess ? "  (64-bit)" : "  (32-bit)").Append("\r\n");
            bool remote = GetSystemMetrics(SM_REMOTESESSION) != 0;
            r.Append("실행 환경: ").Append(remote ? "원격 세션 ⚠" : "로컬 콘솔").Append("\r\n");
            r.Append("터치 주입 초기화: ").Append(engine.Ready ? "성공 ✓" : "실패 ✗").Append("\r\n\r\n");

            r.Append("훅 처리 횟수(앱 시작 후): ").Append(hook.DiagCounters).Append("\r\n");
            r.Append("마지막 휠 판정: ").Append(hook.LastWheelGate).Append("\r\n");
            r.Append("마지막 가운데버튼 판정: ").Append(hook.LastMiddleGate).Append("\r\n");
            r.Append("마지막 휠 제스처 이동 가능량: ").Append(engine.LastRoomsInfo).Append("\r\n\r\n");

            POINT p;
            GetCursorPos(out p);
            r.Append("마우스 위치: (").Append(p.X).Append(", ").Append(p.Y).Append(")\r\n");

            IntPtr leaf = WindowFromPoint(p);
            IntPtr canvasHwnd = IntPtr.Zero;
            IntPtr walker = leaf;
            for (int i = 0; i < 8 && walker != IntPtr.Zero; i++)
            {
                if (string.Equals(ClassOf(walker), SlideCanvasClass, StringComparison.OrdinalIgnoreCase))
                {
                    canvasHwnd = walker;
                    break;
                }
                walker = GetParent(walker);
            }
            IntPtr root = GetAncestor(leaf, GA_ROOT);
            bool rootIsPpt = string.Equals(ClassOf(root), PptFrameClass, StringComparison.OrdinalIgnoreCase);
            r.Append("캔버스 인식: ").Append(canvasHwnd != IntPtr.Zero && rootIsPpt ? "충족 ✓" : "불충족 ✗")
             .Append("  (커서 아래: ").Append(ClassOf(leaf)).Append(" / 최상위: ").Append(ClassOf(root)).Append(")\r\n");

            if (!engine.Ready)
            {
                r.Append("\r\n터치 주입이 불가능한 환경입니다. 자동 측정을 건너뜁니다.\r\n");
                return r.ToString();
            }
            if (canvasHwnd == IntPtr.Zero || !rootIsPpt)
            {
                r.Append("\r\n마우스를 PowerPoint 슬라이드 중앙에 두고 다시 실행하세요.\r\n");
                return r.ToString();
            }

            SetForegroundWindow(root);
            Thread.Sleep(200);

            r.Append("\r\n=== 자동 측정 (화면이 몇 초간 움직이는 것은 정상입니다) ===\r\n");
            Step("뷰포트 읽기");
            PowerPointSession session = new PowerPointSession();
            Vp v0 = ReadVp(session);
            if (v0 == null)
            {
                r.Append("PowerPoint 뷰포트를 읽지 못해 자동 측정을 건너뜁니다.\r\n");
                return r.ToString();
            }
            r.Append("현재 배율: ").Append(v0.Zoom).Append("% → 측정 위해 임시로 200% 설정\r\n");

            // v0.Zoom can be 0 when the Zoom read failed; never "restore" to 0%.
            int zoom0 = v0.Zoom;
            Step("배율 200% 설정");
            bool zoomForced = zoom0 > 0 && session.TrySetZoom(200);
            if (zoomForced)
                Thread.Sleep(350);

            try
            {
                // 1) Two-finger parallel pan: the mechanism behind middle-drag & wheel.
                Step("두 손가락 팬 측정");
                double dPan2 = MeasurePan(engine, session, GestureEngine.ProbeKindPan2,
                    p.X, p.Y, r, "두 손가락 팬", inv);

                // 2) Single-finger pan, only when two-finger showed nothing and the
                //    cursor is over empty canvas (else it would drag a shape).
                if (double.IsNaN(dPan2) || Math.Abs(dPan2) < 20.0)
                {
                    Step("한 손가락 팬 측정");
                    if (session.TryBeginDrag(p.X, p.Y) != null)
                        MeasurePan(engine, session, GestureEngine.ProbeKindPan1,
                            p.X, p.Y, r, "한 손가락 팬", inv);
                    else
                        r.Append("한 손가락 팬: 건너뜀 (커서 아래가 빈 캔버스가 아님)\r\n");
                }

                // 3) Raw pinch (no compensation): where does PowerPoint anchor zoom?
                Step("핀치 앵커 측정");
                MeasurePinch(engine, session, p.X, p.Y, canvasHwnd, r, inv);

                // 4) Classic scrollbar messages as a pan fallback candidate.
                Step("스크롤바 테스트");
                MeasureScroll(session, canvasHwnd, r, inv);
            }
            catch (Exception ex)
            {
                r.Append("측정 중 오류: ").Append(ex.Message).Append("\r\n");
            }
            finally
            {
                Step("배율 복원");
                if (zoomForced)
                    session.TrySetZoom(zoom0);
                Step("완료");
            }

            string probe = engine.LastProbeReport;
            if (probe != null)
            {
                r.Append("\r\n주입 파라미터 프로브:\r\n").Append(probe).Append("\r\n");
            }

            r.Append("\r\n(이 창에서 Ctrl+C를 누르면 전체 내용이 복사됩니다.)");
            return r.ToString();
        }

        private static double MeasurePan(GestureEngine engine, PowerPointSession session,
            int kind, int x, int y, StringBuilder r, string label, CultureInfo inv)
        {
            Vp before = ReadVp(session);
            bool injected = engine.RunProbe(kind, x, y, 150.0);
            Thread.Sleep(450);
            Vp after = ReadVp(session);
            engine.RunProbe(kind, x, y, -150.0); // put the view back
            Thread.Sleep(250);

            if (!injected)
            {
                r.Append(label).Append(": 주입 실패 (err=").Append(engine.LastInjectError).Append(")\r\n");
                return double.NaN;
            }
            if (before == null || after == null)
            {
                r.Append(label).Append(": 측정 실패 (뷰포트 읽기 불가)\r\n");
                return double.NaN;
            }
            double d = after.Ox - before.Ox;
            r.Append(label).Append(": 손가락 +150px → 화면 ")
             .Append(d.ToString("F0", inv)).Append("px ")
             .Append(Math.Abs(d) >= 20.0 ? "이동 → 인식됨 ✓" : "이동 → 인식 안 됨 ✗").Append("\r\n");
            return d;
        }

        private static void MeasurePinch(GestureEngine engine, PowerPointSession session,
            int x, int y, IntPtr canvasHwnd, StringBuilder r, CultureInfo inv)
        {
            Vp before = ReadVp(session);
            bool injected = engine.RunProbe(GestureEngine.ProbeKindPinch, x, y, 1.5);
            Thread.Sleep(550);
            Vp after = ReadVp(session);
            engine.RunProbe(GestureEngine.ProbeKindPinch, x, y, 1.0 / 1.5);
            Thread.Sleep(300);

            if (!injected || before == null || after == null)
            {
                r.Append("핀치 줌: 측정 실패\r\n");
                return;
            }
            r.Append("핀치 줌(보정 없음): 배율 ").Append(before.Zoom).Append("% → ").Append(after.Zoom).Append("%");
            if (after.Zoom == before.Zoom)
            {
                r.Append("  → 줌 반응 없음 ✗\r\n");
                return;
            }

            // Whichever screen point kept its slide coordinate is the real anchor.
            double curBx = (x - before.Ox) / before.Sx;
            double curBy = (y - before.Oy) / before.Sy;
            double curAx = (x - after.Ox) / after.Sx;
            double curAy = (y - after.Oy) / after.Sy;
            double dCursor = Dist(curBx, curBy, curAx, curAy);

            double dCenter = double.NaN;
            RECT rc;
            if (GetWindowRect(canvasHwnd, out rc))
            {
                double ccx = (rc.Left + rc.Right) / 2.0;
                double ccy = (rc.Top + rc.Bottom) / 2.0;
                double cenBx = (ccx - before.Ox) / before.Sx;
                double cenBy = (ccy - before.Oy) / before.Sy;
                double cenAx = (ccx - after.Ox) / after.Sx;
                double cenAy = (ccy - after.Oy) / after.Sy;
                dCenter = Dist(cenBx, cenBy, cenAx, cenAy);
            }

            r.Append("\r\n   앵커 측정: 커서점 이동 ").Append(dCursor.ToString("F1", inv))
             .Append("pt / 화면중앙점 이동 ")
             .Append(double.IsNaN(dCenter) ? "?" : dCenter.ToString("F1", inv))
             .Append("pt → ");
            if (!double.IsNaN(dCenter) && dCenter < dCursor * 0.5)
                r.Append("중앙 기준 줌 (보정 필요, 이미 적용됨)\r\n");
            else if (!double.IsNaN(dCenter) && dCursor < dCenter * 0.5)
                r.Append("커서 기준 줌\r\n");
            else
                r.Append("불명확\r\n");
        }

        private static void MeasureScroll(PowerPointSession session, IntPtr canvasHwnd,
            StringBuilder r, CultureInfo inv)
        {
            SCROLLINFO si = new SCROLLINFO();
            si.Size = (uint)Marshal.SizeOf(typeof(SCROLLINFO));
            si.Mask = SIF_ALL;
            bool has = GetScrollInfo(canvasHwnd, SB_VERT, ref si);
            r.Append("스크롤바 정보: ")
             .Append(has
                 ? "있음 (pos=" + si.Pos + ", range=" + si.Min + ".." + si.Max + ", page=" + si.Page + ")"
                 : "없음")
             .Append("\r\n");

            Vp before = ReadVp(session);
            IntPtr result;
            for (int i = 0; i < 3; i++)
                SendMessageTimeout(canvasHwnd, WM_VSCROLL, (IntPtr)SB_LINEDOWN, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 500, out result);
            Thread.Sleep(350);
            Vp after = ReadVp(session);
            for (int i = 0; i < 3; i++)
                SendMessageTimeout(canvasHwnd, WM_VSCROLL, (IntPtr)SB_LINEUP, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 500, out result);

            if (before == null || after == null)
            {
                r.Append("WM_VSCROLL 테스트: 측정 실패\r\n");
                return;
            }
            double d = after.Oy - before.Oy;
            r.Append("WM_VSCROLL(라인 x3): 화면 ").Append(d.ToString("F0", inv)).Append("px ")
             .Append(Math.Abs(d) >= 5.0 ? "이동 → 동작함 ✓" : "이동 → 반응 없음 ✗").Append("\r\n");
        }
    }
}
