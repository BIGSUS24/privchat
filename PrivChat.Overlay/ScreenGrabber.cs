using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PrivChat.Overlay;

internal static class ScreenGrabber
{
    public const string JpegMimeType = "image/jpeg";

    // Keeps the base64 payload comfortably inside Gemini's 20 MB request limit
    // while staying legible for on-screen text.
    private const int MaxWidth = 1920;
    private const long JpegQuality = 90L;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    // CopyFromScreen/BitBlt does not reliably honor WDA_EXCLUDEFROMCAPTURE, so the
    // overlay must leave the screen for one DWM frame while capturing. Hiding via
    // ShowWindow(SW_HIDE/SW_SHOW) is NOT safe here: hide->show makes Windows fall
    // back to drawing the protected window as an opaque BLACK BOX in captures,
    // even though GetWindowDisplayAffinity still reports WDA_EXCLUDEFROMCAPTURE
    // (same defect as electron#29085). DWM cloaking removes the window from the
    // composition without touching its visibility/presentation state, so the
    // capture exclusion survives. Affinity is re-asserted afterwards anyway.
    public static byte[] CaptureVirtualScreenJpeg(IntPtr hideHwnd) => CaptureVirtualScreenJpeg(hideHwnd, MaxWidth);

    // Local vision models price an image in tokens and encode on the CPU, so callers
    // targeting them pass a smaller budget (the local LLM tab uses 1024).
    public static byte[] CaptureVirtualScreenJpeg(IntPtr hideHwnd, int maxWidth)
    {
        var cloaked = false;
        var obscured = false;
        NativeRect original = default;
        try
        {
            if (hideHwnd != IntPtr.Zero)
            {
                // Ref-counted: if the user has Ctrl+H-cloaked the overlay, this
                // acquire keeps it cloaked and the release below leaves the user's
                // hide intact. Only when we are the sole holder does the window
                // actually reappear. A false return means cloaking is NOT in place
                // and the off-screen fallback must run.
                cloaked = WindowCloak.Acquire(hideHwnd);
                obscured = cloaked;
                if (!cloaked)
                {
                    // Cloak unavailable: move the window outside the virtual desktop
                    // for the capture frame instead of hiding it. NEVER use
                    // ShowWindow(SW_HIDE/SW_SHOW) here: hide->show makes Windows fall
                    // back to drawing the protected window as an opaque BLACK BOX in
                    // captures even while GetWindowDisplayAffinity still reports
                    // WDA_EXCLUDEFROMCAPTURE (same defect as electron#29085).
                    // Position moves do not disturb the window's presentation state.
                    obscured = GetWindowRect(hideHwnd, out original) && MoveOffScreen(hideHwnd);
                }

                _ = DwmFlush();
            }

            return CaptureCore(maxWidth);
        }
        finally
        {
            if (obscured && hideHwnd != IntPtr.Zero)
            {
                if (cloaked)
                {
                    WindowCloak.Release(hideHwnd);
                }
                else
                {
                    SetWindowPos(hideHwnd, IntPtr.Zero,
                        original.Left, original.Top, 0, 0,
                        SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpNoCopyBits);
                }

                // Free insurance: force DWM to re-register the capture exclusion in
                // case the obscure/restore cycle disturbed its presentation state.
                _ = SetWindowDisplayAffinity(hideHwnd, WdaExcludeFromCapture);
            }
        }
    }

    private static bool MoveOffScreen(IntPtr hwnd)
    {
        var vx = GetSystemMetrics(SmXVirtualScreen);
        var vy = GetSystemMetrics(SmYVirtualScreen);
        return SetWindowPos(hwnd, IntPtr.Zero,
            vx + GetSystemMetrics(SmCxVirtualScreen) + OffsetBeyondDesktop,
            vy + GetSystemMetrics(SmCyVirtualScreen) + OffsetBeyondDesktop,
            0, 0, SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpNoCopyBits);
    }

    private const int OffsetBeyondDesktop = 200;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoCopyBits = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static byte[] CaptureCore(int maxWidth)
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("No display could be captured.");
        }

        var captured = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(captured))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height));
            }

            Bitmap final = captured;
            try
            {
                if (captured.Width > maxWidth)
                {
                    var scaledHeight = Math.Max(1, (int)Math.Round(captured.Height * (double)maxWidth / captured.Width));
                    final = new Bitmap(captured, maxWidth, scaledHeight);
                }

                var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                    ?? throw new InvalidOperationException("JPEG encoder is missing.");
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                using var stream = new MemoryStream();
                final.Save(stream, codec, parameters);
                return stream.ToArray();
            }
            finally
            {
                if (!ReferenceEquals(final, captured))
                {
                    final.Dispose();
                }
            }
        }
        finally
        {
            captured.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private const uint WdaExcludeFromCapture = 0x00000011;
}
