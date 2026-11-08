using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KyoshinMonitor.Map;

/// <summary>強震モニタ画像上の1ピクセル分のドット (画像のピクセル座標と色)</summary>
public readonly record struct RawDot(int X, int Y, Color Color);

public static class StationImage
{
    /// <summary>画像から、色の目盛りに乗っているピクセルを集める (黒い文字・灰色の線・白地は除く)</summary>
    public static List<RawDot> ExtractDots(Bitmap bmp)
    {
        var dots = new List<RawDot>();
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var y = 0; y < data.Height; y++)
            {
                for (var x = 0; x < data.Width; x++)
                {
                    var i = y * data.Stride + x * 4;
                    byte b = bytes[i], g = bytes[i + 1], r = bytes[i + 2], a = bytes[i + 3];
                    if (a < 200) continue;
                    if (IsScaleColor(r, g, b)) dots.Add(new RawDot(x, y, Color.FromArgb(r, g, b)));
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return dots;
    }

    /// <summary>彩度が高く、暗すぎない色 (KyoshinMonitorLib の判定に合わせた条件)</summary>
    private static bool IsScaleColor(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max == 0) return false;
        var s = (max - min) / (double)max;
        var v = max / 255.0;
        return s > 0.75 && v > 0.1;
    }
}