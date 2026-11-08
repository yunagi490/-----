public static class Shindo
{
    public static string FromScale(int scale) => scale switch
    {
        10 => "1",
        20 => "2",
        30 => "3",
        40 => "4",
        45 => "5弱",
        50 => "5強",
        55 => "6弱",
        60 => "6強",
        70 => "7",
        _ => "不明",
    };
 
    public static Color ColorFromScale(int scale) => scale switch
    {
        >= 70 => Color.FromArgb(180, 0, 104),
        >= 60 => Color.FromArgb(165, 0, 33),
        >= 55 => Color.FromArgb(255, 40, 0),
        >= 50 => Color.FromArgb(255, 153, 0),
        >= 45 => Color.FromArgb(250, 230, 150),
        >= 40 => Color.FromArgb(255, 255, 100),
        >= 30 => Color.FromArgb(0, 65, 255),
        >= 20 => Color.FromArgb(0, 170, 255),
        >= 10 => Color.FromArgb(242, 242, 255),
        _ => Color.White,
    };
}