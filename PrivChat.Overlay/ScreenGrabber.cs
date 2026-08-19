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
    private const int SwHide = 0;
    private const int SwShowNoActivate = 8;

    // CopyFromScreen/BitBlt does not reliably honor WDA_EXCLUDEFROMCAPTURE, so the
    // overlay is hidden for one DWM frame, captured, then restored without activating.
    // That keeps the pairing code and chat off the image sent to Gemini.
    public static byte[] CaptureVirtualScreenJpeg(IntPtr hideHwnd)
    {
        var hidden = false;
        try
        {
            if (hideHwnd != IntPtr.Zero)
            {
                ShowWindow(hideHwnd, SwHide);
                hidden = true;
                _ = DwmFlush();
            }

            return CaptureCore();
        }
        finally
        {
            if (hidden)
            {
                ShowWindow(hideHwnd, SwShowNoActivate);
            }
        }
    }

    private static byte[] CaptureCore()
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
                if (captured.Width > MaxWidth)
                {
                    var scaledHeight = Math.Max(1, (int)Math.Round(captured.Height * (double)MaxWidth / captured.Width));
                    final = new Bitmap(captured, MaxWidth, scaledHeight);
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
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
