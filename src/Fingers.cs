using System;
using System.Runtime.InteropServices;

namespace ZoomScroller
{
    public interface IFingers
    {
        bool IsDown { get; }
        bool Press(double x0, double y0, double x1, double y1);
        bool Move(double x0, double y0, double x1, double y1);
        bool Hold();
        bool Release();
    }

    /// <summary>Two injected touch contacts ("fingers"). Positions are physical screen pixels.</summary>
    public class Fingers : IFingers
    {
        static bool initialized;
        readonly POINTER_TOUCH_INFO[] contacts = new POINTER_TOUCH_INFO[2];
        public bool IsDown { get; private set; }
        public int LastError { get; private set; }

        public static bool Init()
        {
            if (!initialized) initialized = Native.InitializeTouchInjection(2, Native.TOUCH_FEEDBACK_NONE);
            return initialized;
        }

        void Set(int i, double x, double y, uint flags)
        {
            var t = new POINTER_TOUCH_INFO();
            t.pointerInfo.pointerType = Native.PT_TOUCH;
            t.pointerInfo.pointerId = (uint)i;
            t.pointerInfo.pointerFlags = flags;
            int px = Native.Px(x), py = Native.Px(y);
            t.pointerInfo.ptPixelLocation = new POINT(px, py);
            t.touchMask = Native.TOUCH_MASK_CONTACTAREA | Native.TOUCH_MASK_ORIENTATION | Native.TOUCH_MASK_PRESSURE;
            t.rcContact.Left = px - 2; t.rcContact.Right = px + 2;
            t.rcContact.Top = py - 2; t.rcContact.Bottom = py + 2;
            t.orientation = 90;
            t.pressure = 32000;
            contacts[i] = t;
        }

        /// <summary>Send finger 1 before finger 0 in the next injections.</summary>
        public bool Reversed;
        readonly POINTER_TOUCH_INFO[] swapped = new POINTER_TOUCH_INFO[2];

        bool Inject()
        {
            var frame = contacts;
            if (Reversed) { swapped[0] = contacts[1]; swapped[1] = contacts[0]; frame = swapped; }
            if (Native.InjectTouchInput(2, frame)) return true;
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        const uint Contact = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT;

        public bool Press(double x1, double y1, double x2, double y2)
        {
            if (!Init()) { LastError = Marshal.GetLastWin32Error(); return false; }
            Set(0, x1, y1, Native.POINTER_FLAG_DOWN | Contact);
            Set(1, x2, y2, Native.POINTER_FLAG_DOWN | Contact);
            IsDown = Inject();
            return IsDown;
        }

        public bool Move(double x1, double y1, double x2, double y2)
        {
            if (!IsDown) return false;
            Set(0, x1, y1, Native.POINTER_FLAG_UPDATE | Contact);
            Set(1, x2, y2, Native.POINTER_FLAG_UPDATE | Contact);
            if (Inject()) return true;
            IsDown = false;  // the system cancelled the contacts (e.g. after a long gap)
            return false;
        }

        /// <summary>Re-sends the current positions (contacts are cancelled if not refreshed).</summary>
        public bool Hold()
        {
            if (!IsDown) return false;
            for (int i = 0; i < 2; i++) contacts[i].pointerInfo.pointerFlags = Native.POINTER_FLAG_UPDATE | Contact;
            if (Inject()) return true;
            IsDown = false;
            return false;
        }

        public bool Release()
        {
            if (!IsDown) return true;
            IsDown = false;
            for (int i = 0; i < 2; i++) contacts[i].pointerInfo.pointerFlags = Native.POINTER_FLAG_UP;
            return Inject();
        }
    }
}
