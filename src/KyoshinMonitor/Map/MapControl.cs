using System.Drawing.Drawing2D;
using System.Drawing;
using System.Windows.Forms;
namespace KyoshinMonitor.Map;

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

        if (_map is null)
        {
            TextRenderer.DrawText(g, _message, Font, ClientRectangle, Color.FromArgb(180, 186, 198),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        // ここに観測点・震源・EEWの円などのオーバーレイを描いていく
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RebuildBase();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _baseBitmap?.Dispose();
        base.Dispose(disposing);
    }
}