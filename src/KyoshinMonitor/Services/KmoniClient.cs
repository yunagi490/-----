using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;

namespace KyoshinMonitor;

/// <summary>
/// 防災科研 強震モニタの配信画像を取得して重ね合わせる。
/// 画像は全国版 (約352x400) の透過GIFで、ベース地図・リアルタイム震度・EEW予測震度・P/S波円を同サイズで重ねられる。
/// </summary>
public sealed class KmoniClient : IDisposable
{
    /// <summary>直近の取得失敗の内容 (デバッグ表示用)</summary>
    public string LastError { get; private set; } = "";

    private const string Host = "http://www.kmoni.bosai.go.jp";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Bitmap? _baseMap;

    public KmoniClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KyoshinMonitorSample/0.1");
    }

    /// <summary>サーバー側の最新データ時刻 (JST)</summary>
    public async Task<DateTime> GetLatestTimeAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{Host}/webservice/server/pros/latest.json", ct);
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement.GetProperty("latest_time").GetString()!;
        return DateTime.ParseExact(s, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>指定時刻のフレームを合成して返す。リアルタイム震度が取れなければnull。</summary>
    public async Task<Bitmap?> RenderAsync(DateTime t, bool includeEew, CancellationToken ct)
    {
        var d = t.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var ts = t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        _baseMap ??= await FetchGifAsync($"{Host}/data/map_img/CommonImg/base_map_w.gif", ct);

        var realtimeTask = FetchGifAsync($"{Host}/data/map_img/RealTimeImg/jma_s/{d}/{ts}.jma_s.gif", ct, record: true);
        Task<Bitmap?> estTask = includeEew
            ? FetchGifAsync($"{Host}/data/map_img/EstShindoImg/eew/{d}/{ts}.eew.gif", ct)
            : Task.FromResult<Bitmap?>(null);
        Task<Bitmap?> psTask = includeEew
            ? FetchGifAsync($"{Host}/data/map_img/PSWaveImg/eew/{d}/{ts}.eew.gif", ct)
            : Task.FromResult<Bitmap?>(null);

        await Task.WhenAll(realtimeTask, estTask, psTask);

        using var realtime = realtimeTask.Result;
        using var est = estTask.Result;
        using var ps = psTask.Result;
        if (realtime is null) return null;

        var w = _baseMap?.Width ?? realtime.Width;
        var h = _baseMap?.Height ?? realtime.Height;
        var frame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(frame);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.FromArgb(20, 24, 32));
        if (_baseMap is not null) g.DrawImage(_baseMap, 0, 0, w, h);
        if (est is not null) g.DrawImage(est, 0, 0, w, h);      // 予測震度 (EEW)
        g.DrawImage(realtime, 0, 0, w, h);                       // 実測リアルタイム震度
        if (ps is not null) g.DrawImage(ps, 0, 0, w, h);         // P波/S波到達円
        return frame;
    }

    private async Task<Bitmap?> FetchGifAsync(string url, CancellationToken ct, bool record = false)
    {
        try
        {
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 404 = その秒のデータなし/EEWなし
                if (record) LastError = $"HTTP {(int)res.StatusCode} {url[(url.LastIndexOf('/') + 1)..]}";
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(img, 0, 0, img.Width, img.Height);
            return bmp;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _baseMap?.Dispose();
        
    }
}
