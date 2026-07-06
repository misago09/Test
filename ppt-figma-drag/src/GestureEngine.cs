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
        private const int PinchStartHalfPx = 70;
        private const int PinchMinHalfPx = 26;
        private const int PinchMaxHalfPx = 230;
        private const int MaxPinchStepPx = 10;
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
            SelfTest
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
        private volatile bool _selfTestOk;

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
        private double _roomPosX, _roomNegX, _roomPosY, _roomNegY;
        private int _lastNotchTick;

        // Pinch
        private double _pinchCenterX, _pinchCenterY;
        private double _pinchHalf, _pinchTargetHalf;

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

        // Diagnostic: on the engine thread, inject a visible two-finger pinch-zoom
        // at (x,y) and report whether every InjectTouchInput call succeeded.
        //  -1 = engine never became Ready (touch injection unavailable)
        //   0 = injection API call failed (permission / UIPI / bad coords)
        //   1 = all injection calls returned success
        public int RunSelfTest(int x, int y)
        {
            if (!_ready)
                return -1;
            Cmd c = new Cmd();
            c.Kind = CmdKind.SelfTest;
            c.X = x;
            c.Y = y;
            Post(c);
            _selfTestDone.WaitOne(2000);
            return _selfTestOk ? 1 : 0;
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
                case CmdKind.SelfTest:
                {
                    if (_state != GState.Idle)
                        FinishGesture();
                    // Visible pinch-zoom-in around the cursor: always shows if
                    // PowerPoint accepts injected touch, regardless of scroll room.
                    bool ok = true;
                    int half = 40;
                    ok = _injector.Down(c.X - half, c.Y, c.X + half, c.Y);
                    for (int i = 0; i < 12 && ok; i++)
                    {
                        half += 9;
                        ok = _injector.Move(c.X - half, c.Y, c.X + half, c.Y);
                        Thread.Sleep(FrameMs * 2);
                    }
                    _injector.Hold();
                    Thread.Sleep(FrameMs);
                    bool up = _injector.Up();
                    _selfTestOk = ok && up;
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
                    _wheelTargetX = ClampPan(_wheelTargetX + c.Dx, _roomNegX, _roomPosX);
                    _wheelTargetY = ClampPan(_wheelTargetY + c.Dy, _roomNegY, _roomPosY);
                    _lastNotchTick = Environment.TickCount;
                    break;

                case CmdKind.Pinch:
                    if (_state == GState.MousePan)
                        break;
                    if (_state == GState.WheelPan)
                        FinishGesture();
                    if (_state == GState.Idle)
                    {
                        int cx = c.X, cy = c.Y;
                        ClampContactCenter(c.CanvasHwnd, PinchStartHalfPx, ref cx, ref cy);
                        _pinchCenterX = cx;
                        _pinchCenterY = cy;
                        _pinchHalf = PinchStartHalfPx;
                        _pinchTargetHalf = PinchStartHalfPx;
                        if (!_injector.Down((int)Math.Round(_pinchCenterX - _pinchHalf), cy,
                                            (int)Math.Round(_pinchCenterX + _pinchHalf), cy))
                            break;
                        _state = GState.Pinch;
                        _monitor.BeginGestureSampling("pinch");
                    }
                    {
                        double target = _pinchTargetHalf * c.Dx;
                        bool atLowCap = _pinchTargetHalf <= PinchMinHalfPx + 0.5 && target < _pinchTargetHalf;
                        bool atHighCap = _pinchTargetHalf >= PinchMaxHalfPx - 0.5 && target > _pinchTargetHalf;
                        if (atLowCap || atHighCap)
                        {
                            // Re-anchor: finish this pinch and start a fresh one so
                            // long zooms are not limited by finger travel.
                            FinishGesture();
                            Cmd again = c;
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
            bool clamped = ComputeWheelRooms(c.CanvasHwnd);
            _wheelTargetX = 0.0;
            _wheelTargetY = 0.0;
            _wheelInjectedX = 0.0;
            _wheelInjectedY = 0.0;
            int startX = c.X, startY = c.Y;
            ClampContactCenter(c.CanvasHwnd, ContactSpreadPx, ref startX, ref startY);
            _wheelBaseX = startX;
            _wheelBaseY = startY;
            _wheelContactX = startX;
            _wheelContactY = startY;
            _wheelRectValid = c.CanvasHwnd != IntPtr.Zero && GetWindowRect(c.CanvasHwnd, out _wheelRect);
            if (!_wheelRectValid)
            {
                _wheelRect.Left = _wheelRect.Top = _wheelRect.Right = _wheelRect.Bottom = 0;
            }
            if (!_injector.Down(startX - ContactSpreadPx, startY, startX + ContactSpreadPx, startY))
                return false;
            _state = GState.WheelPan;
            _monitor.BeginGestureSampling("wheel");
            // With valid rooms the pan mathematically cannot leave the slide, so
            // the guard would only risk reverting the user's own navigation; arm
            // it solely for the unclamped fallback.
            if (!clamped)
                _monitor.BeginSlideGuard();
            return true;
        }

        // How far the view may pan in each direction without leaving the current
        // slide (which is what makes PowerPoint jump to the next/previous one).
        // Returns true when real rooms were computed, false for the unclamped
        // fallback (which needs the slide guard as backstop).
        private bool ComputeWheelRooms(IntPtr canvasHwnd)
        {
            _roomPosX = _roomNegX = _roomPosY = _roomNegY = 100000.0; // no clamp fallback

            // Only a sample taken for THIS gesture is trustworthy - an older one
            // predates whatever zoom/scroll/slide change happened since.
            int requestTick = Environment.TickCount;
            _monitor.RequestSampleNow();
            ViewportState vs = null;
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
                return false; // unclamped; the slide guard is the backstop

            RECT canvas;
            if (canvasHwnd == IntPtr.Zero || !GetWindowRect(canvasHwnd, out canvas))
                return false;

            double slideRight = vs.Ox + vs.SlideWpt * vs.Sx;
            double slideBottom = vs.Oy + vs.SlideHpt * vs.Sy;

            // Content moving down/right (positive pan) is allowed until the slide
            // top/left edge reaches the canvas top/left edge, and vice versa.
            _roomPosY = Math.Max(0.0, canvas.Top - vs.Oy);
            _roomNegY = Math.Max(0.0, slideBottom - canvas.Bottom);
            _roomPosX = Math.Max(0.0, canvas.Left - vs.Ox);
            _roomNegX = Math.Max(0.0, slideRight - canvas.Right);
            return true;
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

        private static double ClampPan(double v, double roomNeg, double roomPos)
        {
            if (v > roomPos) return roomPos;
            if (v < -roomNeg) return -roomNeg;
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
                            Thread.Sleep(FrameMs);
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
                        moved = true;
                    }
                    if (!moved && Environment.TickCount - _lastNotchTick > GestureIdleEndMs)
                        FinishGesture();
                    break;
                }

                case GState.Pinch:
                {
                    bool moved = false;
                    if (_pinchHalf != _pinchTargetHalf)
                    {
                        _pinchHalf = StepToward(_pinchHalf, _pinchTargetHalf, MaxPinchStepPx);
                        if (!_injector.Move((int)Math.Round(_pinchCenterX - _pinchHalf), (int)Math.Round(_pinchCenterY),
                                            (int)Math.Round(_pinchCenterX + _pinchHalf), (int)Math.Round(_pinchCenterY)))
                        {
                            AbortGesture();
                            break;
                        }
                        moved = true;
                    }
                    if (!moved && Environment.TickCount - _lastNotchTick > GestureIdleEndMs)
                        FinishGesture();
                    break;
                }
            }
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
            if (_state == GState.WheelPan)
                _monitor.EndSlideGuard();
            if (_state != GState.Idle)
                _monitor.EndGestureSampling();
            // Only a MousePan owns this flag: a PanStart command may have set it
            // eagerly for the NEXT gesture while we are closing the previous one.
            if (_state == GState.MousePan)
                _mousePanActive = false;
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
