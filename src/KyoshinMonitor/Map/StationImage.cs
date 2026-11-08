using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KyoshinMonitor.Map;

/// <summary>観測点1つ分のドット。位置は画像上のピクセル座標 (小数)、Rank は震度の高さの目安 (大きいほど強い)。</summary>
public readonly record struct RawDot(double X, double Y, Color Color, int Rank);

public static class StationImage
{
    /// <summary>この数以下のピクセルの塊は、1つの観測点として扱う。これより大きい塊はピクセルごとに分ける。</summary>
    private const int MaxClusterSize = 4;

    /// <summary>画像から観測点のドットを集める。震度の低い順に並べて返す (描くとき、強い点が手前に来る)。</summary>
    public static List<RawDot> ExtractDots(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var colors = new Color?[w * h];

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * data.Stride + x * 4;
                    byte b = bytes[i], g = bytes[i + 1], r = bytes[i + 2], a = bytes[i + 3];
                    if (a >= 200 && IsScaleColor(r, g, b)) colors[y * w + x] = Color.FromArgb(r, g, b);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // 隣り合うピクセル (斜めも含む) を1つの塊にまとめる
        var result = new List<RawDot>();
        var visited = new bool[w * h];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (var start = 0; start < colors.Length; start++)
        {
            if (colors[start] is null || visited[start]) continue;

            members.Clear();
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                members.Add(cur);
                int cx = cur % w, cy = cur / w;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var ni = ny * w + nx;
                        if (colors[ni] is null || visited[ni]) continue;
                        visited[ni] = true;
                        stack.Push(ni);
                    }
                }
            }

            if (members.Count <= MaxClusterSize)
            {
                // 1つの観測点: 塊の中心に、いちばん強い色で1つ
                double sx = 0, sy = 0;
                var bestColor = colors[members[0]]!.Value;
                var bestRank = Rank(bestColor);
                foreach (var m in members)
                {
                    sx += m % w;
                    sy += m / w;
                    var c = colors[m]!.Value;
                    var rank = Rank(c);
                    if (rank > bestRank)
                    {
                        bestRank = rank;
                        bestColor = c;
                    }
                }
                result.Add(new RawDot(sx / members.Count, sy / members.Count, bestColor, bestRank));
            }
            else
            {
                // 大きな塊 (密集やロゴ): まとめずにピクセルごと
                foreach (var m in members)
                {
                    var c = colors[m]!.Value;
                    result.Add(new RawDot(m % w, m / w, c, Rank(c)));
                }
            }
        }

        result.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        return result;
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

    /// <summary>
    /// 色の強さの目安。色の目盛りは、青(弱い)→水色→緑→黄→橙→赤(強い)→赤紫(最大級)と、
    /// 色相が下がっていくので、それを順位にする。並べ替えと、塊の中の代表色の選択にだけ使う。
    /// </summary>
    private static int Rank(Color c)
    {
        var hue = c.GetHue(); // 0〜360
        if (hue >= 250) return 300; // 紫〜赤紫
        return (int)Math.Clamp(240 - hue, 0, 240);
    }
}