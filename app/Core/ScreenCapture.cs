using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DesktopPetUi.Core;

public static class ScreenCapture
{
    /// <summary>捕获指定屏幕（1-based 编号；空列表=当前鼠标所在屏幕）。返回每屏的 base64 PNG，单屏失败为 null。</summary>
    public static List<string?> CaptureScreens(IReadOnlyList<int> indices, int maxDim = 1024)
    {
        var results = new List<string?>();
        try
        {
            var all = Screen.AllScreens;
            IEnumerable<Screen> targets;
            if (indices == null || indices.Count == 0)
            {
                targets = new[] { Screen.FromPoint(Cursor.Position) };
            }
            else
            {
                targets = indices.Where(i => i >= 1 && i <= all.Length).Distinct().Select(i => all[i - 1]);
            }
            foreach (var s in targets) results.Add(CaptureOne(s, maxDim));
        }
        catch
        {
            // 枚举屏幕失败（如会话无显示器）→ 空结果
        }
        return results;
    }

    private static string? CaptureOne(Screen screen, int maxDim)
    {
        try
        {
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
