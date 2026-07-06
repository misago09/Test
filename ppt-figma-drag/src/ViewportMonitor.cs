using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PptFigmaDrag
{
    // Immutable snapshot of the slide viewport, published for the GestureEngine
    // (wheel-pan clamping) and the diagnostic log.
    internal sealed class ViewportState
    {
        public bool Valid;
        public double Ox, Oy;        // screen px of slide point (0,0)
        public double Sx, Sy;        // px per slide point
        public double SlideWpt, SlideHpt;
        public int ZoomPercent;
        public int SlideIndex;
        public int TickMs;           // Environment.TickCount at sample time
    }

    // Samples the PowerPoint viewport over COM on its own STA thread while a
    // gesture is running: publishes the mapping for the engine, watches for
    // unwanted slide changes during wheel pans (and reverts them), and - when
    // enabled - writes a CSV log pairing cursor and viewport coordinates with
    // millisecond timestamps, so the "magnet" behaviour is verifiable.
    internal sealed class ViewportMonitor : IDisposable
    {
        private const int SampleIntervalMs = 15;   // ~66 Hz while a gesture runs
        private const int SampleTailMs = 400;      // keep sampling after gesture end
        private const int GuardVerifyDelayMs = 250;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        private readonly PowerPointSession _session = new PowerPointSession();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;

        private volatile ViewportState _current = new ViewportState();
        private volatile bool _loggingEnabled;
        private volatile string _phase = "idle";
        private int _activeGestures;               // interlocked
        private int _lastGestureEndTick;
        private volatile bool _sampleRequested;

        // Slide guard (wheel pans must never end up on another slide). Mutated
        // from both the engine thread (Begin/End) and the monitor thread (Sample),
        // hence the lock; the COM revert itself runs outside it.
        private readonly object _guardLock = new object();
        private volatile bool _guardArmed;
        private int _guardSlideIndex;
        private int _guardVerifyAtTick;
        private volatile bool _guardVerifyPending;
        private int _guardVerifyAttempts;
        private bool _guardRevertedMidGesture;
        // Content signature of the previous sample: SlideIndex flickers to a
        // neighbor during gestures while the coordinates stay frozen, so an
        // index change only counts as a real escape when Ox/Oy also moved.
        private double _prevSampleOx, _prevSampleOy;
        private bool _prevSampleValid;

        private StreamWriter _log;
        private int _logStartTick;
        private readonly string _logPath;

        public ViewportMonitor()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PptFigmaDrag");
            _logPath = Path.Combine(dir, "viewport-log.csv");

            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "PptFigmaDrag.Viewport";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public string LogPath
        {
            get { return _logPath; }
        }

        public bool LoggingEnabled
        {
            get { return _loggingEnabled; }
            set
            {
                _loggingEnabled = value;
                _signal.Set();
            }
        }

        public ViewportState Current
        {
            get { return _current; }
        }

        public void BeginGestureSampling(string phase)
        {
            _phase = phase;
            Interlocked.Increment(ref _activeGestures);
            _signal.Set();
        }

        public void EndGestureSampling()
        {
            _phase = "idle";
            Interlocked.Decrement(ref _activeGestures);
            _lastGestureEndTick = Environment.TickCount;
            _signal.Set();
        }

        public void RequestSampleNow()
        {
            _sampleRequested = true;
            _signal.Set();
        }

        public void BeginSlideGuard()
        {
            lock (_guardLock)
            {
                // If the previous wheel gesture's verify is still pending, keep its
                // captured slide as the baseline: rapid successive wheel bursts must
                // all come home to the slide the FIRST one started on.
                if (!(_guardVerifyPending && _guardSlideIndex > 0))
                {
                    _guardSlideIndex = 0;
                    // The engine just forced a fresh sample for its clamp math; use
                    // it so the baseline predates any injection.
                    ViewportState vs = _current;
                    if (vs != null && vs.Valid && vs.SlideIndex > 0 &&
                        Environment.TickCount - vs.TickMs < 500)
                        _guardSlideIndex = vs.SlideIndex;
                }
                _guardVerifyPending = false;
                _guardVerifyAttempts = 0;
                _guardRevertedMidGesture = false;
                _guardArmed = true;
            }
            _signal.Set();
        }

        public void EndSlideGuard()
        {
            lock (_guardLock)
            {
                if (_guardArmed)
                {
                    _guardVerifyAtTick = Environment.TickCount + GuardVerifyDelayMs;
                    _guardVerifyPending = true;
                }
            }
            _signal.Set();
        }

        private bool SamplingActive()
        {
            if (Thread.VolatileRead(ref _activeGestures) > 0)
                return true;
            if (Environment.TickCount - _lastGestureEndTick < SampleTailMs)
                return true;
            return _guardVerifyPending;
        }

        private void Run()
        {
            OleMessageFilter.Register();

            while (!_stop)
            {
                bool active = SamplingActive() || _sampleRequested;
                _signal.WaitOne(active ? SampleIntervalMs : Timeout.Infinite);
                if (_stop)
                    break;

                try
                {
                    OleMessageFilter.ResetRetryBudget();
                    if (SamplingActive() || _sampleRequested)
                    {
                        _sampleRequested = false;
                        Sample();
                    }
                    else if (_log != null)
                    {
                        FlushLog();
                        if (!_loggingEnabled)
                            CloseLog(); // release the file once the user turns logging off
                    }
                }
                catch
                {
                    // Sampling is best-effort; never let it die.
                }
            }

            CloseLog();
        }

        private void Sample()
        {
            // Cursor first, COM second: TryGetViewport can take a few ms, and the
            // log's whole point is pairing the two as closely as possible.
            POINT cursor;
            GetCursorPos(out cursor);

            ViewportState vs = new ViewportState();
            double ox, oy, sx, sy, w, h;
            int zoom, slideIndex;
            if (_session.TryGetViewport(out ox, out oy, out sx, out sy, out w, out h,
                    out zoom, out slideIndex))
            {
                vs.Valid = true;
                vs.Ox = ox;
                vs.Oy = oy;
                vs.Sx = sx;
                vs.Sy = sy;
                vs.SlideWpt = w;
                vs.SlideHpt = h;
                vs.ZoomPercent = zoom;
                vs.SlideIndex = slideIndex;
            }
            vs.TickMs = Environment.TickCount;
            _current = vs;

            int revertTo = 0;
            lock (_guardLock)
            {
                if (vs.Valid && vs.SlideIndex > 0 && _guardArmed && _guardSlideIndex == 0)
                {
                    _guardSlideIndex = vs.SlideIndex;
                    DiagLog.Log("GUARD", "baseline slide=" + vs.SlideIndex);
                }

                // Escape detected while the gesture is still running: revert right
                // away instead of waiting for the end-of-gesture deadline. Only a
                // sample whose coordinates actually moved counts (SlideIndex alone
                // flickers during gestures - the log showed 12 false reverts in
                // one session, each one resetting the user's view), and at most
                // one mid-gesture revert per gesture.
                if (_guardArmed && !_guardVerifyPending && !_guardRevertedMidGesture &&
                    _guardSlideIndex > 0 && vs.Valid && vs.SlideIndex > 0 &&
                    vs.SlideIndex != _guardSlideIndex &&
                    _prevSampleValid &&
                    (vs.Ox != _prevSampleOx || vs.Oy != _prevSampleOy) &&
                    Environment.TickCount - MouseHook.LastLeftClickTick >= 400)
                {
                    DiagLog.Log("GUARD", "mid-gesture escape " + _guardSlideIndex + "->" + vs.SlideIndex);
                    _guardRevertedMidGesture = true;
                    revertTo = _guardSlideIndex;
                }

                if (vs.Valid)
                {
                    _prevSampleOx = vs.Ox;
                    _prevSampleOy = vs.Oy;
                    _prevSampleValid = true;
                }

                if (_guardVerifyPending && Environment.TickCount - _guardVerifyAtTick >= 0)
                {
                    if (!vs.Valid || vs.SlideIndex <= 0)
                    {
                        // Can't read the slide right now; retry a few times.
                        _guardVerifyAttempts++;
                        if (_guardVerifyAttempts >= 5)
                        {
                            _guardVerifyPending = false;
                            _guardArmed = false;
                        }
                        else
                        {
                            _guardVerifyAtTick = Environment.TickCount + 100;
                        }
                    }
                    else
                    {
                        // A recent real click means the user navigated on purpose
                        // (e.g. picked a thumbnail right after wheeling) - never
                        // undo that.
                        bool userNavigated = Environment.TickCount - MouseHook.LastLeftClickTick < 400;
                        bool escaped = _guardSlideIndex > 0 && vs.SlideIndex != _guardSlideIndex;
                        if (escaped && !userNavigated)
                            revertTo = _guardSlideIndex;
                        _guardVerifyPending = false;
                        _guardArmed = false;
                    }
                }
            }

            if (revertTo > 0)
                DiagLog.Log("GUARD", "revert -> slide " + revertTo);
            if (revertTo > 0 && !_session.TryGotoSlide(revertTo))
            {
                DiagLog.Log("GUARD", "revert FAILED");
                lock (_guardLock)
                {
                    if (_guardVerifyAttempts < 5 && !_guardArmed)
                    {
                        // Revert failed (PowerPoint busy): re-arm and try again.
                        _guardVerifyAttempts++;
                        _guardSlideIndex = revertTo;
                        _guardArmed = true;
                        _guardVerifyPending = true;
                        _guardVerifyAtTick = Environment.TickCount + 150;
                    }
                }
            }

            if (_loggingEnabled)
                WriteLogRow(vs, cursor.X, cursor.Y);
        }

        private void WriteLogRow(ViewportState vs, int cursorX, int cursorY)
        {
            try
            {
                if (_log == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                    bool fresh = !File.Exists(_logPath);
                    _log = new StreamWriter(_logPath, true, Encoding.UTF8);
                    if (fresh)
                        _log.WriteLine("wall_clock,elapsed_ms,phase,cursor_x_px,cursor_y_px," +
                                       "slide_origin_x_px,slide_origin_y_px,px_per_pt_x,px_per_pt_y," +
                                       "zoom_percent,slide_index,cursor_slide_x_pt,cursor_slide_y_pt");
                    _logStartTick = Environment.TickCount;
                }

                double cursorSlideX = 0.0, cursorSlideY = 0.0;
                if (vs.Valid && Math.Abs(vs.Sx) > 0.0001 && Math.Abs(vs.Sy) > 0.0001)
                {
                    // During a perfect "magnet" pan these two stay constant: the
                    // cursor keeps pointing at the same slide coordinate.
                    cursorSlideX = (cursorX - vs.Ox) / vs.Sx;
                    cursorSlideY = (cursorY - vs.Oy) / vs.Sy;
                }

                CultureInfo inv = CultureInfo.InvariantCulture;
                StringBuilder sb = new StringBuilder(160);
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", inv)).Append(',');
                sb.Append((Environment.TickCount - _logStartTick).ToString(inv)).Append(',');
                sb.Append(_phase).Append(',');
                sb.Append(cursorX.ToString(inv)).Append(',');
                sb.Append(cursorY.ToString(inv)).Append(',');
                if (vs.Valid)
                {
                    sb.Append(vs.Ox.ToString("F1", inv)).Append(',');
                    sb.Append(vs.Oy.ToString("F1", inv)).Append(',');
                    sb.Append(vs.Sx.ToString("F4", inv)).Append(',');
                    sb.Append(vs.Sy.ToString("F4", inv)).Append(',');
                    sb.Append(vs.ZoomPercent.ToString(inv)).Append(',');
                    sb.Append(vs.SlideIndex.ToString(inv)).Append(',');
                    sb.Append(cursorSlideX.ToString("F2", inv)).Append(',');
                    sb.Append(cursorSlideY.ToString("F2", inv));
                }
                else
                {
                    sb.Append(",,,,,,,");
                }
                _log.WriteLine(sb.ToString());
            }
            catch
            {
                // Logging must never break gestures.
            }
        }

        private void FlushLog()
        {
            try
            {
                if (_log != null)
                    _log.Flush();
            }
            catch
            {
            }
        }

        private void CloseLog()
        {
            try
            {
                if (_log != null)
                {
                    _log.Flush();
                    _log.Dispose();
                    _log = null;
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            try { _thread.Join(500); } // give CloseLog a chance to flush
            catch { }
        }
    }
}
