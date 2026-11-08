using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace KyoshinMonitor.Map;
/// <summary>地図に描くEEW (震源と、発生時刻からの波の広がり)</summary>
public sealed record EewMarker(double Lat, double Lon, double DepthKm, DateTimeOffset OriginTime, bool IsWarn);

/// <summary>専用地図 (ウェブメルカトル)。ベース地図はBitmapにキャッシュし、その上にオーバーレイを重ねる。</summary>
public sealed class MapControl : Control
{
    // 表示範囲 (日本周辺)
    private const double MinLon = 122.5, MaxLon = 146.5, MinLat = 24.0, MaxLat = 45.8;
    private const int ViewMargin = 12;

    private static readonly Color SeaColor = Color.FromArgb(20, 24, 32);
    private static readonly Color LandColor = Color.FromArgb(46, 50, 60);
    private static readonly Color BorderColor = Color.FromArgb(96, 102, 118);

    private GeoJsonMap? _map;
    private string _message = "地図データを読み込み中...";
    private Bitmap? _baseBitmap;
    private EewMarker? _eew;

    private IReadOnlyList<RawDot> _rawDots = Array.Empty<RawDot>();
    private ImageCalibration? _calibration;
    private bool _useLandMask = true;
    private Bitmap? _dotsBitmap;
    private string? _overlayText;
    private const int LandMargin = 6; // 陸地からこの距離(px)以内のドットだけ描く
    // 波の速さ(仮定)。気象庁の走時表とは一致しない概算値で、見た目の目安用。
    private const double PWaveKmPerSec = 7.0;
    private const double SWaveKmPerSec = 4.0;
    private const double MaxDrawKm = 1500;
    private double _scale, _offsetX, _offsetY; // メルカトル平面(0..1) → 画面

