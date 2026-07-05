using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PptFigmaDrag
{
    // Consumes mouse events on a dedicated STA thread. All PowerPoint COM traffic
    // happens here, so the hook thread (and the mouse) never waits on PowerPoint.
    internal sealed class DragWorker : IDisposable
    {
        // Below this movement (px) a press is treated as a click, not a drag.
        private const int DragThresholdPx = 6;
        // Grace period so PowerPoint finishes its own native marquee handling
        // (which runs after our low-level hook saw the button-up) before we
        // overwrite the selection.
        private const int SettleDelayMs = 60;

        private readonly ConcurrentQueue<MouseEvent> _queue = new ConcurrentQueue<MouseEvent>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private readonly PowerPointSession _ppt = new PowerPointSession();
        private volatile bool _stop;

        private sealed class DragState
        {
            public int DownX;
            public int DownY;
            public SlideSnapshot Snapshot;
        }

        private DragState _drag;

        public DragWorker()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "PptFigmaDrag.Worker";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Post(MouseEvent ev)
        {
            _queue.Enqueue(ev);
            _signal.Set();
        }

        // Lets the tray menu release PowerPoint COM references (e.g. when the
        // user disables the feature) without touching COM off the worker thread.
        public void PostReleaseCom()
        {
            MouseEvent ev = new MouseEvent();
            ev.Kind = MouseEventKind.ReleaseCom;
            Post(ev);
        }

        private void Run()
        {
            // Retries outgoing COM calls that PowerPoint rejects while busy.
            OleMessageFilter.Register();

            while (!_stop)
            {
                _signal.WaitOne();
                MouseEvent ev;
                while (!_stop && _queue.TryDequeue(out ev))
                {
                    try
                    {
                        OleMessageFilter.ResetRetryBudget();
                        Handle(ev);
                    }
                    catch
                    {
                        _drag = null;
                        _ppt.InvalidateIfDead();
                    }
                }
            }
        }

        private void Handle(MouseEvent ev)
        {
            if (ev.Kind == MouseEventKind.ReleaseCom)
            {
                _drag = null;
                _ppt.Invalidate();
            }
            else if (ev.Kind == MouseEventKind.Down)
            {
                _drag = null;
                // Ctrl/Alt drags carry their own PowerPoint semantics
                // (duplicate, precise nudge, ...) - stay out of the way.
                if (ev.Ctrl || ev.Alt)
                    return;

                SlideSnapshot snapshot = _ppt.TryBeginDrag(ev.X, ev.Y);
                if (snapshot != null)
                {
                    DragState drag = new DragState();
                    drag.DownX = ev.X;
                    drag.DownY = ev.Y;
                    drag.Snapshot = snapshot;
                    _drag = drag;
                }
            }
            else
            {
                DragState drag = _drag;
                _drag = null;
                if (drag == null)
                    return;

                if (Math.Abs(ev.X - drag.DownX) < DragThresholdPx &&
                    Math.Abs(ev.Y - drag.DownY) < DragThresholdPx)
                    return; // click, not a marquee drag

                Thread.Sleep(SettleDelayMs);
                _ppt.CompleteDrag(drag.Snapshot, ev.X, ev.Y, ev.Shift);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
        }
    }
}
