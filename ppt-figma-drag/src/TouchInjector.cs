using System;
using System.Runtime.InteropServices;

namespace PptFigmaDrag
{
    // Thin wrapper over the Windows touch-injection API (Windows 8+). Every
    // gesture uses TWO synthetic contacts so PowerPoint's DirectManipulation
    // treats them as viewport pan/zoom - never as a shape drag, marquee or ink.
    // Only ever called from the GestureEngine thread.
    internal sealed class TouchInjector
    {
        private const int PT_TOUCH = 2;
        private const uint POINTER_FLAG_INRANGE = 0x00000002;
        private const uint POINTER_FLAG_INCONTACT = 0x00000004;
        private const uint POINTER_FLAG_CANCELED = 0x00008000;
        private const uint POINTER_FLAG_DOWN = 0x00010000;
        private const uint POINTER_FLAG_UPDATE = 0x00020000;
        private const uint POINTER_FLAG_UP = 0x00040000;
        private const uint TOUCH_FEEDBACK_NONE = 3;
        private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
        private const uint TOUCH_MASK_ORIENTATION = 0x00000002;
        private const uint TOUCH_MASK_PRESSURE = 0x00000004;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

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
        private struct POINTER_INFO
        {
            public int PointerType;
            public uint PointerId;
            public uint FrameId;
            public uint PointerFlags;
            public IntPtr SourceDevice;
            public IntPtr HwndTarget;
            public POINT PtPixelLocation;
            public POINT PtHimetricLocation;
            public POINT PtPixelLocationRaw;
            public POINT PtHimetricLocationRaw;
            public uint Time;
            public uint HistoryCount;
            public int InputData;
            public uint KeyStates;
            public ulong PerformanceCount;
            public int ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO PointerInfo;
            public uint TouchFlags;
            public uint TouchMask;
            public RECT Contact;
            public RECT ContactRaw;
            public uint Orientation;
            public uint Pressure;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeTouchInjection(uint maxCount, uint mode);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private readonly POINTER_TOUCH_INFO[] _frame = new POINTER_TOUCH_INFO[2];
        private int _x0, _y0, _x1, _y1; // last injected contact positions
        private bool _contactsDown;

        public bool ContactsDown
        {
            get { return _contactsDown; }
        }

        public bool Initialize()
        {
            try
            {
                return InitializeTouchInjection(2, TOUCH_FEEDBACK_NONE);
            }
            catch (EntryPointNotFoundException)
            {
                return false; // pre-Windows 8
            }
        }

        // Shift the contact PAIR back onto the virtual screen. Clamping each
        // contact separately would change their distance, which DirectManipulation
        // reads as a pinch - a pan would suddenly zoom at screen edges.
        private static void ClampPairToVirtualScreen(ref int x0, ref int y0, ref int x1, ref int y1)
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1;
            int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1;

            int shiftX = 0;
            int minX = Math.Min(x0, x1);
            int maxX = Math.Max(x0, x1);
            if (minX < left) shiftX = left - minX;
            else if (maxX > right) shiftX = right - maxX;

            int shiftY = 0;
            int minY = Math.Min(y0, y1);
            int maxY = Math.Max(y0, y1);
            if (minY < top) shiftY = top - minY;
            else if (maxY > bottom) shiftY = bottom - maxY;

            x0 += shiftX; x1 += shiftX;
            y0 += shiftY; y1 += shiftY;
        }

        private void FillContact(int index, uint id, int x, int y, uint flags)
        {
            POINTER_TOUCH_INFO info = new POINTER_TOUCH_INFO();
            info.PointerInfo.PointerType = PT_TOUCH;
            info.PointerInfo.PointerId = id;
            info.PointerInfo.PointerFlags = flags;
            info.PointerInfo.PtPixelLocation.X = x;
            info.PointerInfo.PtPixelLocation.Y = y;
            info.TouchFlags = 0;
            info.TouchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
            info.Contact.Left = x - 2;
            info.Contact.Top = y - 2;
            info.Contact.Right = x + 2;
            info.Contact.Bottom = y + 2;
            info.Orientation = 90;
            info.Pressure = 512;
            _frame[index] = info;
        }

        public bool Down(int x0, int y0, int x1, int y1)
        {
            ClampPairToVirtualScreen(ref x0, ref y0, ref x1, ref y1);
            uint flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            FillContact(0, 1, x0, y0, flags);
            FillContact(1, 2, x1, y1, flags);
            if (!InjectTouchInput(2, _frame))
            {
                // Contacts may be stuck from an earlier failed Up: cancel and retry once.
                Cancel(x0, y0, x1, y1);
                FillContact(0, 1, x0, y0, flags);
                FillContact(1, 2, x1, y1, flags);
                if (!InjectTouchInput(2, _frame))
                    return false;
            }
            _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1;
            _contactsDown = true;
            return true;
        }

        public bool Move(int x0, int y0, int x1, int y1)
        {
            if (!_contactsDown)
                return false;
            ClampPairToVirtualScreen(ref x0, ref y0, ref x1, ref y1);
            uint flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            FillContact(0, 1, x0, y0, flags);
            FillContact(1, 2, x1, y1, flags);
            if (!InjectTouchInput(2, _frame))
                return false;
            _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1;
            return true;
        }

        // Re-inject the last positions unchanged; a couple of these right before
        // Up zero out the gesture velocity so DirectManipulation adds no inertia.
        public bool Hold()
        {
            return Move(_x0, _y0, _x1, _y1);
        }

        public bool Up()
        {
            if (!_contactsDown)
                return true;
            FillContact(0, 1, _x0, _y0, POINTER_FLAG_UP);
            FillContact(1, 2, _x1, _y1, POINTER_FLAG_UP);
            bool ok = InjectTouchInput(2, _frame);
            if (!ok)
                Cancel(_x0, _y0, _x1, _y1); // last resort so contacts don't stay stuck
            _contactsDown = false;
            return ok;
        }

        private void Cancel(int x0, int y0, int x1, int y1)
        {
            FillContact(0, 1, x0, y0, POINTER_FLAG_UP | POINTER_FLAG_CANCELED);
            FillContact(1, 2, x1, y1, POINTER_FLAG_UP | POINTER_FLAG_CANCELED);
            InjectTouchInput(2, _frame);
        }
    }
}
