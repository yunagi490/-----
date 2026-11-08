using System.Drawing.Drawing2D;
using KyoshinMonitor.Map;
using System.Text.Json;

namespace KyoshinMonitor;

/// <summary>ピクセルを滲ませずに拡大表示するPictureBox</summary>
internal sealed class PixelBox : PictureBox
{
    public PixelBox() { DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs pe)
    {
        pe.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        pe.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        base.OnPaint(pe);
    }
}

public sealed class MainForm : Form
{
/// <summary>true: 強震モニタの画像をそのまま表示 / false: 専用地図 (観測点の描画は未実装)</summary>
    private const bool ShowKmoniImage = false;

    private readonly MapControl _mapView = new() { Dock = DockStyle.Fill };
    private const string EewWsUrl = "wss://ws-api.wolfx.jp/jma_eew";
    private const string P2PWsUrl = "wss://api.p2pquake.net/v2/ws";
    private const string P2PHistoryUrl = "https://api.p2pquake.net/v2/history?codes=551&limit=30";

    /// <summary>この震度以上の地震情報を受信したら通知する (40 = 震度4)</summary>
    private const int NotifyMinScale = 40;

    /// <summary>EEWを表示し続ける秒数</summary>
    private static readonly TimeSpan EewDisplayTime = TimeSpan.FromSeconds(180);

    private readonly CancellationTokenSource _cts = new();
    private readonly KmoniClient _kmoni = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly PixelBox _map = new()
    {
        Dock = DockStyle.Fill,
        Width = 560,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(20, 24, 32),
    };

    private readonly Label _eewLabel = new()
    {
        AutoSize = false,
        Location = new Point(8, 8),
        Size = new Size(380, 84),
        Font = new Font("Yu Gothic UI", 12f, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = Color.FromArgb(210, 40, 44, 52),
        Padding = new Padding(8),
        Text = "緊急地震速報は発表されていません",        
    };

    private readonly QuakeListBox _list = new()
    {
        Dock = DockStyle.Right,
        Width = 340,
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 0, 0),
        BackColor = Color.FromArgb(18, 20, 26),
        ForeColor = Color.FromArgb(180, 186, 198),
    };

