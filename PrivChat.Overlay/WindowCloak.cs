using System.Runtime.InteropServices;

namespace PrivChat.Overlay;

// Ref-counted DWM cloaking. Two independent features hide the window:
// Ctrl+H (user intent, held indefinitely) and ScreenGrabber (one capture frame).
// A naive cloak(true)/cloak(false) pair would let the short-lived caller un-hide
// the window the user asked to keep hidden. The counter makes them compose:
// the window stays cloaked until every holder releases.
internal static class WindowCloak
{
    private const int DwmwaCloak = 13;
    private static readonly object Gate = new();
    private static int _count;

    public static bool IsCloaked
    {
        get { lock (Gate) { return _count > 0; } }
    }

    // Returns true when the window is actually cloaked (this call or an earlier one).
    // A failed DWM call must return false so callers fall back to another obscuring
    // method instead of trusting a protection that is not in place.
    public static bool Acquire(IntPtr hwnd)
    {
        bool apply;
        lock (Gate)
        {
            _count++;
            apply = _count == 1;
        }

        if (!apply)
        {
            return true;
        }

        if (hwnd == IntPtr.Zero || !SetCloak(hwnd, true))
        {
            lock (Gate)
            {
                _count--;
            }

            return false;
        }

        return true;
    }

    public static void Release(IntPtr hwnd)
    {
        bool remove;
        lock (Gate)
        {
            if (_count == 0)
            {
                return;
            }

            _count--;
            remove = _count == 0;
        }

        if (remove && hwnd != IntPtr.Zero)
        {
            SetCloak(hwnd, false);
        }
    }

    private static bool SetCloak(IntPtr hwnd, bool cloak)
    {
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