    public MapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SeaColor;
    }

    public void SetMap(GeoJsonMap map)
    {
        _map = map;
        RebuildBase();
        Invalidate();
    }

    public void SetMessage(string message)
    {
        _map = null;
        _message = message;
        RebuildBase();
        Invalidate();
    }
    /// <summary>EEWの表示を更新する。null で消す。</summary>
    public void SetEew(EewMarker? marker)
    {
        _eew = marker;
        Invalidate();
    }
    /// <summary>観測点ドットを更新する。useLandMask が true なら、陸地の近くのドットだけを描く。</summary>
    public void SetStationDots(IReadOnlyList<RawDot> dots, ImageCalibration calibration, bool useLandMask)
    {
        _rawDots = dots;
        _calibration = calibration;
        _useLandMask = useLandMask;
        RebuildDots();
        Invalidate();
    }

    /// <summary>地図の左下に表示する文字 (位置合わせの案内用)。null で消す。</summary>
    public void SetOverlayText(string? text)
    {
        _overlayText = text;
        Invalidate();
    }
    /// <summary>
    /// 位置合わせの自動調整。陸地の上に載るドットが最も多くなるように、
    /// パラメーターを少しずつ動かして探す (最初は大きく、改善しなければ細かく)。
    /// 結果は cal に書き込まれ、最終的に陸地に載ったドットの割合(0〜1)を返す。
    /// </summary>
    public double AutoFit(ImageCalibration cal)
    {
        if (_map is null || _baseBitmap is null || _rawDots.Count == 0 || _scale == 0) return 0;
        var (land, stride) = ReadPixels(_baseBitmap);
        var dots = _rawDots;

        int Score(int margin)
        {
            var n = 0;
            foreach (var d in dots)
            {
                var (lat, lon) = cal.ToLatLon(d.X, d.Y);
                if (Math.Abs(lat) > 85) continue;
                if (NearLand(land, stride, LatLonToScreen(lat, lon), margin)) n++;
            }
            return n;
        }

        double Get(int i) => i switch { 0 => cal.Lon0, 1 => cal.Lat0, 2 => cal.LonPerPx, _ => cal.LatPerPx };

        void Set(int i, double v)
        {
            switch (i)
            {
                case 0: cal.Lon0 = v; break;
                case 1: cal.Lat0 = v; break;
                // 点が1か所に潰れるのを防ぐため、拡大率には範囲を設ける
                case 2: cal.LonPerPx = Math.Clamp(v, 0.040, 0.056); break;
                default: cal.LatPerPx = Math.Clamp(v, 0.030, 0.075); break;
            }
        }

        // 探索幅: 左端経度, 上端緯度 (度) / 経度あたり, 緯度あたり (度/px)
        var steps = new[] { 1.0, 1.0, 0.002, 0.002 };
        for (var iter = 0; iter < 300 && steps[0] > 0.004; iter++)
        {
            // 最初は海岸線から離れていても陸地とみなして大づかみに合わせ、だんだん厳しくする
            var margin = steps[0] > 0.2 ? 6 : steps[0] > 0.05 ? 3 : 1;
            var best = Score(margin);
            var improved = false;
            for (var i = 0; i < 4; i++)
            {
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    var before = Get(i);
                    Set(i, before + sign * steps[i]);
                    var s = Score(margin);
                    if (s > best)
                    {
                        best = s;
                        improved = true;
                    }
                    else
                    {
                        Set(i, before);
                    }
                }
            }
            if (!improved)
            {
                for (var i = 0; i < 4; i++) steps[i] *= 0.5;
            }
        }
        return Score(1) / (double)dots.Count;
    }

    /// <summary>緯度経度 → 画面座標 (観測点・震源・EEWの円などの描画で使う)</summary>
    public PointF LatLonToScreen(double lat, double lon)
    {
        var (x, y) = Mercator(lat, lon);
        return new PointF((float)(x * _scale + _offsetX), (float)(y * _scale + _offsetY));
    }

    private static (double X, double Y) Mercator(double lat, double lon)
    {
        var x = (lon + 180.0) / 360.0;
        var latRad = lat * Math.PI / 180.0;
        var y = 0.5 - Math.Log(Math.Tan(Math.PI / 4 + latRad / 2)) / (2 * Math.PI);
        return (x, y);
    }

    private void UpdateView()
    {
        var (x1, y1) = Mercator(MaxLat, MinLon); // 左上
        var (x2, y2) = Mercator(MinLat, MaxLon); // 右下
        var w = Math.Max(1, ClientSize.Width - ViewMargin * 2);
        var h = Math.Max(1, ClientSize.Height - ViewMargin * 2);
        _scale = Math.Min(w / (x2 - x1), h / (y2 - y1));
        _offsetX = (ClientSize.Width - (x2 - x1) * _scale) / 2 - x1 * _scale;
        _offsetY = (ClientSize.Height - (y2 - y1) * _scale) / 2 - y1 * _scale;
    }

    private void RebuildBase()
    {
        _baseBitmap?.Dispose();
        _baseBitmap = null;
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;

        UpdateView();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(SeaColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_map is not null)
            {
                using var land = new SolidBrush(LandColor);
                using var border = new Pen(BorderColor, 1f) { LineJoin = LineJoin.Round };
                foreach (var poly in _map.Polygons)
                {
                    using var path = new GraphicsPath(FillMode.Alternate);
                    foreach (var ring in poly.Rings)
                    {
                        var pts = Project(ring);
                        if (pts.Count >= 3) path.AddPolygon(pts.ToArray());
                    }
                    if (path.PointCount < 3) continue;
                    g.FillPath(land, path);
                    g.DrawPath(border, path);
                }
            }
        }
        _baseBitmap = bmp;
        RebuildDots();
    }

    /// <summary>画面上で0.6px未満しか動かない点は間引く。極端な緯度は無視する。</summary>
    private List<PointF> Project(GeoPoint[] ring)
    {
        var result = new List<PointF>(ring.Length / 2 + 4);
        foreach (var p in ring)
        {
            if (Math.Abs(p.Lat) > 85) continue;
            var s = LatLonToScreen(p.Lat, p.Lon);
            if (result.Count > 0)
            {
                var last = result[^1];
                if (Math.Abs(s.X - last.X) < 0.6f && Math.Abs(s.Y - last.Y) < 0.6f) continue;
            }
            result.Add(s);
        }
        return result;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_baseBitmap is not null) g.DrawImageUnscaled(_baseBitmap, 0, 0);
        else g.Clear(SeaColor);
        if (_dotsBitmap is not null) g.DrawImageUnscaled(_dotsBitmap, 0, 0);

        if (_map is null)
        {
            TextRenderer.DrawText(g, _message, Font, ClientRectangle, Color.FromArgb(180, 186, 198),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        if (_overlayText is not null)
        {
            var box = new Rectangle(8, Height - 96, Width - 16, 88);
            using var bg = new SolidBrush(Color.FromArgb(200, 18, 20, 26));
            g.FillRectangle(bg, box);
            TextRenderer.DrawText(g, _overlayText, Font, Rectangle.Inflate(box, -6, -4), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        if(_eew is { } eew) DrawEew(g, eew);
    }
    private void RebuildDots()
    {
        _dotsBitmap?.Dispose();
        _dotsBitmap = null;
        if (_calibration is null || _rawDots.Count == 0 || _scale == 0 || ClientSize.Width < 2 || ClientSize.Height < 2) return;

        byte[]? land = null;
        var stride = 0;
        if (_useLandMask && _map is not null && _baseBitmap is not null)
            (land, stride) = ReadPixels(_baseBitmap);

        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        var brushes = new Dictionary<int, SolidBrush>();
        using var outline = new Pen(Color.FromArgb(170, 10, 12, 18), 1f);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var d in _rawDots)
            {
                var (lat, lon) = _calibration.ToLatLon(d.X, d.Y);
                var p = LatLonToScreen(lat, lon);
                if (land is not null && !NearLand(land, stride, p, LandMargin)) continue;

                var key = d.Color.ToArgb();
                if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = new SolidBrush(d.Color);
                var size = d.Rank >= 130 ? 6f : 4.5f; // 震度の高い点は少し大きく描く
                g.FillEllipse(brush, p.X - size / 2, p.Y - size / 2, size, size);
                g.DrawEllipse(outline, p.X - size / 2, p.Y - size / 2, size, size);
            }
        }
        finally
        {
            foreach (var b in brushes.Values) b.Dispose();
        }
        _dotsBitmap = bmp;
    }

    private static (byte[] Bytes, int Stride) ReadPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return (bytes, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>点の周囲に、海の色以外 (陸地や県境) のピクセルがあるか</summary>
    private bool NearLand(byte[] px, int stride, PointF p, int margin)
    {
        var cx = (int)p.X;
        var cy = (int)p.Y;
        for (var y = Math.Max(0, cy - margin); y <= Math.Min(ClientSize.Height - 1, cy + margin); y++)
        {
            for (var x = Math.Max(0, cx - margin); x <= Math.Min(ClientSize.Width - 1, cx + margin); x++)
            {
                var i = y * stride + x * 4;
                if (px[i] != SeaColor.B || px[i + 1] != SeaColor.G || px[i + 2] != SeaColor.R) return true;
            }
        }
        return false;
    }

    private void DrawEew(Graphics g, EewMarker m)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var center = LatLonToScreen(m.Lat, m.Lon);
        var elapsed = (DateTimeOffset.Now - m.OriginTime).TotalSeconds;

        if (elapsed >= 0)
        {
            // メルカトルでは、緯度ごとに距離の縮尺が cos(緯度) で変わる
            var pxPerKm = _scale / (40075.0 * Math.Cos(m.Lat * Math.PI / 180.0));

            var pKm = SurfaceRadiusKm(PWaveKmPerSec * elapsed, m.DepthKm);
            var sKm = SurfaceRadiusKm(SWaveKmPerSec * elapsed, m.DepthKm);
            if (sKm > 0 && sKm < MaxDrawKm)
                DrawWave(g, center, (float)(sKm * pxPerKm), Color.FromArgb(255, 90, 90), Color.FromArgb(40, 255, 80, 80));
            if (pKm > 0 && pKm < MaxDrawKm)
                DrawWave(g, center, (float)(pKm * pxPerKm), Color.FromArgb(90, 170, 255), null);
        }

        // 震源 (×印)
        const float r = 8f;
        using var outline = new Pen(Color.White, 5f);
        using var cross = new Pen(m.IsWarn ? Color.FromArgb(230, 30, 30) : Color.FromArgb(255, 150, 0), 3f);
        foreach (var pen in new[] { outline, cross })
        {
            g.DrawLine(pen, center.X - r, center.Y - r, center.X + r, center.Y + r);
            g.DrawLine(pen, center.X - r, center.Y + r, center.X + r, center.Y - r);
        }
    }

    /// <summary>震源からの距離(深さ込み)を、地表での半径に直す</summary>
    private static double SurfaceRadiusKm(double distanceKm, double depthKm)
    {
        var d2 = distanceKm * distanceKm - depthKm * depthKm;
        return d2 > 0 ? Math.Sqrt(d2) : 0;
    }

    private static void DrawWave(Graphics g, PointF c, float radius, Color line, Color? fill)
    {
        var rect = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        if (fill is { } f)
        {
            using var b = new SolidBrush(f);
            g.FillEllipse(b, rect);
        }
        using var p = new Pen(line, 2f);
        g.DrawEllipse(p, rect);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RebuildBase();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseBitmap?.Dispose();
            _dotsBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}