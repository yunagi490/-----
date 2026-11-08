using System.Text.Json;

namespace KyoshinMonitor.Map;

/// <summary>
/// 強震モニタ画像のピクセル座標 → 緯度経度の変換。経度はピクセルに比例。
/// 緯度は「等緯度経度(緯度がピクセルに比例)」と「メルカトル」を切り替えられる。
/// 値は実測して合わせる (位置合わせモード) ので、初期値は目安。
/// </summary>
public sealed class ImageCalibration
{
    private const double DefaultLon0 = 129.1, DefaultLat0 = 45.6, DefaultLonPerPx = 0.0475, DefaultLatPerPx = 0.0450;

    public double Lon0 { get; set; } = DefaultLon0;          // 画像の左端の経度
    public double Lat0 { get; set; } = DefaultLat0;          // 画像の上端の緯度
    public double LonPerPx { get; set; } = DefaultLonPerPx;  // 1ピクセルあたりの経度(度)
    public double LatPerPx { get; set; } = DefaultLatPerPx;  // 1ピクセルあたりの緯度(度。メルカトルのときはメルカトル上の度)
    public bool Mercator { get; set; }

    public (double Lat, double Lon) ToLatLon(double px, double py)
    {
        var lon = Lon0 + (px + 0.5) * LonPerPx;
        var lat = Mercator
            ? FromMercator(ToMercator(Lat0) - (py + 0.5) * LatPerPx)
            : Lat0 - (py + 0.5) * LatPerPx;
        return (lat, lon);
    }

    /// <summary>画像の中心を固定したまま拡大・縮小する</summary>
    public void ScaleAboutCenter(double fx, double fy, int w, int h)
    {
        var cLon = Lon0 + w / 2.0 * LonPerPx;
        LonPerPx *= fx;
        Lon0 = cLon - w / 2.0 * LonPerPx;

        if (Mercator)
        {
            var cM = ToMercator(Lat0) - h / 2.0 * LatPerPx;
            LatPerPx *= fy;
            Lat0 = FromMercator(cM + h / 2.0 * LatPerPx);
        }
        else
        {
            var cLat = Lat0 - h / 2.0 * LatPerPx;
            LatPerPx *= fy;
            Lat0 = cLat + h / 2.0 * LatPerPx;
        }
    }

    /// <summary>投影の種類を切り替える。画像の中心の位置と縮尺は変えない。</summary>
    public void ToggleMercator(int w, int h)
    {
        var (cLat, _) = ToLatLon(w / 2.0 - 0.5, h / 2.0 - 0.5);
        var cos = Math.Cos(cLat * Math.PI / 180.0);
        if (!Mercator)
        {
            LatPerPx /= cos; // 中心での縮尺を保つ
            Mercator = true;
            Lat0 = FromMercator(ToMercator(cLat) + h / 2.0 * LatPerPx);
        }
        else
        {
            LatPerPx *= cos;
            Mercator = false;
            Lat0 = cLat + h / 2.0 * LatPerPx;
        }
    }
    public ImageCalibration Clone() => (ImageCalibration)MemberwiseClone();

    public void CopyFrom(ImageCalibration o)
    {
        Lon0 = o.Lon0;
        Lat0 = o.Lat0;
        LonPerPx = o.LonPerPx;
        LatPerPx = o.LatPerPx;
        Mercator = o.Mercator;
    }

    public void Reset()
    {
        Lon0 = DefaultLon0;
        Lat0 = DefaultLat0;
        LonPerPx = DefaultLonPerPx;
        LatPerPx = DefaultLatPerPx;
        Mercator = false;
    }

    public override string ToString() =>
        $"{(Mercator ? "メルカトル" : "等緯度経度")}  左端経度 {Lon0:0.000}  上端緯度 {Lat0:0.000}  経度/px {LonPerPx:0.00000}  緯度/px {LatPerPx:0.00000}";

    private static double ToMercator(double latDeg) =>
        Math.Log(Math.Tan(Math.PI / 4 + latDeg * Math.PI / 360.0)) * 180.0 / Math.PI;

    private static double FromMercator(double m) =>
        (2 * Math.Atan(Math.Exp(m * Math.PI / 180.0)) - Math.PI / 2) * 180.0 / Math.PI;

    // ---- 保存 / 読み込み ----

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KyoshinMonitor", "calibration.json");

    public static bool HasSaved => File.Exists(FilePath);

    public static ImageCalibration LoadOrDefault()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ImageCalibration>(File.ReadAllText(FilePath)) ?? new ImageCalibration();
        }
        catch { /* 壊れていたら初期値 */ }
        return new ImageCalibration();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}