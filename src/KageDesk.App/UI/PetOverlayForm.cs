using System.Diagnostics;
using System.Drawing;
using KageDesk.App.Native;
using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.App.UI;

public class PetOverlayForm : Form
{
    private readonly TaskbarPetRenderer _renderer;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Stopwatch _stopwatch;

    private PixelBuffer _petBuffer;
    private PixelBuffer _scaledBuffer;

    private HqState _currentState;
    private ThemePack _currentTheme;
    private int _scale = 2;
    private string _idleMode = "chill"; // "chill" | "auto-hide" | "off"
    private int? _petX;
    private bool _isPaused;
    private bool _disposed;

    private bool _isDragging;
    private int _dragStartX;
    private string? _dismissedAgentId;
    private AgentState? _dismissedAgentState;

    public int PetScale => _scale;
    public int? PetX => _petX;
    public string IdleMode => _idleMode;

    public event Action<int>? PetPositionChanged;
    public event Action<AgentSnapshot>? AgentClicked;
    public ContextMenuStrip? StudioContextMenu { get; set; }

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
            else if (!_disposed && _idleMode != "off")
            {
                _renderTimer.Start();
            }
        }
    }

    public PetOverlayForm(ThemePack initialTheme, int initialScale = 2, int? initialPetX = null, string idleMode = "chill", int targetFps = 15)
    {
        _currentTheme = initialTheme ?? throw new ArgumentNullException(nameof(initialTheme));
        _scale = Math.Clamp(initialScale, 1, 2);
        _petX = initialPetX;
        _idleMode = idleMode;

        _currentState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

        _petBuffer = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        _scaledBuffer = new PixelBuffer(TaskbarPetRenderer.CanvasWidth * _scale, TaskbarPetRenderer.CanvasHeight * _scale);
        _renderer = new TaskbarPetRenderer();
        _stopwatch = Stopwatch.StartNew();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false; // Layered windows handle own painting via UpdateLayeredWindow

        int fps = Math.Clamp(targetFps, 5, 30);
        _renderTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(16, 1000 / fps)
        };
        _renderTimer.Tick += OnRenderTick;

        UpdateWindowBounds();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32Interop.WS_EX_LAYERED
                       | Win32Interop.WS_EX_TOPMOST
                       | Win32Interop.WS_EX_TOOLWINDOW
                       | Win32Interop.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Start()
    {
        UpdateWindowBounds();
        Show();
        if (_idleMode != "off" && !_isPaused)
        {
            _renderTimer.Start();
        }
    }

    public void SetScale(int scale)
    {
        int newScale = Math.Clamp(scale, 1, 2);
        if (_scale == newScale) return;

        _scale = newScale;
        _scaledBuffer.Dispose();
        _scaledBuffer = new PixelBuffer(TaskbarPetRenderer.CanvasWidth * _scale, TaskbarPetRenderer.CanvasHeight * _scale);

        UpdateWindowBounds();
        RenderAndPresent();
    }

    public void SetIdleMode(string idleMode)
    {
        _idleMode = idleMode;
        if (_idleMode == "off")
        {
            _renderTimer.Stop();
            Hide();
        }
        else
        {
            if (!_isPaused)
            {
                _renderTimer.Start();
                Show();
            }
        }
    }

    public void SetPetX(int? x)
    {
        _petX = x;
        UpdateWindowBounds();
    }

    public void UpdateData(HqState state, ThemePack theme)
    {
        _currentState = state ?? _currentState;
        _currentTheme = theme ?? _currentTheme;
    }

    private void UpdateWindowBounds()
    {
        int w = TaskbarPetRenderer.CanvasWidth * _scale;
        int h = TaskbarPetRenderer.CanvasHeight * _scale;
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);

        int top = workArea.Bottom - h;
        int maxLeft = Math.Max(workArea.Left, workArea.Right - w);
        int left = _petX ?? (workArea.Right - w - 16);
        left = Math.Clamp(left, workArea.Left, maxLeft);

        SetBounds(left, top, w, h);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_disposed || _isPaused) return;

        if (_idleMode == "off")
        {
            if (Visible) Hide();
            return;
        }

        // Check if all agents are idle
        bool hasActiveAgent = false;
        var agents = _currentState.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].State != AgentState.Idle)
            {
                hasActiveAgent = true;
                break;
            }
        }

        if (_idleMode == "auto-hide" && !hasActiveAgent)
        {
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();
        RenderAndPresent();
    }

    public void RenderAndPresent()
    {
        if (_disposed) return;

        // Reset dismissed agent if state changed
        if (_dismissedAgentId != null)
        {
            bool stillDismissed = false;
            foreach (var ag in _currentState.Agents)
            {
                if (ag.Id == _dismissedAgentId && ag.State == _dismissedAgentState)
                {
                    stillDismissed = true;
                    break;
                }
            }
            if (!stillDismissed)
            {
                _dismissedAgentId = null;
                _dismissedAgentState = null;
            }
        }

        HqState stateToRender = _currentState;
        if (_dismissedAgentId != null)
        {
            var filtered = new List<AgentSnapshot>(_currentState.Agents.Count);
            foreach (var ag in _currentState.Agents)
            {
                if (ag.Id == _dismissedAgentId)
                {
                    // Downgrade to Idle for rendering only
                    filtered.Add(ag with { State = AgentState.Idle, QuestionPrompt = null });
                }
                else
                {
                    filtered.Add(ag);
                }
            }
            stateToRender = _currentState with { Agents = filtered };
        }

        float timeSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
        bool showIdle = _idleMode != "off";

        _renderer.Render(_petBuffer, stateToRender, _currentTheme, timeSeconds, showIdle);

        if (_scale == 1)
        {
            PresentLayeredBitmap(_petBuffer.Bitmap);
        }
        else
        {
            _petBuffer.BlitNearestNeighborTo(_scaledBuffer, _scale);
            PresentLayeredBitmap(_scaledBuffer.Bitmap);
        }
    }

    public void PresentLayeredBitmap(Bitmap bmp)
    {
        if (_disposed || !IsHandleCreated) return;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = Win32Interop.GetDC(IntPtr.Zero);
            memDc = Win32Interop.CreateCompatibleDC(screenDc);
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = Win32Interop.SelectObject(memDc, hBitmap);

            var blend = new Win32Interop.BLENDFUNCTION
            {
                BlendOp = Win32Interop.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32Interop.AC_SRC_ALPHA
            };

            var ptSrc = new Win32Interop.POINT(0, 0);
            var ptDst = new Win32Interop.POINT(Left, Top);
            var szDst = new Win32Interop.SIZE(bmp.Width, bmp.Height);

            Win32Interop.UpdateLayeredWindow(
                Handle,
                screenDc,
                ref ptDst,
                ref szDst,
                memDc,
                ref ptSrc,
                0,
                ref blend,
                Win32Interop.ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) Win32Interop.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) Win32Interop.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) Win32Interop.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            StudioContextMenu?.Show(Cursor.Position);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            int unscaledX = e.X / _scale;
            int unscaledY = e.Y / _scale;

            var hit = FindHitRegion(unscaledX, unscaledY);
            if (hit != null)
            {
                if (hit.ActionId == "open_orca")
                {
                    WindowFocuser.TryFocusOrca();
                    return;
                }
                if (hit.ActionId == "dismiss")
                {
                    DismissActiveApprovalOrFail();
                    return;
                }
                if (hit.ActionId.StartsWith("agent:"))
                {
                    string agentId = hit.ActionId["agent:".Length..];
                    var agent = _currentState.Agents.FirstOrDefault(a => a.Id == agentId);
                    if (agent != null)
                    {
                        AgentClicked?.Invoke(agent);
                        return;
                    }
                }
            }

            // Start horizontal dragging
            _isDragging = true;
            _dragStartX = e.X;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isDragging)
        {
            int deltaX = e.X - _dragStartX;
            int newLeft = Left + deltaX;

            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
            int maxLeft = Math.Max(workArea.Left, workArea.Right - Width);
            newLeft = Math.Clamp(newLeft, workArea.Left, maxLeft);

            if (newLeft != Left)
            {
                Left = newLeft;
                // Move layered window
                PresentLayeredBitmap(_scale == 1 ? _petBuffer.Bitmap : _scaledBuffer.Bitmap);
            }
        }
        else
        {
            int unscaledX = e.X / _scale;
            int unscaledY = e.Y / _scale;
            var hit = FindHitRegion(unscaledX, unscaledY);
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_isDragging)
        {
            _isDragging = false;
            _petX = Left;
            PetPositionChanged?.Invoke(Left);
        }
    }

    private PetHitRegion? FindHitRegion(int unscaledX, int unscaledY)
    {
        var regions = _renderer.HitRegions;
        for (int i = regions.Count - 1; i >= 0; i--)
        {
            if (regions[i].Contains(unscaledX, unscaledY))
            {
                return regions[i];
            }
        }
        return null;
    }

    private void DismissActiveApprovalOrFail()
    {
        foreach (var ag in _currentState.Agents)
        {
            if (ag.State is AgentState.NeedsApproval or AgentState.Failed)
            {
                _dismissedAgentId = ag.Id;
                _dismissedAgentState = ag.State;
                RenderAndPresent();
                break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _renderTimer.Stop();
            _renderTimer.Dispose();
            _petBuffer.Dispose();
            _scaledBuffer.Dispose();
        }

        base.Dispose(disposing);
    }
}
