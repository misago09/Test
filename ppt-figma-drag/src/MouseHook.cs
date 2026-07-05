using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PptFigmaDrag
{
    internal enum MouseEventKind
    {
        Down,
        Up
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

    // WH_MOUSE_LL global hook. System-wide pointer latency depends on how fast this
    // callback returns, so it must never touch COM, draw, or block: left button
    // down/up are copied into a lock-free queue and everything else passes straight
    // through. Mouse-move events take the single "not a button message" branch.
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

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
        private struct MSLLHOOKSTRUCT
        {
            public int PtX;
            public int PtY;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private readonly DragWorker _worker;
        private readonly HookProc _proc; // field keeps the delegate alive against GC
        private IntPtr _hook;
        private volatile bool _enabled = true;

        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        public MouseHook(DragWorker worker)
        {
            _worker = worker;
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
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _enabled)
            {
                long msg = wParam.ToInt64();
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
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
