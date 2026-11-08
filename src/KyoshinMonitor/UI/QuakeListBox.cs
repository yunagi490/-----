namespace KyoshinMonitor;

/// <summary>JQuake風の地震情報リスト (震度バッジ + 震源 + 発生時刻 + M)</summary>
internal sealed class QuakeListBox : ListBox
{
    private static readonly Font BadgeFont = new("Yu Gothic UI", 20f, FontStyle.Bold);
    private static readonly Font NameFont = new("Yu Gothic UI", 11.5f, FontStyle.Bold);
    private static readonly Font SubFont = new("Yu Gothic UI", 9f);
    private static readonly Font MagFont = new("Yu Gothic UI", 15f, FontStyle.Bold);

    private const TextFormatFlags Flags =
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis |
        TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

    public QuakeListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 58;
        IntegralHeight = false;
        BorderStyle = BorderStyle.None;
        BackColor = Color.FromArgb(24, 26, 32);
        DoubleBuffered = true;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not QuakeInfo q) return;

        var g = e.Graphics;
        var r = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(Color.FromArgb(24, 26, 32))) g.FillRectangle(bg, r);
        var card = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
        using (var cardBrush = new SolidBrush(selected ? Color.FromArgb(58, 64, 80) : Color.FromArgb(38, 41, 49)))
            g.FillRectangle(cardBrush, card);

        // 震度バッジ
        var badge = new Rectangle(card.X, card.Y, 54, card.Height);
        using (var b = new SolidBrush(Shindo.ColorFromScale(q.MaxScale))) g.FillRectangle(b, badge);
        var badgeText = q.MaxScale is >= 55 or (>= 30 and < 40) ? Color.White : Color.Black;
        TextRenderer.DrawText(g, Shindo.Badge(q.MaxScale), BadgeFont, badge, badgeText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // マグニチュード (右寄せ)
        var magRect = new Rectangle(card.Right - 86, card.Y, 82, card.Height);
        var magText = q.Magnitude is { } m ? $"M {m:0.0}" : "M -";
        TextRenderer.DrawText(g, magText, MagFont, magRect, Color.White, Flags | TextFormatFlags.Right);

        // 震源 / 発生時刻・深さ
        var left = badge.Right + 8;
        var width = magRect.X - left - 4;
        var nameRect = new Rectangle(left, card.Y + 3, width, card.Height / 2 - 2);
        var subRect = new Rectangle(left, card.Y + card.Height / 2, width, card.Height / 2 - 3);

        var name = string.IsNullOrEmpty(q.Hypocenter) ? $"({q.IssueLabel})" : q.Hypocenter;
        TextRenderer.DrawText(g, name, NameFont, nameRect, Color.White, Flags);

        var time = q.Time.Length >= 16 ? q.Time[..16] : q.Time;
        var depth = q.Depth is { } d ? (d == 0 ? "  ごく浅い" : $"  深さ{d}km") : "";
        TextRenderer.DrawText(g, time + depth, SubFont, subRect, Color.FromArgb(180, 186, 198), Flags);
    }
}