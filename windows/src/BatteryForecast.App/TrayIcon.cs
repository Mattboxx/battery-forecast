using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace BatteryForecast.App;

/// <summary>
/// Icona nell'area di notifica. Windows non permette un'etichetta di testo lunga come la barra dei menu
/// di macOS: c'è l'icona (batteria disegnata con il livello esatto, o la percentuale se scelta) e il
/// tooltip con durata e obiettivo serale. Se Explorer riparte, NotifyIcon si registra di nuovo da solo.
/// </summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    IntPtr hicon;
    string latestTooltip = "Battery Forecast";
    bool tooltipSuppressed;
    (int level, bool charging, bool percent, bool dark, int size) drawn = (-1, false, false, false, 0);

    public TrayIcon(Action onClick, Action onOpen, Action onHistory, Action onPreferences, Action onQuit, Func<string, string> text,
        Action? onHover = null)
    {
        var menu = new Forms.ContextMenuStrip { RenderMode = Forms.ToolStripRenderMode.System, ShowImageMargin = false };
        menu.Items.Add(text("Open"), null, (_, _) => onOpen());
        menu.Items.Add(text("History"), null, (_, _) => onHistory());
        menu.Items.Add(text("Preferences"), null, (_, _) => onPreferences());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(text("Quit"), null, (_, _) => onQuit());
        icon = new Forms.NotifyIcon { Text = "Battery Forecast", ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) onClick(); };
        // Windows manda un movimento a ogni spostamento sopra l'icona: l'app decide se aprire dopo una breve pausa
        if (onHover is not null) icon.MouseMove += (_, _) => onHover();
        icon.BalloonTipClicked += (_, _) => (balloonClick ?? onOpen)();
        Set(null, false, false, "Battery Forecast");
        icon.Visible = true;
    }

    public void Set(double? level, bool charging, bool showPercent, string tooltip)
    {
        latestTooltip = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        icon.Text = tooltipSuppressed ? "" : latestTooltip;
        var size = Forms.SystemInformation.SmallIconSize.Width;
        var key = ((int)Math.Round(level ?? -1), charging, showPercent, Theme.TaskbarIsDark, size);
        if (key == drawn) return;
        drawn = key;
        using var bmp = Draw(level, charging, showPercent, Theme.TaskbarIsDark, size);
        var next = bmp.GetHicon();
        icon.Icon = Icon.FromHandle(next);
        if (hicon != IntPtr.Zero) DestroyIcon(hicon);
        hicon = next;
    }

    public void SetTooltipSuppressed(bool suppressed)
    {
        tooltipSuppressed = suppressed;
        icon.Text = suppressed ? "" : latestTooltip;
    }

    /// <summary>
    /// Notifica di sistema. Su Windows 10/11 il fumetto dell'icona diventa una notifica del centro notifiche
    /// (rispetta Non disturbare); il clic apre il pannello.
    /// </summary>
    public void Notify(string title, string body, Action? onClick = null)
    {
        balloonClick = onClick;
        icon.ShowBalloonTip(10_000, title, body, Forms.ToolTipIcon.None);
    }

    Action? balloonClick;

    /// <summary>Batteria orizzontale come quella di sistema, riempita al livello esatto; fulmine se in carica.</summary>
    internal static Bitmap Draw(double? level, bool charging, bool showPercent, bool darkTaskbar, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var fg = darkTaskbar ? Color.White : Color.FromArgb(0x1B, 0x18, 0x30);
        var s = size / 16f;

        if (showPercent && level is double pct && !charging)
        {
            var text = pct >= 100 ? "F" : $"{pct:0}";
            using var font = new Font("Segoe UI", text.Length > 1 ? 8.6f * s : 10f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(-1, 0, size + 2, size), fmt);
            return bmp;
        }

        var body = new RectangleF(0.75f * s, 4f * s, 12.5f * s, 8f * s);
        using (var pen = new Pen(Color.FromArgb(200, fg), 1.1f * s))
        using (var path = Rounded(body, 2f * s))
            g.DrawPath(pen, path);
        using (var tip = new SolidBrush(Color.FromArgb(200, fg)))
            g.FillRectangle(tip, 13.6f * s, 6.5f * s, 1.5f * s, 3f * s);
        if (level is double l)
        {
            var inner = RectangleF.Inflate(body, -1.9f * s, -1.9f * s);
            inner.Width = Math.Max(1f * s, inner.Width * (float)Math.Clamp(l, 0, 100) / 100f);
            var fill = l <= 15 && !charging ? Color.FromArgb(0xE8, 0x5A, 0x3C) : fg;
            using var b = new SolidBrush(fill);
            using var p = Rounded(inner, 0.8f * s);
            g.FillPath(b, p);
        }
        if (charging)
        {
            PointF[] bolt = [new(8.2f * s, 2.2f * s), new(4.6f * s, 8.6f * s), new(7.3f * s, 8.6f * s),
                             new(6.2f * s, 13.8f * s), new(10.2f * s, 7f * s), new(7.4f * s, 7f * s)];
            using var outline = new Pen(darkTaskbar ? Color.Black : Color.White, 1.6f * s) { LineJoin = LineJoin.Round };
            g.DrawPolygon(outline, bolt);
            using var b = new SolidBrush(Color.FromArgb(0xFF, 0xC3, 0x5A));
            g.FillPolygon(b, bolt);
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        if (hicon != IntPtr.Zero) DestroyIcon(hicon);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr handle);
}
