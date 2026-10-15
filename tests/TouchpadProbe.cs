using System;
using System.Runtime.InteropServices;

namespace MousePinch
{
    /// <summary>Can Windows synthesize touchpad (precision touchpad) pointers? Tries CreateSyntheticPointerDevice
    /// with each pointer type and reports what the system accepts. Injects nothing.</summary>
    static class TouchpadProbe
    {
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);
        [DllImport("user32.dll")] static extern void DestroySyntheticPointerDevice(IntPtr device);

        static int Main()
        {
            foreach (var t in new[] { Tuple.Create(1, "PT_POINTER"), Tuple.Create(2, "PT_TOUCH"), Tuple.Create(3, "PT_PEN"),
                                      Tuple.Create(4, "PT_MOUSE"), Tuple.Create(5, "PT_TOUCHPAD") })
            {
                IntPtr h = CreateSyntheticPointerDevice(t.Item1, 2, 1);
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine("{0,-12} {1}", t.Item2, h != IntPtr.Zero ? "accepted" : "refused (error " + err + ")");
                if (h != IntPtr.Zero) DestroySyntheticPointerDevice(h);
            }
            return 0;
        }
    }
}
