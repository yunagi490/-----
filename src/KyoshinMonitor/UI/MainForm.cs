using System.Drawing.Drawing2D;
using System.Net.Http;
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
        Dock = DockStyle.Left,
        Width = 560,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(20, 24, 32),
    };

    private readonly Label _eewLabel = new()
    {
        Dock = DockStyle.Top,
        Height = 130,
        Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = Color.FromArgb(40, 44, 52),
        Padding = new Padding(10),
        Text = "緊急地震速報: なし",
    };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        Font = new Font("Yu Gothic UI", 10f),
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 0, 0),
    };

    private readonly NotifyIcon _tray = new()
    {
        Icon = SystemIcons.Warning,
        Text = "強震モニタ",
        Visible = true,
    };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    private readonly HashSet<string> _seenEew = new();
    private readonly HashSet<string> _seenQuake = new();
    private readonly HashSet<string> _alertedEvents = new();

    private EewInfo? _currentEew;
    private volatile bool _eewActive;
    private DateTime _kmoniTime;
    private bool _eewConnected;
    private bool _p2pConnected;
    private string _kmoniMessage = "待機中";

    public MainForm()
    {
        Text = "強震モニタ";
        ClientSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;

        _list.Columns.Add("発生時刻", 150);
        _list.Columns.Add("震源", 140);
        _list.Columns.Add("M", 45);
        _list.Columns.Add("深さ", 60);
        _list.Columns.Add("最大震度", 70);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_list);
        right.Controls.Add(_eewLabel);

        Controls.Add(right);
        Controls.Add(_map);
        Controls.Add(_status);

        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _ = Task.Run(() => KmoniLoopAsync(_cts.Token));
        _ = Task.Run(() => WsRunner.RunAsync(new Uri(EewWsUrl), OnEewMessage, s => { _eewConnected = s; }, _cts.Token));
        _ = Task.Run(() => LoadQuakeHistoryAsync(_cts.Token));
        _ = Task.Run(() => WsRunner.RunAsync(new Uri(P2PWsUrl), OnQuakeMessage, s => { _p2pConnected = s; }, _cts.Token));
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
                var latest = await _kmoni.GetLatestTimeAsync(ct);
                if (latest != last)
                {
                    var frame = await _kmoni.RenderAsync(latest, _eewActive, ct);
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
                        _kmoniMessage = "画像取得失敗";
                    }
                }
            }
            catch (OperationCanceledException) { return; }
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

        var item = new ListViewItem(new[]
        {
            q.Time.Length >= 16 ? q.Time[..16] : q.Time,
            string.IsNullOrEmpty(q.Hypocenter) ? $"({q.IssueLabel})" : q.Hypocenter,
            q.Magnitude is { } m ? m.ToString("0.0") : "-",
            q.Depth is { } d ? (d == 0 ? "ごく浅い" : $"{d}km") : "-",
            q.ShindoText,
        })
        {
            BackColor = Shindo.ColorFromScale(q.MaxScale),
            ForeColor = q.MaxScale is >= 55 or (>= 30 and < 40) ? Color.White : Color.Black,
        };
        _list.Items.Insert(0, item);
        while (_list.Items.Count > 100) _list.Items.RemoveAt(_list.Items.Count - 1);

        if (notify && q.MaxScale >= NotifyMinScale)
        {
            _tray.ShowBalloonTip(8000, $"{q.IssueLabel} 最大震度{q.ShindoText}",
                string.IsNullOrEmpty(q.Hypocenter) ? q.Time : $"{q.Hypocenter}  M{q.Magnitude:0.0}", ToolTipIcon.Info);
            System.Media.SystemSounds.Asterisk.Play();
        }
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
        }
        else
        {
            _eewActive = false;
            _eewLabel.BackColor = Color.FromArgb(40, 44, 52);
            _eewLabel.Text = eew is { IsCancel: true } ? "緊急地震速報: 取り消されました" : "緊急地震速報: なし";
        }

        var kt = _kmoniTime == DateTime.MinValue ? "--:--:--" : _kmoniTime.ToString("HH:mm:ss");
        _status.Text =
            $"強震モニタ {kt} ({_kmoniMessage})  |  EEW: {(_eewConnected ? "接続中" : "未接続")}  |  地震情報: {(_p2pConnected ? "接続中" : "未接続")}";
    }

    private void UiPost(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (ObjectDisposedException) { }
    }
}