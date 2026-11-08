using System.Net.WebSockets;
using System.Text;

namespace KyoshinMonitor;

/// <summary>自動再接続つきのWebSocket受信ループ (指数バックオフ 1s→60s)</summary>
public static class WsRunner
{
    public static async Task RunAsync(Uri uri, Action<string> onText, Action<bool> onState, CancellationToken ct)
    {
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(uri, ct);
                onState(true);
                backoff = 1;

                var buf = new byte[64 * 1024];
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (r.EndOfMessage)
                    {
                        var text = sb.ToString();
                        sb.Clear();
                        try { onText(text); } catch { /* 1メッセージの失敗で接続は落とさない */ }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 再接続へ */ }

            onState(false);
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 60);
        }
    }
}