    private readonly NotifyIcon _tray = new()
    {
        Icon = SystemIcons.Warning,
        Text = "強震モニタ",

        Visible = true,
    };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };

    private readonly HashSet<string> _seenEew = new();
    private readonly HashSet<string> _seenQuake = new();
    private readonly HashSet<string> _alertedEvents = new();

    private EewInfo? _currentEew;
    private volatile bool _eewActive;
    private DateTime _kmoniTime;
    private bool _eewConnected;
    private bool _p2pConnected;
    private string _kmoniMessage = "待機中";
    private readonly DateTime? _replayStart;
    private readonly System.Diagnostics.Stopwatch _replayClock = System.Diagnostics.Stopwatch.StartNew();

    public MainForm(DateTime? replayStart = null)
    {
        Text = "強震モニタ";
        _replayStart = replayStart;
        KeyPreview = true;
        Text = replayStart is null ? "強震モニタ" : "強震モニタ [リプレイ]";
        ClientSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = Color.FromArgb(24, 26, 32);
        _map.Visible = ShowKmoniImage;
        Control mapHost = ShowKmoniImage ? _map : _mapView;
        mapHost.Controls.Add(_eewLabel); // EEWバナーは地図の左上に重ねる

        Controls.Add(_map);    // Fill (先に追加)
        Controls.Add(_mapView); // 
        Controls.Add(_list);   // Right
        Controls.Add(_status); // Bottom

        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadMap();
        _ = Task.Run(() => KmoniLoopAsync(_cts.Token));
        _ = Task.Run(() => WsRunner.RunAsync(new Uri(EewWsUrl), OnEewMessage, s => { _eewConnected = s; }, _cts.Token));
        _ = Task.Run(() => LoadQuakeHistoryAsync(_cts.Token));
        _ = Task.Run(() => WsRunner.RunAsync(new Uri(P2PWsUrl), OnQuakeMessage, s => { _p2pConnected = s; }, _cts.Token));
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F9)
        {
            SimulateEew();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>F9: 疑似EEW (静岡県西部・訓練扱い) を流して、パネル・通知・地図の表示を確認する</summary>
    private void SimulateEew()
    {
        var jst = TimeSpan.FromHours(9);
        var now = DateTimeOffset.UtcNow.ToOffset(jst);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "jma_eew",
            ["EventID"] = "TEST" + now.ToString("HHmmss"),
            ["Serial"] = 1,
            ["AnnouncedTime"] = now.ToString("yyyy/MM/dd HH:mm:ss"),
            ["OriginTime"] = now.AddSeconds(-5).ToString("yyyy/MM/dd HH:mm:ss"),
            ["Hypocenter"] = "テスト(静岡県西部)",
            ["Latitude"] = 34.8,
            ["Longitude"] = 137.9,
            ["Magunitude"] = 5.7,
            ["Depth"] = 40,
            ["MaxIntensity"] = "5強",
            ["isWarn"] = true,
            ["isTraining"] = true,
        });
        OnEewMessage(json);
    }

    private void LoadMap()
    {
    
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "prefectures.geojson");
        try
        {
            _mapView.SetMap(GeoJsonMap.Load(path));
        }
        catch (Exception ex)
        {
            _mapView.SetMessage($"地図データを読み込めません: {ex.Message}");
        }
    }


    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        _uiTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- 強震モニタ

    private async Task KmoniLoopAsync(CancellationToken ct)
    {
        DateTime last = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var latest = _replayStart is { } rs
                    ? rs + _replayClock.Elapsed
                    : await _kmoni.GetLatestTimeAsync(ct);
                latest = new DateTime(latest.Ticks - latest.Ticks % TimeSpan.TicksPerSecond); // 秒未満を切り捨て
                if (latest != last)
                {
                    var frame = await _kmoni.RenderAsync(latest, _eewActive || _replayStart is not null, ct);
                    if (frame is not null)
                    {
                        last = latest;
                        _kmoniTime = latest;
                        _kmoniMessage = "受信中";
                        UiPost(() =>
                        {
                            var old = _map.Image;
                            _map.Image = frame;
                            old?.Dispose();
                        });
                    }
                    else
                    {
                        frame?.Dispose();
                        _kmoniMessage = "画像取得失敗: " + _kmoni.LastError;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (TaskCanceledException) { _kmoniMessage = "タイムアウト"; }
            catch (Exception ex) { _kmoniMessage = "エラー: " + ex.Message; }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---------------------------------------------------------------- EEW

    private void OnEewMessage(string text)
    {
        if (!text.StartsWith('{')) return; // "pong" 等
        var eew = EewInfo.Parse(text);
        if (eew is null) return; // heartbeat など

        // 接続直後に過去の最終報が流れてくることがあるので、古いものは無視
        if (eew.AnnouncedAt != DateTime.MinValue && DateTime.Now - eew.AnnouncedAt > EewDisplayTime) return;
        if (!_seenEew.Add(eew.Key)) return;

        _currentEew = eew;
        _eewActive = !eew.IsCancel;

        if (eew.IsCancel) return;

        // 同一地震では「初報」と「警報に格上げ」のときに鳴らす
        var alertKey = $"{eew.EventId}:{(eew.IsWarn ? "warn" : "fcst")}";
        if (_alertedEvents.Add(alertKey))
        {
            UiPost(() => Alert(eew));
        }
    }

    private void Alert(EewInfo eew)
    {
        var kind = eew.IsWarn ? "警報" : "予報";
        var title = $"緊急地震速報({kind}) 第{eew.Serial}報" + (eew.IsTraining ? " [訓練]" : "");
        var body = $"{eew.Hypocenter}  M{eew.Magnitude:0.0}  深さ{eew.Depth}km  最大震度{eew.MaxIntensity}";
        _tray.ShowBalloonTip(10000, title, body, eew.IsWarn ? ToolTipIcon.Error : ToolTipIcon.Warning);

        // 最前面に出す
        WindowState = FormWindowState.Normal;
        Activate();

        if (eew.IsWarn)
        {
            _ = Task.Run(() =>
            {
                for (var i = 0; i < 3; i++)
                {
                    try { Console.Beep(1000, 300); Thread.Sleep(100); Console.Beep(800, 300); Thread.Sleep(200); }
                    catch { System.Media.SystemSounds.Hand.Play(); }
                }
            });
        }
        else
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    // ---------------------------------------------------------------- 地震情報 (P2P地震情報)

    private async Task LoadQuakeHistoryAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(P2PHistoryUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var list = new List<QuakeInfo>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var q = QuakeInfo.Parse(el);
                if (q is not null) list.Add(q);
            }
            // APIは新しい順。古い方から Insert(0) して新しい順に並べる
            list.Reverse();
            UiPost(() =>
            {
                foreach (var q in list) AddQuake(q, notify: false);
            });
        }
        catch (OperationCanceledException) { }
        catch { /* 履歴が取れなくてもWebSocketで続行 */ }
    }

    private void OnQuakeMessage(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var q = QuakeInfo.Parse(doc.RootElement);
        if (q is null) return;
        UiPost(() => AddQuake(q, notify: true));
    }

    private void AddQuake(QuakeInfo q, bool notify)
    {
        if (!_seenQuake.Add(q.Id)) return;
        // 同じ分に出た情報 (震度速報→震源情報→各地の震度) は1件にまとめる。
        // 震源名がどちらにもあって違う場合は、別の地震として扱う。
        var minute = MinuteOf(q.Time);
        for (var i = 0; i < _list.Items.Count; i++)
        {
            for (var j = 0; j < _list.Items.Count; j++)
            {
                if (_list.Items[j] is not QuakeInfo old || MinuteOf(old.Time) != minute) continue;
                if (old.Hypocenter.Length > 0 && q.Hypocenter.Length > 0 && old.Hypocenter != q.Hypocenter) continue;

                var merged = old.MergeWith(q);
                _list.Items[j] = merged;
                if (notify && merged.MaxScale >= NotifyMinScale && old.MaxScale < NotifyMinScale) NotifyQuake(merged);
                return;
            }
        }

        _list.Items.Insert(0, q);
        while (_list.Items.Count > 100) _list.Items.RemoveAt(_list.Items.Count - 1);
        if (notify && q.MaxScale >= NotifyMinScale) NotifyQuake(q);
    }

    private static string MinuteOf(string time) => time.Length >= 16 ? time[..16] : time;
    private void NotifyQuake(QuakeInfo q)
    {
        _tray.ShowBalloonTip(8000, $"{q.IssueLabel} 最大震度{q.ShindoText}",
            string.IsNullOrEmpty(q.Hypocenter) ? q.Time : $"{q.Hypocenter}  M{q.Magnitude:0.0}", ToolTipIcon.Info);
        System.Media.SystemSounds.Asterisk.Play();
    }

    // ---------------------------------------------------------------- UI更新

    private void RefreshUi()
    {
        var eew = _currentEew;
        if (eew is not null && !eew.IsCancel && DateTime.Now - eew.ReceivedAt < EewDisplayTime)
        {
            _eewActive = true;
            var kind = eew.IsWarn ? "警報" : "予報";
            _eewLabel.BackColor = eew.IsWarn ? Color.FromArgb(200, 20, 20) : Color.FromArgb(220, 130, 0);
            _eewLabel.Text =
                $"緊急地震速報({kind}) 第{eew.Serial}報{(eew.IsFinal ? " 最終" : "")}{(eew.IsTraining ? " [訓練]" : "")}\n" +
                $"震源: {eew.Hypocenter}  M{eew.Magnitude:0.0}  深さ{eew.Depth}km\n" +
                $"最大震度: {eew.MaxIntensity}";
            _mapView.SetEew(eew.HasLocation
                ? new EewMarker(eew.Latitude, eew.Longitude, eew.Depth, eew.OriginAt, eew.IsWarn)
                : null);            
        }
        else
        {
            _eewActive = false;
            _mapView.SetEew(null);
            _eewLabel.BackColor = Color.FromArgb(210, 40, 44, 52);
            _eewLabel.Text = eew is { IsCancel: true } ? "緊急地震速報は取り消されました" : "緊急地震速報は発表されていません";
        }

        var kt = _kmoniTime == DateTime.MinValue ? "--:--:--" : _kmoniTime.ToString("HH:mm:ss");
        _status.Text =
            (_replayStart is null ? "" : "[リプレイ] ") + $"強震モニタ {kt} ({_kmoniMessage})  |  EEW: {(_eewConnected ? "接続中" : "未接続")}  |  地震情報: {(_p2pConnected ? "接続中" : "未接続")}";
    }

    private void UiPost(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (ObjectDisposedException) { }
    }
}
