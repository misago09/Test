using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PptFigmaDrag
{
    internal enum MouseEventKind
    {
        Down,
        Up,
        ReleaseCom // not a mouse event: tells the worker to drop its COM references
    }

    internal struct MouseEvent
    {
        public MouseEventKind Kind;
        public int X;
        public int Y;
        public bool Shift;
        public bool Ctrl;
        public bool Alt;
    }

    // WH_MOUSE_LL global hook. System-wide pointer latency depends on how fast
    // this callback returns, so it must never touch COM, draw, or block:
    // - left down/up are copied into the marquee worker's queue;
    // - middle down/up and wheel over the PowerPoint slide canvas are consumed
    //   and forwarded to the gesture engine (pan/zoom);
    // - mouse moves take a single volatile check, plus a tiny forward while a
    //   middle-drag pan is active.
    // Events synthesized from touch (incl. our own injection) are left alone.
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        private const int VK_LBUTTON = 0x01;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private const uint LLMHF_INJECTED = 0x00000001;
        // Mouse events synthesized from touch/pen carry this signature in extra info.
        private const uint MI_WP_SIGNATURE = 0xFF515700;
        private const uint MI_WP_SIGNATURE_MASK = 0xFFFFFF00;

        private const uint GA_ROOT = 2;
        private const string PptFrameClass = "PPTFrameClass";
        private const string SlideCanvasClass = "mdiClass";

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

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
        private static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int PtX;
            public int PtY;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        // TickCount of the last real (non-synthesized) left-button press anywhere.
        // The slide guard uses it to avoid reverting deliberate user navigation.
        public static volatile int LastLeftClickTick;

        // Diagnostic gate trace: why the last wheel / middle-button event was or
        // was not taken over. Written on the hook thread, read by the diagnostic.
        private volatile int _cntWheelPan;
        private volatile int _cntWheelPinch;
        private volatile int _cntMiddle;
        private volatile string _lastWheelGate = "(휠 이벤트 없음)";
        private volatile string _lastMiddleGate = "(가운데 버튼 이벤트 없음)";

        public string DiagCounters
        {
            get
            {
                return "휠→팬 " + _cntWheelPan + "회, Ctrl+휠→줌 " + _cntWheelPinch +
                       "회, 가운데 드래그 " + _cntMiddle + "회";
            }
        }

        public string LastWheelGate
        {
            get { return _lastWheelGate; }
        }

        public string LastMiddleGate
        {
            get { return _lastMiddleGate; }
        }

        private readonly DragWorker _worker;
        private readonly GestureEngine _engine;
        private readonly HookProc _proc; // field keeps the delegate alive against GC
        private readonly StringBuilder _classBuffer = new StringBuilder(128);
        private IntPtr _hook;
        private volatile int _lastEventTick;
        private int _installCount;

        public int InstallCount
        {
            get { return _installCount; }
        }

        public int LastEventTick
        {
            get { return _lastEventTick; }
        }
        private volatile bool _enabled = true;
        private volatile bool _panZoomEnabled = true;
        private bool _middleCaptured;    // hook thread only
        private IntPtr _lastCanvasHwnd;  // hook thread only

        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        public bool PanZoomEnabled
        {
            get { return _panZoomEnabled; }
            set { _panZoomEnabled = value; }
        }

        public MouseHook(DragWorker worker, GestureEngine engine)
        {
            _worker = worker;
            _engine = engine;
            _proc = Callback;
        }

        // Must be called from a thread that pumps messages (the UI thread).
        public void Install()
        {
            if (_hook != IntPtr.Zero)
                return;

            IntPtr module = GetModuleHandle(null);
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, module, 0);
            if (_hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 훅 설치에 실패했습니다.");
            _installCount++;
            _lastEventTick = Environment.TickCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // Windows silently removes a low-level hook whose callback ever stalls
        // past the hook timeout - from then on every feature reverts to native
        // behavior with no error. Detect "the system saw input but we didn't"
        // and re-install. Must run on the thread that owns the hook.
        public void CheckHealthAndReinstall()
        {
            if (_hook == IntPtr.Zero)
                return;
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.Size = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info))
                return;
            int sinceInput = Environment.TickCount - (int)info.Time;
            int sinceHookEvent = Environment.TickCount - _lastEventTick;
            if (sinceInput < 500 && sinceHookEvent > 3000)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Install();
            }
        }

        private static bool IsSynthesized(ref MSLLHOOKSTRUCT data)
        {
            if ((data.Flags & LLMHF_INJECTED) != 0)
                return true;
            return IsTouchSynthesized(ref data);
        }

        // Only events that Windows synthesized from touch/pen (incl. our own
        // injection). Precision-touchpad drivers inject wheel events with
        // LLMHF_INJECTED but no touch signature - those are real user scrolling
        // and the wheel path must still take them over.
        private static bool IsTouchSynthesized(ref MSLLHOOKSTRUCT data)
        {
            ulong extra = (ulong)data.ExtraInfo.ToInt64();
            return ((uint)extra & MI_WP_SIGNATURE_MASK) == MI_WP_SIGNATURE;
        }

        private bool ClassNameIs(IntPtr hwnd, string name)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            _classBuffer.Length = 0;
            if (GetClassName(hwnd, _classBuffer, _classBuffer.Capacity) == 0)
                return false;
            return string.Equals(_classBuffer.ToString(), name, StringComparison.OrdinalIgnoreCase);
        }

        // True when the point sits on PowerPoint's slide-editing canvas. The
        // canvas class may be the leaf under the cursor or an ancestor of it
        // (Office versions differ), so walk a few parents up.
        private bool IsOnPptCanvas(int x, int y, out IntPtr canvasHwnd)
        {
            canvasHwnd = IntPtr.Zero;
            POINT p;
            p.X = x;
            p.Y = y;
            IntPtr current = WindowFromPoint(p);
            for (int i = 0; i < 8 && current != IntPtr.Zero; i++)
            {
                if (ClassNameIs(current, SlideCanvasClass))
                {
                    canvasHwnd = current;
                    break;
                }
                current = GetParent(current);
            }
            if (canvasHwnd == IntPtr.Zero)
                return false;
            IntPtr root = GetAncestor(canvasHwnd, GA_ROOT);
            if (!ClassNameIs(root, PptFrameClass))
            {
                canvasHwnd = IntPtr.Zero;
                return false;
            }
            return true;
        }

        private static int WheelDelta(uint mouseData)
        {
            return (short)((mouseData >> 16) & 0xFFFF);
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                _lastEventTick = Environment.TickCount; // hook liveness marker
                long msg = wParam.ToInt64();

                if (msg == WM_MOUSEMOVE)
                {
                    // Hot path: one volatile read on every mouse move.
                    if (_engine != null && _engine.MousePanActive)
                    {
                        MSLLHOOKSTRUCT move = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        if (!IsSynthesized(ref move))
                            _engine.MouseMoved(move.PtX, move.PtY);
                    }
                }
                else if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    // Touch-synthesized clicks (incl. taps) must not arm the marquee logic.
                    if (!IsSynthesized(ref data))
                    {
                        if (msg == WM_LBUTTONDOWN)
                            LastLeftClickTick = Environment.TickCount; // slide-guard: user navigation marker
                        if (_enabled)
                        {
                            MouseEvent ev;
                            ev.Kind = msg == WM_LBUTTONDOWN ? MouseEventKind.Down : MouseEventKind.Up;
                            ev.X = data.PtX;
                            ev.Y = data.PtY;
                            ev.Shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                            ev.Ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                            ev.Alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                            _worker.Post(ev);
                        }
                    }
                }
                else if (msg == WM_MBUTTONDOWN)
                {
                    // A capture whose button-up never reached this hook is stale.
                    if (_middleCaptured && _engine != null && !_engine.MousePanActive)
                        _middleCaptured = false;

                    if (!_panZoomEnabled)
                        _lastMiddleGate = "통과: 이동/확대 기능 꺼짐";
                    else if (_engine == null || !_engine.Ready)
                        _lastMiddleGate = "통과: 터치 주입 준비 안 됨";
                    else
                    {
                        MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        IntPtr canvas;
                        if (IsSynthesized(ref data))
                            _lastMiddleGate = "통과: 합성 이벤트";
                        else if (!IsOnPptCanvas(data.PtX, data.PtY, out canvas))
                            _lastMiddleGate = "통과: PPT 캔버스 아님";
                        else
                        {
                            _lastMiddleGate = "소비: 팬 시작";
                            _cntMiddle++;
                            _middleCaptured = true;
                            _engine.StartMousePan(data.PtX, data.PtY, canvas);
                            return (IntPtr)1; // PowerPoint never sees this middle-drag
                        }
                    }
                }
                else if (msg == WM_MBUTTONUP)
                {
                    // Deliberately not gated on _panZoomEnabled: a pan in progress
                    // must always be closeable, or the fingers stay down.
                    if (_middleCaptured)
                    {
                        MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        if (!IsSynthesized(ref data))
                        {
                            _middleCaptured = false;
                            bool wasActive = _engine != null && _engine.MousePanActive;
                            if (_engine != null)
                                _engine.EndMousePan(data.PtX, data.PtY);
                            if (wasActive)
                                return (IntPtr)1; // we consumed the matching down
                        }
                    }
                }
                else if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                {
                    // While a middle-drag pan is running, wheel input must not fall
                    // through to PowerPoint (native scrolling would fight the pan).
                    if (_engine != null && _engine.MousePanActive)
                    {
                        IntPtr fgRoot = GetForegroundWindow();
                        if (ClassNameIs(fgRoot, PptFrameClass))
                        {
                            _lastWheelGate = "소비: 가운데 드래그 중 휠 차단";
                            return (IntPtr)1;
                        }
                    }
                    else if (!_panZoomEnabled)
                        _lastWheelGate = "통과: 이동/확대 기능 꺼짐";
                    else if (_engine == null || !_engine.Ready)
                        _lastWheelGate = "통과: 터치 주입 준비 안 됨";
                    else
                    {
                        MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        IntPtr canvas = IntPtr.Zero;
                        bool onCanvas = false;
                        if (IsTouchSynthesized(ref data))
                            _lastWheelGate = "통과: 터치 합성 이벤트";
                        else if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                            _lastWheelGate = "통과: 왼쪽 버튼 드래그 중";
                        else
                        {
                            onCanvas = IsOnPptCanvas(data.PtX, data.PtY, out canvas);
                            // Injected touch walks the pointer; while our wheel/pinch
                            // gesture is live, keep routing notches to its canvas even
                            // if the walked cursor strayed off it (else mid-gesture
                            // notches fall through to PowerPoint natively).
                            if (!onCanvas && _engine.WheelPinchActive && _lastCanvasHwnd != IntPtr.Zero)
                            {
                                canvas = _lastCanvasHwnd;
                                onCanvas = true;
                            }
                            if (!onCanvas)
                                _lastWheelGate = "통과: PPT 캔버스 아님";
                        }
                        // NOTE: no foreground gate here. It misfired in real use (wheel
                        // and Ctrl+wheel silently fell through to PowerPoint's native
                        // scrolling/zoom - the "slides flip / centre zoom" symptoms)
                        // while middle-drag, which never had the gate, worked fine.
                        // Wheel over the canvas means the user wants PowerPoint anyway.
                        if (onCanvas)
                        {
                            _lastCanvasHwnd = canvas;
                            int delta = WheelDelta(data.MouseData);
                            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                            if (msg == WM_MOUSEWHEEL && ctrl)
                            {
                                // Figma-style zoom around the cursor.
                                _lastWheelGate = "소비: Ctrl+휠 → 커서 기준 줌";
                                _cntWheelPinch++;
                                _engine.AddPinchZoom(GestureEngine.WheelNotchesToZoomFactor(delta),
                                    data.PtX, data.PtY, canvas);
                            }
                            else
                            {
                                // Smooth pan instead of line scrolling; clamped by the
                                // engine so the view never jumps to another slide.
                                _lastWheelGate = "소비: 휠 → 팬";
                                _cntWheelPan++;
                                double pan = GestureEngine.WheelNotchesToPanPx(delta);
                                double dx = 0.0, dy = 0.0;
                                if (msg == WM_MOUSEHWHEEL)
                                    dx = -pan;
                                else if (shift)
                                    dx = pan;
                                else
                                    dy = pan;
                                _engine.AddWheelPan(dx, dy, data.PtX, data.PtY, canvas);
                            }
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
