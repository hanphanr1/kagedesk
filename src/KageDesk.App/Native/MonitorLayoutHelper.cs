using System.Drawing;

namespace KageDesk.App.Native;

public sealed record MonitorDescriptor(
    string DeviceName,
    bool IsPrimary,
    Rectangle Bounds,
    Rectangle WorkingArea,
    int Dpi = 96
);

/// <summary>
/// Pure, testable multi-monitor resolution, per-monitor DPI lookup, virtual-desktop wallpaper offset,
/// and per-monitor taskbar-edge docking helper.
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
    /// Computes clean taskbar-docked bounds for the Taskbar Pet on any monitor, automatically adapting to
    /// bottom, top, left, or right taskbars while keeping the pet strictly inside that monitor's WorkingArea.
    /// </summary>
    public static Rectangle ComputeTaskbarDockedBounds(
        Rectangle monitorBounds,
        Rectangle workingArea,
        int formWidth,
        int formHeight,
        int? relativeX = null,
        int? legacyAbsoluteX = null)
    {
        bool topTaskbar = workingArea.Top > monitorBounds.Top && workingArea.Bottom == monitorBounds.Bottom;
        int top = topTaskbar
            ? workingArea.Top
            : workingArea.Bottom - formHeight;

        int maxTop = Math.Max(workingArea.Top, workingArea.Bottom - formHeight);
        top = Math.Clamp(top, workingArea.Top, maxTop);

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
            desiredLeft = workingArea.Right - formWidth - 16;
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
