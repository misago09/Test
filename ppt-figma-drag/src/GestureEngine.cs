using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace PptFigmaDrag
{
    // Turns middle-drag / wheel / Ctrl+wheel into synthetic two-finger touch
    // gestures on a dedicated high-priority thread. The mouse hook only posts
    // tiny commands here; this thread never touches COM - viewport geometry
    // comes from ViewportMonitor's cached state.
    internal sealed class GestureEngine : IDisposable
    {
        private const int FrameMs = 8;                  // ~125 Hz injection cadence
        private const int ContactSpreadPx = 24;         // half-distance between the two fingers
        private const double PanPxPerNotch = 110.0;     // wheel notch -> pan distance
        private const int MaxPanStepPx = 48;            // per-frame wheel-pan speed cap
        private const int GestureIdleEndMs = 140;       // no new notches -> finish gesture
        private const double ZoomFactorPerNotch = 1.15;
        // Direction-aware start spread: a zoom-in leg grows 40->260 (6.5x) and a
        // zoom-out leg shrinks 240->24 (10x), so continuous zooming rarely needs
        // a mid-zoom re-anchor (each re-anchor risks a visible anchor wobble).
        private const int PinchStartInHalfPx = 40;
        private const int PinchStartOutHalfPx = 240;
        private const int PinchMinHalfPx = 24;
        private const int PinchMaxHalfPx = 260;
        private const int MaxPinchStepPx = 14;
        private const int FreshStateWaitMs = 100;
        private const int SettleFrames = 4;            // ~64ms stationary before UP kills inertia

        private enum GState
        {
            Idle,
            MousePan,
            WheelPan,
            Pinch
        }

        private enum CmdKind
        {
            PanStart,
            PanEnd,
            Wheel,
            Pinch,
            Probe
        }

        private struct Cmd
        {
            public CmdKind Kind;
            public int X;
            public int Y;
            public double Dx;   // wheel: pan px; pinch: zoom factor
            public double Dy;
            public IntPtr CanvasHwnd;
        }

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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        private const int VK_MBUTTON = 0x04;
        private const int CanvasEdgeInsetPx = 12;      // re-anchor before fingers leave the canvas
        private const int PanWatchdogMs = 150;         // middle button seen released for this long -> end pan

        private readonly TouchInjector _injector = new TouchInjector();
        private readonly ViewportMonitor _monitor;
        private readonly ConcurrentQueue<Cmd> _queue = new ConcurrentQueue<Cmd>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private readonly AutoResetEvent _selfTestDone = new AutoResetEvent(false);
        private volatile bool _stop;
        private volatile bool _ready;
        private volatile bool _mousePanActive;
        private volatile bool _wheelPinchActive;
        private volatile bool _selfTestOk;
        private volatile string _probeReport;

        // Where the real pointer was when a wheel/pinch gesture began. Injected
        // touch WALKS the visible cursor to the contact positions; it must be put
        // back on gesture end or the next gesture anchors at the walked position
        // (the "zoom anchor drifts" / "cursor slides to the screen edge" symptoms).
        private int _restoreCursorX, _restoreCursorY;

        // True while a wheel/pinch gesture is running; the hook keeps routing
        // wheel notches to the gesture even though the walked cursor may no
        // longer sit over the canvas.
        public bool WheelPinchActive
        {
            get { return _wheelPinchActive; }
        }

        // Latest cursor position while a mouse pan is active (coalesced). Packed
        // into one long so the engine thread never reads a torn x/y pair; only
        // the hook thread writes it.
        private long _movePacked;
        private volatile int _moveStamp;

        private static long PackPoint(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        private GState _state = GState.Idle;

        // MousePan
        private int _panCursorX, _panCursorY;
        private int _panOffsetX, _panOffsetY; // shift applied to keep contacts on-canvas
        private int _lastInjectedStamp;
        private int _lastPanEventTick;

        // WheelPan
        private double _wheelTargetX, _wheelTargetY;     // accumulated requested pan
        private double _wheelInjectedX, _wheelInjectedY; // pan injected so far
        private double _wheelBaseX, _wheelBaseY;         // contact centre anchor
        private double _wheelContactX, _wheelContactY;   // current contact centre
        private RECT _wheelRect;
        private bool _wheelRectValid;
        // Scroll room measured at _roomsSampleTick, when _wheelInjAtSample* had
        // been injected. Valid target range: injAtSample - roomNeg .. + roomPos.
        private double _roomPosX, _roomNegX, _roomPosY, _roomNegY;
        private double _wheelInjAtSampleX, _wheelInjAtSampleY;
        private int _roomsSampleTick;
        private int _wheelStartSlide;
        private int _lastNotchTick;
        // Previous live sample, for stall detection (fingers moving, view not).
        private int _stallPrevTick;
        private double _stallPrevOx, _stallPrevOy;

        // Recent (tick, injected pan) pairs. COM viewport samples lag behind the
        // injections; pairing a sample with the injected total AT ITS TIME (not
        // now) is what keeps the clamp exact under rapid scrolling.
        private const int HistSize = 64;
        private readonly int[] _histTick = new int[HistSize];
        private readonly double[] _histX = new double[HistSize];
        private readonly double[] _histY = new double[HistSize];
        private int _histCount;

        private void RecordInjectionHistory()
        {
            int i = _histCount % HistSize;
            _histTick[i] = Environment.TickCount;
            _histX[i] = _wheelInjectedX;
            _histY[i] = _wheelInjectedY;
            _histCount++;
        }

        private void InjectedAt(int tick, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            bool found = false;
            int bestTick = 0;
            int count = _histCount < HistSize ? _histCount : HistSize;
            for (int i = 0; i < count; i++)
            {
                if (tick - _histTick[i] >= 0 && (!found || _histTick[i] - bestTick >= 0))
                {
                    found = true;
                    bestTick = _histTick[i];
                    x = _histX[i];
                    y = _histY[i];
                }
            }
            // Not found: the sample predates the gesture, when nothing was injected.
        }

        // Pinch
        private double _pinchCenterX, _pinchCenterY;
        private double _pinchHalf, _pinchTargetHalf;
        private double _pinchCursorX, _pinchCursorY; // re-anchor point for fresh legs
        private RECT _pinchRect;
        private bool _pinchRectValid;
        private IntPtr _pinchCanvasHwnd;
        // Closed-loop anchor lock: PowerPoint re-clamps the view into its scroll
        // extent while zooming, dragging the anchor toward the view centre. We
        // measure where the anchored slide point actually is each sample and
        // translate the finger pair to pull it back under the cursor.
        private double _pinchAnchorSlideX, _pinchAnchorSlideY;
        private bool _pinchAnchorValid;
        private double _pinchTransX, _pinchTransY;
        private int _pinchCorrTick;

        // Diagnostics
        private volatile string _lastRoomsInfo = "(아직 휠 제스처 없음)";
        private int _wheelGestureCount;

        public string LastRoomsInfo
        {
            get { return _lastRoomsInfo; }
        }

        public int WheelGestureCount
        {
            get { return _wheelGestureCount; }
        }

        public GestureEngine(ViewportMonitor monitor)
        {
            _monitor = monitor;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "PptFigmaDrag.Gesture";
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        // True once touch injection initialized; the hook must not consume
        // middle/wheel events before (or if) this becomes true.
        public bool Ready
        {
            get { return _ready; }
        }

        public bool MousePanActive
        {
            get { return _mousePanActive; }
        }

        // Win32 error from the last failed touch injection (0 if none). Diagnostic only.
        public int LastInjectError
        {
            get { return _injector.LastError; }
        }

        // Parameter-probe report from the last failed self-test (null if none).
        public string LastProbeReport
        {
            get { return _probeReport; }
        }

        public const int ProbeKindPan2 = 0;  // two-finger parallel drag, amount = dx px
        public const int ProbeKindPan1 = 1;  // single-finger drag, amount = dx px
        public const int ProbeKindPinch = 2; // pinch, amount = spread factor (e.g. 1.5)

        // Diagnostic: inject one gesture on the engine thread and return whether
        // every InjectTouchInput call succeeded. The caller measures the actual
        // viewport reaction over COM before/after.
        public bool RunProbe(int kind, int x, int y, double amount)
        {
            if (!_ready)
                return false;
            while (_selfTestDone.WaitOne(0))
            {
                // drain a signal left over from a previously timed-out probe
            }
            Cmd c = new Cmd();
            c.Kind = CmdKind.Probe;
            c.X = x;
            c.Y = y;
            c.Dx = kind;
            c.Dy = amount;
            Post(c);
            if (!_selfTestDone.WaitOne(4000))
                return false; // engine wedged: don't report a stale result
            return _selfTestOk;
        }

        #region hook-side entry points (must stay cheap)

        public void StartMousePan(int x, int y, IntPtr canvasHwnd)
        {
            _mousePanActive = true; // set eagerly so WM_MOUSEMOVE starts flowing
            Interlocked.Exchange(ref _movePacked, PackPoint(x, y));
            _moveStamp++;
            Cmd c = new Cmd();
            c.Kind = CmdKind.PanStart;
            c.X = x;
            c.Y = y;
            c.CanvasHwnd = canvasHwnd;
            Post(c);
        }

        public void MouseMoved(int x, int y)
        {
            Interlocked.Exchange(ref _movePacked, PackPoint(x, y));
            _moveStamp++;
            _signal.Set();
        }

        public void EndMousePan(int x, int y)
        {
            _mousePanActive = false;
            Cmd c = new Cmd();
            c.Kind = CmdKind.PanEnd;
            c.X = x;
            c.Y = y;
            Post(c);
        }

        public void AddWheelPan(double dxPx, double dyPx, int x, int y, IntPtr canvasHwnd)
        {
            Cmd c = new Cmd();
            c.Kind = CmdKind.Wheel;
            c.X = x;
            c.Y = y;
            c.Dx = dxPx;
            c.Dy = dyPx;
            c.CanvasHwnd = canvasHwnd;
            Post(c);
        }

        public void AddPinchZoom(double factor, int x, int y, IntPtr canvasHwnd)
        {
            Cmd c = new Cmd();
            c.Kind = CmdKind.Pinch;
            c.X = x;
            c.Y = y;
            c.Dx = factor;
            c.CanvasHwnd = canvasHwnd;
            Post(c);
        }

        #endregion

        public static double WheelNotchesToPanPx(int wheelDelta)
        {
            return wheelDelta * (PanPxPerNotch / 120.0);
        }

        public static double WheelNotchesToZoomFactor(int wheelDelta)
        {
            return Math.Pow(ZoomFactorPerNotch, wheelDelta / 120.0);
        }

        private void Post(Cmd c)
        {
            _queue.Enqueue(c);
            _signal.Set();
        }

        private void Run()
        {
            _ready = _injector.Initialize();
            if (!_ready)
                return; // pre-Win8 or injection unavailable: features stay off

            while (!_stop)
            {
                _signal.WaitOne(_state == GState.Idle ? Timeout.Infinite : FrameMs);
                if (_stop)
                    break;
                try
                {
                    Cmd c;
                    while (_queue.TryDequeue(out c))
                        HandleCommand(c);
                    Tick();
                }
                catch
                {
                    AbortGesture();
                }
            }

            if (_injector.ContactsDown)
                _injector.Up();
        }

        private void HandleCommand(Cmd c)
        {
            switch (c.Kind)
            {
                case CmdKind.Probe:
                {
                    if (_state != GState.Idle)
                        FinishGesture();
                    int kind = (int)c.Dx;
                    bool ok;
                    if (kind == ProbeKindPinch)
                    {
                        int half1 = (int)Math.Round(55.0 * c.Dy);
                        ok = _injector.ProbePinch(c.X, c.Y, 55, half1, 12, FrameMs * 2);
                    }
                    else
                    {
                        int contacts = kind == ProbeKindPan2 ? 2 : 1;
                        ok = _injector.ProbeDrag(contacts, c.X, c.Y, (int)c.Dy, 10, FrameMs * 2);
                    }
                    _selfTestOk = ok;
                    // On failure, probe parameter variants to find what Windows rejects.
                    _probeReport = ok ? null : _injector.ProbeAll(c.X, c.Y);
                    _selfTestDone.Set();
                    break;
                }

                case CmdKind.PanStart:
                {
                    if (_state != GState.Idle)
                        FinishGesture();
                    _panCursorX = c.X;
                    _panCursorY = c.Y;
                    _lastInjectedStamp = -1;
                    // Both fingers must land ON the canvas, or DirectManipulation
                    // may see a single contact and drag a shape instead of panning.
                    // The offset is kept for the whole gesture so relative motion
                    // (and thus the magnet) is unaffected.
                    int startX = c.X, startY = c.Y;
                    ClampContactCenter(c.CanvasHwnd, ContactSpreadPx, ref startX, ref startY);
                    _panOffsetX = startX - c.X;
                    _panOffsetY = startY - c.Y;
                    _lastPanEventTick = Environment.TickCount;
                    if (_injector.Down(startX - ContactSpreadPx, startY, startX + ContactSpreadPx, startY))
                    {
                        _state = GState.MousePan;
                        _monitor.BeginGestureSampling("pan");
                    }
                    else
                    {
                        _mousePanActive = false;
                    }
                    break;
                }

                case CmdKind.PanEnd:
                    if (_state == GState.MousePan)
                    {
                        // Inject the release position directly: the engine thread
                        // must not write the hook thread's coalesced move state.
                        _panCursorX = c.X + _panOffsetX;
                        _panCursorY = c.Y + _panOffsetY;
                        _injector.Move(_panCursorX - ContactSpreadPx, _panCursorY,
                                       _panCursorX + ContactSpreadPx, _panCursorY);
                        FinishGesture();
                    }
                    break;

                case CmdKind.Wheel:
                    if (_state == GState.MousePan)
                        break; // wheel during a middle-drag: ignore
                    if (_state == GState.Pinch)
                        FinishGesture();
                    if (_state == GState.Idle)
                    {
                        if (!BeginWheelGesture(c))
                            break;
                    }
                    _wheelTargetX = ClampWheelTargetX(_wheelTargetX + c.Dx);
                    _wheelTargetY = ClampWheelTargetY(_wheelTargetY + c.Dy);
                    _lastNotchTick = Environment.TickCount;
                    DiagLog.Log("ENG", "wheel notch d=(" + (int)c.Dx + "," + (int)c.Dy +
                        ") tgt=(" + (int)_wheelTargetX + "," + (int)_wheelTargetY +
                        ") inj=(" + (int)_wheelInjectedX + "," + (int)_wheelInjectedY + ")");
                    break;

                case CmdKind.Pinch:
                    if (_state == GState.MousePan)
                        break;
                    // At PowerPoint's zoom limits (400% / 10%) a pinch delivers no
                    // scale change, so the anchor compensation would degenerate into
                    // a pure sideways drift - drop the notch entirely.
                    {
                        ViewportState vsNow = _monitor.Current;
                        if (vsNow != null && vsNow.Valid && vsNow.ZoomPercent > 0 &&
                            Environment.TickCount - vsNow.TickMs < 1500)
                        {
                            if ((c.Dx > 1.0 && vsNow.ZoomPercent >= 400) ||
                                (c.Dx < 1.0 && vsNow.ZoomPercent <= 10))
                                break;
                        }
                    }
                    if (_state == GState.WheelPan)
                        FinishGesture();
                    if (_state == GState.Idle)
                    {
                        int startHalf = c.Dx >= 1.0 ? PinchStartInHalfPx : PinchStartOutHalfPx;
                        int cx = c.X, cy = c.Y;
                        ClampContactCenter(c.CanvasHwnd, startHalf, ref cx, ref cy);
                        _pinchCenterX = cx;
                        _pinchCenterY = cy;
                        _pinchHalf = startHalf;
                        _pinchTargetHalf = startHalf;
                        // Measured on a real machine: PowerPoint anchors pinch zoom
                        // at the finger centroid (cursor point drift 1.1pt vs centre
                        // point 18.1pt), so a fixed centroid at the cursor IS the
                        // Figma-style zoom - no compensation needed.
                        _pinchCursorX = c.X;
                        _pinchCursorY = c.Y;
                        _restoreCursorX = c.X;
                        _restoreCursorY = c.Y;
                        _pinchCanvasHwnd = c.CanvasHwnd;
                        _pinchRectValid = c.CanvasHwnd != IntPtr.Zero &&
                                          GetWindowRect(c.CanvasHwnd, out _pinchRect);
                        _pinchTransX = 0.0;
                        _pinchTransY = 0.0;
                        _pinchAnchorValid = false;
                        _pinchCorrTick = 0;
                        ViewportState seed = _monitor.Current;
                        if (seed != null && seed.Valid &&
                            Environment.TickCount - seed.TickMs < 300 &&
                            Math.Abs(seed.Sx) > 0.0001 && Math.Abs(seed.Sy) > 0.0001)
                        {
                            _pinchAnchorSlideX = (c.X - seed.Ox) / seed.Sx;
                            _pinchAnchorSlideY = (c.Y - seed.Oy) / seed.Sy;
                            _pinchAnchorValid = true;
                            _pinchCorrTick = seed.TickMs;
                        }
                        if (!_injector.Down((int)Math.Round(_pinchCenterX - _pinchHalf), cy,
                                            (int)Math.Round(_pinchCenterX + _pinchHalf), cy))
                            break;
                        _state = GState.Pinch;
                        _wheelPinchActive = true;
                        _monitor.BeginGestureSampling("pinch");
                        DiagLog.Log("ENG", "pinch start @(" + c.X + "," + c.Y + ") half=" + startHalf +
                            " seed=" + (_pinchAnchorValid ? "yes" : "no"));
                    }
                    {
                        double target = _pinchTargetHalf * c.Dx;
                        bool atLowCap = _pinchTargetHalf <= PinchMinHalfPx + 0.5 && target < _pinchTargetHalf;
                        bool atHighCap = _pinchTargetHalf >= PinchMaxHalfPx - 0.5 && target > _pinchTargetHalf;
                        if (atLowCap || atHighCap)
                        {
                            // Re-anchor: finish this pinch and start a fresh one so
                            // long zooms are not limited by finger travel. The notch
                            // event's coordinates are the WALKED cursor position -
                            // keep the fresh leg anchored where the zoom began, or
                            // the anchor creeps to the screen edge leg by leg.
                            DiagLog.Log("ENG", "pinch cap re-anchor");
                            Cmd again = c;
                            again.X = (int)Math.Round(_pinchCursorX);
                            again.Y = (int)Math.Round(_pinchCursorY);
                            FinishGesture();
                            HandleCommand(again);
                            return;
                        }
                        if (target < PinchMinHalfPx) target = PinchMinHalfPx;
                        if (target > PinchMaxHalfPx) target = PinchMaxHalfPx;
                        _pinchTargetHalf = target;
                    }
                    _lastNotchTick = Environment.TickCount;
                    break;
            }
        }

        private bool BeginWheelGesture(Cmd c)
        {
            _wheelTargetX = 0.0;
            _wheelTargetY = 0.0;
            _wheelInjectedX = 0.0;
            _wheelInjectedY = 0.0;
            _histCount = 0;
            RecordInjectionHistory();
            bool clamped = ComputeWheelRooms(c.CanvasHwnd);
            ViewportState vsStart = _monitor.Current;
            _wheelStartSlide = vsStart != null && vsStart.Valid ? vsStart.SlideIndex : 0;
            _wheelGestureCount++;
            _lastRoomsInfo = (clamped ? "클램프 적용" : "뷰포트 확인 실패 → 휠 입력 무시") +
                " / 위로 " + (int)_roomPosY + "px, 아래로 " + (int)_roomNegY +
                "px, 왼쪽 " + (int)_roomPosX + "px, 오른쪽 " + (int)_roomNegX + "px";
            // Requirement: the wheel must NEVER land on another slide. Without
            // viewport info we cannot bound the pan, so dropping the notch is
            // strictly better than guessing.
            if (!clamped)
            {
                DiagLog.Log("ENG", "wheel drop: no viewport sample");
                return false;
            }
            // No scroll room in the requested direction (e.g. the slide fits the
            // window): don't bother with a gesture that would move nothing.
            if (Math.Abs(ClampWheelTargetX(c.Dx)) < 1.0 &&
                Math.Abs(ClampWheelTargetY(c.Dy)) < 1.0)
            {
                DiagLog.Log("ENG", "wheel drop: no room (" + _lastRoomsInfo + ")");
                return false;
            }

            int startX = c.X, startY = c.Y;
            ClampContactCenter(c.CanvasHwnd, ContactSpreadPx, ref startX, ref startY);
            _wheelBaseX = startX;
            _wheelBaseY = startY;
            _wheelContactX = startX;
            _wheelContactY = startY;
            _restoreCursorX = c.X;
            _restoreCursorY = c.Y;
            if (!_injector.Down(startX - ContactSpreadPx, startY, startX + ContactSpreadPx, startY))
                return false;
            _state = GState.WheelPan;
            _wheelPinchActive = true;
            _monitor.BeginGestureSampling("wheel");
            _monitor.BeginSlideGuard(); // backstop in case a flip slips through anyway
            DiagLog.Log("ENG", "wheel start @(" + c.X + "," + c.Y + ") slide=" + _wheelStartSlide +
                " " + _lastRoomsInfo);
            return true;
        }

        // How far the view may pan in each direction without leaving the current
        // slide (which is what makes PowerPoint jump to the next/previous one).
        // Returns false when the viewport could not be established - the caller
        // must then drop the wheel input entirely.
        private bool ComputeWheelRooms(IntPtr canvasHwnd)
        {
            _roomPosX = _roomNegX = _roomPosY = _roomNegY = 0.0;
            _wheelInjAtSampleX = _wheelInjectedX;
            _wheelInjAtSampleY = _wheelInjectedY;

            _wheelRectValid = canvasHwnd != IntPtr.Zero && GetWindowRect(canvasHwnd, out _wheelRect);
            if (!_wheelRectValid)
                return false;

            // During rapid scrolling the monitor is already sampling at ~66Hz, so
            // a recent sample is fine; only genuinely stale data forces a wait.
            ViewportState vs = _monitor.Current;
            if (vs == null || !vs.Valid || Environment.TickCount - vs.TickMs >= 700)
            {
                int requestTick = Environment.TickCount;
                _monitor.RequestSampleNow();
                vs = null;
                for (int waited = 0; waited <= FreshStateWaitMs; waited += 10)
                {
                    ViewportState candidate = _monitor.Current;
                    if (candidate != null && candidate.Valid &&
                        candidate.TickMs - requestTick >= -30)
                    {
                        vs = candidate;
                        break;
                    }
                    // A middle-drag pan wants to start: don't make it wait on COM.
                    Cmd pending;
                    if (_queue.TryPeek(out pending) && pending.Kind == CmdKind.PanStart)
                        break;
                    Thread.Sleep(10);
                }
                if (vs == null)
                    return false;
            }

            ApplyRooms(vs);
            return true;
        }

        // Rooms are measured relative to the view at the sample's time; the pan
        // injected up to that moment is snapshotted so targets can be clamped
        // exactly even while more pan lands between samples.
        private void ApplyRooms(ViewportState vs)
        {
            // The canvas window rect includes chrome the viewport doesn't: the
            // ruler sits at the TOP (about 26px), which made upward scrolling
            // overshoot past the slide onto the previous one. Allow extra safety
            // there; the bottom edge proved accurate in testing.
            const double topSafetyPx = 40.0;
            const double bottomSafetyPx = 12.0;
            const double horizSafetyPx = 3.0;

            // PowerPoint's true scroll extent is the slide PLUS gray margin. When
            // the slide overflows the window, let the pan reach that margin - the
            // stall detector and the slide guard stop us at the real extent. When
            // the slide fully fits, keep everything pinned so gestures don't run
            // dead (fingers moving, nothing scrolling).
            double rectW = _wheelRect.Right - _wheelRect.Left;
            double rectH = _wheelRect.Bottom - _wheelRect.Top;
            bool overflow = vs.SlideWpt * vs.Sx > rectW || vs.SlideHpt * vs.Sy > rectH;
            // No vertical margin: past the slide's top/bottom lies the neighboring
            // slide, and even 60px of allowance caused escape-and-revert jumps.
            double marginY = 0.0;
            double marginX = overflow ? 250.0 : 0.0;  // sideways there is no next slide

            double slideRight = vs.Ox + vs.SlideWpt * vs.Sx;
            double slideBottom = vs.Oy + vs.SlideHpt * vs.Sy;

            // Content moving down/right (positive pan) is allowed until the slide
            // top/left edge reaches the canvas top/left edge, and vice versa.
            _roomPosY = Math.Max(0.0, _wheelRect.Top - vs.Oy - topSafetyPx) + marginY;
            _roomNegY = Math.Max(0.0, slideBottom - _wheelRect.Bottom - bottomSafetyPx) + marginY;
            _roomPosX = Math.Max(0.0, _wheelRect.Left - vs.Ox - horizSafetyPx) + marginX;
            _roomNegX = Math.Max(0.0, slideRight - _wheelRect.Right - horizSafetyPx) + marginX;
            _roomsSampleTick = vs.TickMs;
            InjectedAt(vs.TickMs, out _wheelInjAtSampleX, out _wheelInjAtSampleY);
            _stallPrevTick = vs.TickMs;
            _stallPrevOx = vs.Ox;
            _stallPrevOy = vs.Oy;
        }

        private double ClampWheelTargetX(double target)
        {
            double lo = _wheelInjAtSampleX - _roomNegX;
            double hi = _wheelInjAtSampleX + _roomPosX;
            if (target < lo) return lo;
            if (target > hi) return hi;
            return target;
        }

        private double ClampWheelTargetY(double target)
        {
            double lo = _wheelInjAtSampleY - _roomNegY;
            double hi = _wheelInjAtSampleY + _roomPosY;
            if (target < lo) return lo;
            if (target > hi) return hi;
            return target;
        }

        // Shifts a gesture centre so that contacts spread +-halfSpread horizontally
        // (with a small inset) stay inside the canvas window.
        private static void ClampContactCenter(IntPtr canvasHwnd, int halfSpread, ref int x, ref int y)
        {
            RECT r;
            if (canvasHwnd == IntPtr.Zero || !GetWindowRect(canvasHwnd, out r))
                return;
            x = ClampSafe(x, r.Left + halfSpread + 4, r.Right - halfSpread - 4);
            y = ClampSafe(y, r.Top + 4, r.Bottom - 4);
        }

        private static int ClampSafe(int v, int lo, int hi)
        {
            if (lo > hi)
                return (lo + hi) / 2; // canvas smaller than the spread: best effort
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static double StepToward(double current, double target, double maxStep)
        {
            double d = target - current;
            if (d > maxStep) d = maxStep;
            else if (d < -maxStep) d = -maxStep;
            return current + d;
        }

        private static double ClampStep(double delta, double maxStep)
        {
            if (delta > maxStep) return maxStep;
            if (delta < -maxStep) return -maxStep;
            return delta;
        }

        private void Tick()
        {
            switch (_state)
            {
                case GState.MousePan:
                {
                    int stamp = _moveStamp;
                    if (stamp != _lastInjectedStamp)
                    {
                        _lastInjectedStamp = stamp;
                        _lastPanEventTick = Environment.TickCount;
                        long packed = Interlocked.Read(ref _movePacked);
                        _panCursorX = (int)(packed >> 32) + _panOffsetX;
                        _panCursorY = (int)packed + _panOffsetY;
                        if (!_injector.Move(_panCursorX - ContactSpreadPx, _panCursorY,
                                            _panCursorX + ContactSpreadPx, _panCursorY))
                            AbortGesture();
                    }
                    else if ((GetAsyncKeyState(VK_MBUTTON) & 0x8000) == 0 &&
                             Environment.TickCount - _lastPanEventTick > PanWatchdogMs)
                    {
                        // The button-up never reached the hook (secure desktop,
                        // hook removed, ...): don't leave the fingers stuck.
                        FinishGesture();
                        _mousePanActive = false;
                    }
                    break;
                }

                case GState.WheelPan:
                {
                    // Live re-clamp: the monitor keeps sampling during the gesture,
                    // and each fresh sample re-anchors the allowed range, so the pan
                    // cannot cross the slide edge even when notches keep arriving.
                    ViewportState liveVs = _monitor.Current;
                    if (liveVs != null && liveVs.Valid)
                    {
                        if (_wheelStartSlide > 0 && liveVs.SlideIndex > 0 &&
                            liveVs.SlideIndex != _wheelStartSlide)
                        {
                            // Escaped onto another slide despite the clamp: stop
                            // pushing immediately; the monitor reverts the slide.
                            DiagLog.Log("ENG", "wheel slide-escape " + _wheelStartSlide +
                                "->" + liveVs.SlideIndex + ", freezing");
                            _wheelTargetX = _wheelInjectedX;
                            _wheelTargetY = _wheelInjectedY;
                        }
                        else if (_wheelRectValid && liveVs.TickMs != _roomsSampleTick)
                        {
                            // Stall detection BEFORE the sample becomes the new
                            // baseline: if the fingers moved but the view did not,
                            // PowerPoint's real scroll extent is reached - stop
                            // pushing (prevents dead finger/cursor travel and
                            // pressure against the slide-flip boundary).
                            double injPrevX, injPrevY, injNowX, injNowY;
                            InjectedAt(_stallPrevTick, out injPrevX, out injPrevY);
                            InjectedAt(liveVs.TickMs, out injNowX, out injNowY);
                            double injDx = injNowX - injPrevX;
                            double injDy = injNowY - injPrevY;
                            double viewDx = liveVs.Ox - _stallPrevOx;
                            double viewDy = liveVs.Oy - _stallPrevOy;
                            if (Math.Abs(injDx) > 40.0 && Math.Abs(viewDx) < Math.Abs(injDx) * 0.15)
                            {
                                DiagLog.Log("ENG", "wheel stall X inj=" + (int)injDx + " view=" + (int)viewDx);
                                _wheelTargetX = _wheelInjectedX;
                            }
                            if (Math.Abs(injDy) > 40.0 && Math.Abs(viewDy) < Math.Abs(injDy) * 0.15)
                            {
                                DiagLog.Log("ENG", "wheel stall Y inj=" + (int)injDy + " view=" + (int)viewDy);
                                _wheelTargetY = _wheelInjectedY;
                            }

                            ApplyRooms(liveVs);
                            _wheelTargetX = ClampWheelTargetX(_wheelTargetX);
                            _wheelTargetY = ClampWheelTargetY(_wheelTargetY);
                            DiagLog.Log("ENG", "vp o=(" + (int)liveVs.Ox + "," + (int)liveVs.Oy +
                                ") zoom=" + liveVs.ZoomPercent + "% slide=" + liveVs.SlideIndex +
                                " inj=(" + (int)_wheelInjectedX + "," + (int)_wheelInjectedY +
                                ") tgt=(" + (int)_wheelTargetX + "," + (int)_wheelTargetY +
                                ") roomY=" + (int)_roomPosY + "/" + (int)_roomNegY +
                                " roomX=" + (int)_roomPosX + "/" + (int)_roomNegX);
                        }
                    }

                    bool moved = false;
                    double stepX = ClampStep(_wheelTargetX - _wheelInjectedX, MaxPanStepPx);
                    double stepY = ClampStep(_wheelTargetY - _wheelInjectedY, MaxPanStepPx);
                    if (stepX != 0.0 || stepY != 0.0)
                    {
                        double nextX = _wheelContactX + stepX;
                        double nextY = _wheelContactY + stepY;

                        // About to run out of canvas: lift the fingers and put them
                        // back down at the anchor, then continue from there.
                        if (_wheelRectValid &&
                            (nextX - ContactSpreadPx < _wheelRect.Left + CanvasEdgeInsetPx ||
                             nextX + ContactSpreadPx > _wheelRect.Right - CanvasEdgeInsetPx ||
                             nextY < _wheelRect.Top + CanvasEdgeInsetPx ||
                             nextY > _wheelRect.Bottom - CanvasEdgeInsetPx))
                        {
                            _injector.Hold();
                            Thread.Sleep(FrameMs * 2);
                            _injector.Hold();
                            Thread.Sleep(FrameMs * 2);
                            _injector.Hold();
                            _injector.Up();
                            if (!_injector.Down((int)Math.Round(_wheelBaseX - ContactSpreadPx), (int)Math.Round(_wheelBaseY),
                                                (int)Math.Round(_wheelBaseX + ContactSpreadPx), (int)Math.Round(_wheelBaseY)))
                            {
                                AbortGesture();
                                break;
                            }
                            _wheelContactX = _wheelBaseX;
                            _wheelContactY = _wheelBaseY;
                            nextX = _wheelContactX + stepX;
                            nextY = _wheelContactY + stepY;
                        }

                        if (!_injector.Move((int)Math.Round(nextX - ContactSpreadPx), (int)Math.Round(nextY),
                                            (int)Math.Round(nextX + ContactSpreadPx), (int)Math.Round(nextY)))
                        {
                            AbortGesture();
                            break;
                        }
                        _wheelContactX = nextX;
                        _wheelContactY = nextY;
                        _wheelInjectedX += stepX;
                        _wheelInjectedY += stepY;
                        RecordInjectionHistory();
                        moved = true;
                    }
                    if (!moved && Environment.TickCount - _lastNotchTick > GestureIdleEndMs)
                        FinishGesture();
                    break;
                }

                case GState.Pinch:
                {
                    double prevHalf = _pinchHalf;
                    if (_pinchHalf != _pinchTargetHalf)
                        _pinchHalf = StepToward(_pinchHalf, _pinchTargetHalf, MaxPinchStepPx);

                    // Closed-loop anchor lock: measure where the anchored slide
                    // point actually sits and pull it back under the cursor by
                    // translating the pair (PowerPoint pans and zooms in one
                    // gesture). This runs even after the spread settles, so the
                    // final zoom frames get corrected too.
                    bool transStepped = false;
                    ViewportState pinchVs = _monitor.Current;
                    if (pinchVs != null && pinchVs.Valid && pinchVs.TickMs != _pinchCorrTick &&
                        Math.Abs(pinchVs.Sx) > 0.0001 && Math.Abs(pinchVs.Sy) > 0.0001)
                    {
                        _pinchCorrTick = pinchVs.TickMs;
                        if (!_pinchAnchorValid)
                        {
                            _pinchAnchorSlideX = (_pinchCursorX - pinchVs.Ox) / pinchVs.Sx;
                            _pinchAnchorSlideY = (_pinchCursorY - pinchVs.Oy) / pinchVs.Sy;
                            _pinchAnchorValid = true;
                        }
                        else
                        {
                            double errX = _pinchCursorX - (pinchVs.Ox + _pinchAnchorSlideX * pinchVs.Sx);
                            double errY = _pinchCursorY - (pinchVs.Oy + _pinchAnchorSlideY * pinchVs.Sy);
                            if (Math.Abs(errX) > 2.0)
                            {
                                _pinchTransX += ClampStep(errX * 0.6, 30.0);
                                transStepped = true;
                            }
                            if (Math.Abs(errY) > 2.0)
                            {
                                _pinchTransY += ClampStep(errY * 0.6, 30.0);
                                transStepped = true;
                            }
                            if (_pinchTransX > 500.0) _pinchTransX = 500.0;
                            else if (_pinchTransX < -500.0) _pinchTransX = -500.0;
                            if (_pinchTransY > 500.0) _pinchTransY = 500.0;
                            else if (_pinchTransY < -500.0) _pinchTransY = -500.0;
                            if (transStepped)
                                DiagLog.Log("ENG", "pinch corr err=(" + (int)errX + "," + (int)errY +
                                    ") trans=(" + (int)_pinchTransX + "," + (int)_pinchTransY +
                                    ") half=" + (int)_pinchHalf + " zoom=" + pinchVs.ZoomPercent + "%");
                        }
                    }

                    bool moved = false;
                    if (_pinchHalf != prevHalf || transStepped)
                    {
                        double cx = _pinchCenterX + _pinchTransX;
                        double cy = _pinchCenterY + _pinchTransY;

                        // If the pair is about to leave the canvas (where the
                        // virtual-screen clamp would shift the centroid and drag the
                        // zoom anchor off the cursor), lift and restart a fresh leg
                        // at the cursor with the residual zoom.
                        if (_pinchRectValid &&
                            (cx - _pinchHalf < _pinchRect.Left + CanvasEdgeInsetPx ||
                             cx + _pinchHalf > _pinchRect.Right - CanvasEdgeInsetPx ||
                             cy < _pinchRect.Top + CanvasEdgeInsetPx ||
                             cy > _pinchRect.Bottom - CanvasEdgeInsetPx))
                        {
                            double remaining = prevHalf > 0.0 ? _pinchTargetHalf / prevHalf : 1.0;
                            _injector.Hold();
                            Thread.Sleep(FrameMs * 2);
                            _injector.Hold();
                            Thread.Sleep(FrameMs * 2);
                            _injector.Hold();
                            _injector.Up();

                            int startHalf = remaining >= 1.0 ? PinchStartInHalfPx : PinchStartOutHalfPx;
                            int ncx = (int)Math.Round(_pinchCursorX);
                            int ncy = (int)Math.Round(_pinchCursorY);
                            ClampContactCenter(_pinchCanvasHwnd, startHalf, ref ncx, ref ncy);
                            _pinchCenterX = ncx;
                            _pinchCenterY = ncy;
                            _pinchHalf = startHalf;
                            _pinchTransX = 0.0; // the delivered pan is baked into the view
                            _pinchTransY = 0.0;
                            double newTarget = startHalf * remaining;
                            if (newTarget < PinchMinHalfPx) newTarget = PinchMinHalfPx;
                            if (newTarget > PinchMaxHalfPx) newTarget = PinchMaxHalfPx;
                            _pinchTargetHalf = newTarget;
                            DiagLog.Log("ENG", "pinch edge re-anchor half=" + startHalf +
                                " target=" + (int)newTarget);

                            if (!_injector.Down(ncx - startHalf, ncy, ncx + startHalf, ncy))
                            {
                                AbortGesture();
                                break;
                            }
                            moved = true; // fresh leg continues next tick
                        }
                        else if (!_injector.Move((int)Math.Round(cx - _pinchHalf), (int)Math.Round(cy),
                                                 (int)Math.Round(cx + _pinchHalf), (int)Math.Round(cy)))
                        {
                            AbortGesture();
                            break;
                        }
                        else
                        {
                            moved = true;
                        }
                    }
                    if (!moved && Environment.TickCount - _lastNotchTick > GestureIdleEndMs)
                        FinishGesture();
                    break;
                }
            }

            // Injected touch drags the visible pointer to the contact positions on
            // EVERY frame - a long-lived gesture (continuous wheel/zoom) walked it
            // to the screen edge before the end-of-gesture restore could act. Pin
            // it back each tick; a middle-drag pan uses the real pointer, skip it.
            if (_state == GState.WheelPan || _state == GState.Pinch)
                SetCursorPos(_restoreCursorX, _restoreCursorY);
        }

        // Hold the fingers still for a few frames (kills DirectManipulation
        // inertia, keeping the view exactly where the cursor left it), then lift.
        private void FinishGesture()
        {
            if (_injector.ContactsDown)
            {
                for (int i = 0; i < SettleFrames; i++)
                {
                    _injector.Hold();
                    Thread.Sleep(FrameMs * 2);
                }
                _injector.Up();
            }
            EndGestureBookkeeping();
        }

        private void AbortGesture()
        {
            if (_injector.ContactsDown)
            {
                // Best-effort inertia kill even on the failure path, so a transient
                // injection error doesn't fling the viewport.
                _injector.Hold();
                Thread.Sleep(FrameMs * 2);
                _injector.Hold();
                _injector.Up();
            }
            EndGestureBookkeeping();
        }

        private void EndGestureBookkeeping()
        {
            if (_state != GState.Idle)
                DiagLog.Log("ENG", "gesture end " + _state +
                    " inj=(" + (int)_wheelInjectedX + "," + (int)_wheelInjectedY + ")");
            if (_state == GState.WheelPan)
                _monitor.EndSlideGuard();
            if (_state != GState.Idle)
                _monitor.EndGestureSampling();
            // Injected touch walks the real pointer to the contact positions; put
            // it back where the user left it, or the next wheel/zoom anchors at
            // the walked position and the cursor creeps across the screen.
            if (_state == GState.WheelPan || _state == GState.Pinch)
                SetCursorPos(_restoreCursorX, _restoreCursorY);
            // Only a MousePan owns this flag: a PanStart command may have set it
            // eagerly for the NEXT gesture while we are closing the previous one.
            if (_state == GState.MousePan)
                _mousePanActive = false;
            _wheelPinchActive = false;
            _state = GState.Idle;
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            try { _thread.Join(500); } // let the final Up() land before exit
            catch { }
        }
    }
}
