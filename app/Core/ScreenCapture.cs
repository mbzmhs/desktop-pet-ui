using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace DesktopPetUi.Core;

public static class ScreenCapture
{
    public static string? CaptureCursorScreenAsBase64(int maxDim = 1024)
    {
        try
        {
            var pt = Cursor.Position;
            var screen = Screen.FromPoint(pt);
            var bounds = screen.Bounds;
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            }
            using var resized = Downscale(bmp, maxDim);
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap Downscale(Image src, int maxDim)
    {
        var w = src.Width;
        var h = src.Height;
        if (Math.Max(w, h) <= maxDim) return new Bitmap(src);
        var scale = maxDim / (double)Math.Max(w, h);
        var nw = Math.Max(1, (int)Math.Round(w * scale));
        var nh = Math.Max(1, (int)Math.Round(h * scale));
        var bmp = new Bitmap(nw, nh);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, nw, nh);
        return bmp;
    }
}