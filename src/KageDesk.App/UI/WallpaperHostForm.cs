using System.Diagnostics;
using System.Drawing.Drawing2D;
using KageDesk.App.Native;
using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.App.UI;

public class WallpaperHostForm : Form
{
    private static readonly uint WmTaskbarCreated = Win32Interop.RegisterWindowMessage("TaskbarCreated");

    private readonly PixelBuffer _buffer;
    private readonly HqWallpaperRenderer _renderer;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Stopwatch _stopwatch;

    private HqState _currentState;
    private ThemePack _currentTheme;
    private bool _isAttached;
    private bool _isPaused;
    private bool _disposed;

    public bool IsAttached
    {
        get => _isAttached;
        set
        {
            _isAttached = value;
            if (_isAttached && !_isPaused && !_disposed)
            {
                _renderTimer.Start();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            if (_isPaused)
            {
                _renderTimer.Stop();
            }
            else if ((Visible || _isAttached) && !_disposed)
            {
                _renderTimer.Start();
            }
        }
    }

    public event Action? TaskbarRecreated;

    protected override bool ShowWithoutActivation => true;

    public WallpaperHostForm(ThemePack initialTheme, int targetFps = 15)
    {
        _currentTheme = initialTheme ?? throw new ArgumentNullException(nameof(initialTheme));
        _currentState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

        _buffer = new PixelBuffer(HqWallpaperRenderer.CanvasWidth, HqWallpaperRenderer.CanvasHeight);
        _renderer = new HqWallpaperRenderer();
        _stopwatch = Stopwatch.StartNew();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque,
            true);

        int fps = Math.Clamp(targetFps, 5, 30);
        _renderTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(16, 1000 / fps)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Win32Interop.WS_CHILD;
            return cp;
        }
    }

    public void UpdateData(HqState state, ThemePack theme)
    {
        _currentState = state ?? _currentState;
        _currentTheme = theme ?? _currentTheme;
    }

    public void SetTargetFps(int targetFps)
    {
        int fps = Math.Clamp(targetFps, 5, 30);
        _renderTimer.Interval = Math.Max(16, 1000 / fps);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_isPaused || _disposed || (!Visible && !_isAttached)) return;

        float timeSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
        _renderer.Render(_buffer, _currentState, _currentTheme, timeSeconds);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_disposed) return;

        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(
            _buffer.Bitmap,
            ClientRectangle,
            0,
            0,
            HqWallpaperRenderer.CanvasWidth,
            HqWallpaperRenderer.CanvasHeight,
            GraphicsUnit.Pixel);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WmTaskbarCreated && WmTaskbarCreated != 0)
        {
            TaskbarRecreated?.Invoke();
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _renderTimer.Stop();
            _renderTimer.Dispose();
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }
}
