namespace BatteryForecast.Core.Presentation;

/// <summary>Physical pixels throughout; negative monitor origins are valid.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);
public readonly record struct FlyoutBounds(int X, int Y, int Width, int Height);

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public static class FlyoutPlacement
{
    /// <summary>
    /// Con la barra a scomparsa automatica l'area di lavoro è tutto lo schermo: senza sapere dove sta la barra
    /// il pannello finirebbe sotto di essa e coprirebbe l'icona quando riappare. Se si conosce il rettangolo
    /// della barra (ABM_GETTASKBARPOS) sullo stesso monitor, l'area utile si restringe di conseguenza.
    /// </summary>
    public static ScreenRect AvoidTaskbar(ScreenRect work, ScreenRect full, ScreenRect? taskbar, TaskbarEdge edge, int minThickness)
    {
        if (taskbar is not { } t || t.Right <= full.Left || t.Left >= full.Right || t.Bottom <= full.Top || t.Top >= full.Bottom)
            return work;
        // nascosta, Windows può riportare solo la striscia di pochi pixel: si usa lo spessore tipico
        return edge switch
        {
            TaskbarEdge.Bottom => work with { Bottom = Math.Min(work.Bottom, Math.Min(t.Top, full.Bottom - minThickness)) },
            TaskbarEdge.Top => work with { Top = Math.Max(work.Top, Math.Max(t.Bottom, full.Top + minThickness)) },
            TaskbarEdge.Left => work with { Left = Math.Max(work.Left, Math.Max(t.Right, full.Left + minThickness)) },
            _ => work with { Right = Math.Min(work.Right, Math.Min(t.Left, full.Right - minThickness)) },
        };
    }

    public static FlyoutBounds Calculate(ScreenRect work, ScreenRect full,
        int anchorX, int anchorY, int width, int height, int gap)
    {
        gap = Math.Max(0, Math.Min(gap, Math.Min(work.Right - work.Left, work.Bottom - work.Top) / 4));
        width = Math.Clamp(width, 1, Math.Max(1, work.Right - work.Left - 2 * gap));
        height = Math.Clamp(height, 1, Math.Max(1, work.Bottom - work.Top - 2 * gap));
        int x, y;
        if (work.Bottom < full.Bottom || work == full)
        { x = anchorX - width / 2; y = work.Bottom - height - gap; }
        else if (work.Top > full.Top)
        { x = anchorX - width / 2; y = work.Top + gap; }
        else
        { x = work.Left > full.Left ? work.Left + gap : work.Right - width - gap; y = anchorY - height / 2; }
        return new(Math.Clamp(x, work.Left + gap, work.Right - width - gap),
            Math.Clamp(y, work.Top + gap, work.Bottom - height - gap), width, height);
    }
}
