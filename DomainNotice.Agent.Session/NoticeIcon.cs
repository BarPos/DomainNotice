using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DomainNotice.Agent.Session;

public enum NoticeIconState
{
    Connected,
    Warning,
    Disconnected,
}

/// <summary>A neutral bell glyph drawn in code (no third-party assets), in a colour per connection state.</summary>
internal static class NoticeIcon
{
    private static readonly Dictionary<NoticeIconState, Icon> Cache = [];

    public static Icon For(NoticeIconState state)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(state, out var icon))
            {
                icon = Draw(state switch
                {
                    NoticeIconState.Connected => Color.FromArgb(0x1F, 0x5F, 0xAD),
                    NoticeIconState.Warning => Color.FromArgb(0xC7, 0x7C, 0x02),
                    _ => Color.FromArgb(0x80, 0x80, 0x80),
                });
                Cache[state] = icon;
            }
            return icon;
        }
    }

    public static BitmapSource WindowIcon() =>
        Imaging.CreateBitmapSourceFromHIcon(For(NoticeIconState.Connected).Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    private static Icon Draw(Color fill)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(fill);
            using var outline = new Pen(Darken(fill), 1.5f);

            // Knob on top.
            g.FillEllipse(brush, 14, 2, 4, 4);

            // Bell body: a dome that flares out at the bottom rim.
            using var body = new GraphicsPath();
            body.AddBezier(5f, 24f, 9f, 20f, 8f, 6f, 16f, 5f);
            body.AddBezier(16f, 5f, 24f, 6f, 23f, 20f, 27f, 24f);
            body.AddLine(27f, 24f, 5f, 24f);
            body.CloseFigure();
            g.FillPath(brush, body);
            g.DrawPath(outline, body);

            // Rim and clapper.
            g.FillRectangle(brush, 4, 23, 24, 3);
            g.FillEllipse(brush, 13, 25, 6, 6);
        }

        // The HICON is kept for the life of the process (three cached icons), so it is never destroyed under
        // a NotifyIcon or window that still uses it.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Color Darken(Color c) => Color.FromArgb(c.A, c.R * 3 / 4, c.G * 3 / 4, c.B * 3 / 4);
}
