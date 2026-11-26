using System.Drawing;

namespace KageDesk.App.Native;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

public sealed record MonitorDescriptor(
    string DeviceName,
    bool IsPrimary,
    Rectangle Bounds,
    Rectangle WorkingArea,
    int Dpi = 96
);

/// <summary>
/// Pure, testable multi-monitor resolution, per-monitor DPI lookup, virtual-desktop wallpaper offset,
/// and per-monitor 4-edge taskbar docking helper.
/// </summary>
public static class MonitorLayoutHelper
{
    public static IReadOnlyList<MonitorDescriptor> GetConnectedMonitors()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            return new[]
            {
                new MonitorDescriptor(
                    @"\\.\DISPLAY1",
                    true,
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(0, 0, 1920, 1040),
                    96)
            };
        }

        var list = new List<MonitorDescriptor>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            int dpi = QueryMonitorDpi(s.Bounds);
            list.Add(new MonitorDescriptor(s.DeviceName, s.Primary, s.Bounds, s.WorkingArea, dpi));
        }

        return list;
    }

    public static MonitorDescriptor ResolveMonitor(IReadOnlyList<MonitorDescriptor> monitors, string? deviceName)
    {
        if (monitors == null || monitors.Count == 0)
        {
            return new MonitorDescriptor(
                @"\\.\DISPLAY1",
                true,
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(0, 0, 1920, 1040),
                96);
        }

        if (!string.IsNullOrWhiteSpace(deviceName) &&
            !string.Equals(deviceName.Trim(), "primary", StringComparison.OrdinalIgnoreCase))
        {
            string target = deviceName.Trim();
            for (int i = 0; i < monitors.Count; i++)
            {
                if (string.Equals(monitors[i].DeviceName, target, StringComparison.OrdinalIgnoreCase))
                {
                    return monitors[i];
                }
            }
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
            {
                return monitors[i];
            }
        }

        return monitors[0];
    }

    public static MonitorDescriptor ResolveMonitorFromSystem(string? deviceName)
    {
        return ResolveMonitor(GetConnectedMonitors(), deviceName);
    }

    public static MonitorDescriptor ResolveMonitorAtPoint(IReadOnlyList<MonitorDescriptor> monitors, Point screenPoint)
    {
        if (monitors != null && monitors.Count > 0)
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].Bounds.Contains(screenPoint))
                {
                    return monitors[i];
                }
            }

            // Nearest monitor fallback if cursor is slightly outside bounds
            MonitorDescriptor nearest = monitors[0];
            long bestDistSq = long.MaxValue;
            for (int i = 0; i < monitors.Count; i++)
            {
                var b = monitors[i].Bounds;
                int cx = Math.Clamp(screenPoint.X, b.Left, b.Right);
                int cy = Math.Clamp(screenPoint.Y, b.Top, b.Bottom);
                long dx = screenPoint.X - cx;
                long dy = screenPoint.Y - cy;
                long distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = monitors[i];
                }
            }
            return nearest;
        }

        return ResolveMonitor(Array.Empty<MonitorDescriptor>(), null);
    }

    /// <summary>
    /// Computes exact monitor bounds relative to the virtual desktop origin (WorkerW / Progman client space),
    /// handling secondary monitors placed to the left (negative X) or above (negative Y) cleanly.
    /// </summary>
    public static Rectangle ComputeWallpaperVirtualOffset(Rectangle targetScreenBounds, Rectangle virtualScreenBounds)
    {
        return new Rectangle(
            targetScreenBounds.X - virtualScreenBounds.X,
            targetScreenBounds.Y - virtualScreenBounds.Y,
            targetScreenBounds.Width,
            targetScreenBounds.Height);
    }

    /// <summary>
    /// Derives the active taskbar edge from the delta between monitor Bounds and WorkingArea.
    /// </summary>
    public static TaskbarEdge DetectTaskbarEdge(Rectangle monitorBounds, Rectangle workingArea)
    {
        int insetTop = Math.Max(0, workingArea.Top - monitorBounds.Top);
        int insetBottom = Math.Max(0, monitorBounds.Bottom - workingArea.Bottom);
        int insetLeft = Math.Max(0, workingArea.Left - monitorBounds.Left);
        int insetRight = Math.Max(0, monitorBounds.Right - workingArea.Right);

        if (insetTop > 0 && insetTop >= insetBottom && insetTop >= insetLeft && insetTop >= insetRight)
            return TaskbarEdge.Top;

        if (insetLeft > 0 && insetLeft >= insetBottom && insetLeft >= insetRight && insetLeft >= insetTop)
            return TaskbarEdge.Left;

        if (insetRight > 0 && insetRight >= insetBottom && insetRight >= insetLeft && insetRight >= insetTop)
            return TaskbarEdge.Right;

        return TaskbarEdge.Bottom;
    }

    /// <summary>
    /// Computes physical pixel dimensions of the Taskbar Pet for a given user scale (1x/2x) and window DPI.
    /// </summary>
    public static Size ComputeDpiScaledPetSize(int baseWidth, int baseHeight, int petScale, int dpi)
    {
        int clampedScale = petScale >= 2 ? 2 : 1;
        int clampedDpi = dpi is >= 72 and <= 480 ? dpi : 96;
        int w = (int)Math.Round(baseWidth * clampedScale * (clampedDpi / 96.0));
        int h = (int)Math.Round(baseHeight * clampedScale * (clampedDpi / 96.0));
        return new Size(Math.Max(baseWidth, w), Math.Max(baseHeight, h));
    }

    /// <summary>
    /// Computes clean taskbar-docked bounds for the Taskbar Pet on any monitor, automatically docking to
    /// all 4 taskbar edges (Bottom, Top, Left, Right) while keeping the pet strictly inside that monitor's WorkingArea
    /// and supporting negative X/Y secondary monitor coordinates.
    /// </summary>
    public static Rectangle ComputeTaskbarDockedBounds(
        Rectangle monitorBounds,
        Rectangle workingArea,
        int formWidth,
        int formHeight,
        int? relativeX = null,
        int? legacyAbsoluteX = null)
    {
        TaskbarEdge edge = DetectTaskbarEdge(monitorBounds, workingArea);

        int desiredTop = edge == TaskbarEdge.Top
            ? workingArea.Top
            : workingArea.Bottom - formHeight;

        int maxTop = Math.Max(workingArea.Top, workingArea.Bottom - formHeight);
        int top = Math.Clamp(desiredTop, workingArea.Top, maxTop);

        int desiredLeft;
        if (relativeX.HasValue)
        {
            desiredLeft = workingArea.Left + relativeX.Value;
        }
        else if (legacyAbsoluteX.HasValue)
        {
            desiredLeft = legacyAbsoluteX.Value;
        }
        else
        {
            desiredLeft = edge switch
            {
                TaskbarEdge.Left => workingArea.Left,
                TaskbarEdge.Right => workingArea.Right - formWidth,
                _ => workingArea.Right - formWidth - 16
            };
        }

        int maxLeft = Math.Max(workingArea.Left, workingArea.Right - formWidth);
        int left = Math.Clamp(desiredLeft, workingArea.Left, maxLeft);

        return new Rectangle(left, top, formWidth, formHeight);
    }

    public static string FormatMonitorMenuLabel(MonitorDescriptor monitor, int displayIndex)
    {
        string primaryTag = monitor.IsPrimary ? "Primary " : "";
        int dpiPct = (int)Math.Round(monitor.Dpi * 100.0 / 96.0);
        string dpiTag = dpiPct != 100 ? $" @{dpiPct}%" : "";
        return $"Display {displayIndex + 1} ({primaryTag}{monitor.Bounds.Width}x{monitor.Bounds.Height}{dpiTag})";
    }

    public static int QueryWindowDpi(IntPtr hwnd, int fallbackDpi = 96)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                uint dpi = Win32Interop.GetDpiForWindow(hwnd);
                if (dpi is >= 72 and <= 480)
                {
                    return (int)dpi;
                }
            }
            catch
            {
                // Fallback gracefully on older Windows builds
            }
        }

        return fallbackDpi is >= 72 and <= 480 ? fallbackDpi : 96;
    }

    public static int QueryMonitorDpi(Rectangle monitorBounds)
    {
        try
        {
            var pt = new Win32Interop.POINT(
                monitorBounds.Left + monitorBounds.Width / 2,
                monitorBounds.Top + monitorBounds.Height / 2);
            IntPtr hMonitor = Win32Interop.MonitorFromPoint(pt, Win32Interop.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero &&
                Win32Interop.GetDpiForMonitor(hMonitor, Win32Interop.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                dpiX is >= 72 and <= 480)
            {
                return (int)dpiX;
            }
        }
        catch
        {
            // Fallback gracefully on systems without shcore.dll
        }

        return 96;
    }
}
