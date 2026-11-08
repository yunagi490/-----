using System.Globalization;
namespace KyoshinMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(ParseReplay(args)));
            }

    /// <summary>--replay "2026/11/08 01:04:00" (JST) の形式で指定された開始時刻を読む</summary>
    private static DateTime? ParseReplay(string[] args)
    {
        var i = Array.IndexOf(args, "--replay");
        if (i < 0 || i + 1 >= args.Length) return null;
        return DateTime.TryParseExact(args[i + 1], "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }
}

