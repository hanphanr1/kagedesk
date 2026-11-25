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

    private readonly PixelBuffer _petBuffer;
    private readonly LayeredDibSurface _dibSurface;

    private HqState _currentState;
    private HqState _cachedRenderState;
    private ThemePack _currentTheme;
    private int _scale = 2;
    private string _idleMode = "chill"; // "chill" | "auto-hide" | "off"
    private int? _petX;
    private bool _isPaused;
    private bool _isHiddenByDndOrLock;
    private bool _disposed;

    private bool _isDragging;
    private int _dragStartX;
    private string? _dismissedAgentId;
    private AgentState? _dismissedAgentState;

    public int PetScale => _scale;
    public int? PetX => _petX;
    public string IdleMode => _idleMode;
    public bool IsHiddenByDndOrLock
    {
        get => _isHiddenByDndOrLock;
        set
        {
            if (_isHiddenByDndOrLock == value) return;
            _isHiddenByDndOrLock = value;
            ApplyEffectiveVisibility();
        }
    }

    public event Action<int>? PetPositionChanged;
    public event Action<AgentSnapshot>? AgentClicked;
    public ContextMenuStrip? StudioContextMenu { get; set; }

    protected override bool ShowWithoutActivation => true;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            ApplyEffectiveVisibility();
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
        _cachedRenderState = _currentState;

        try
        {
            _petBuffer = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
            _dibSurface = new LayeredDibSurface(TaskbarPetRenderer.CanvasWidth * _scale, TaskbarPetRenderer.CanvasHeight * _scale);
            _renderer = new TaskbarPetRenderer();
            _stopwatch = Stopwatch.StartNew();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = false; // Layered windows handle painting via UpdateLayeredWindow

            int fps = Math.Clamp(targetFps, 5, 30);
            _renderTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(16, 1000 / fps)
            };
            _renderTimer.Tick += OnRenderTick;

            UpdateWindowBounds();
        }
        catch
        {
            _renderTimer?.Dispose();
            _dibSurface?.Dispose();
            _petBuffer?.Dispose();
            throw;
        }
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
        ApplyEffectiveVisibility();
    }

    public bool SetScale(int scale)
    {
        int newScale = Math.Clamp(scale, 1, 2);
        if (_scale == newScale) return true;

        try
        {
            _dibSurface.Resize(TaskbarPetRenderer.CanvasWidth * newScale, TaskbarPetRenderer.CanvasHeight * newScale);
        }
        catch
        {
            return false;
        }

        _scale = newScale;
        try
        {
            UpdateWindowBounds();
            if (Visible && !_isPaused && !_isHiddenByDndOrLock)
            {
                RenderAndPresent();
            }
        }
        catch
        {
            // Do NOT return false here because _scale was committed and DIB surface was resized
        }

        return true;
    }

    public void SetIdleMode(string idleMode)
    {
        _idleMode = idleMode;
        ApplyEffectiveVisibility();
    }

    public void SetPetX(int? x)
    {
        _petX = x;
        UpdateWindowBounds();
    }

    public void ApplyRenderState(HqState state, ThemePack theme, bool petPaused, bool petHiddenByDndOrLock)
    {
        _currentState = state ?? _currentState;
        _currentTheme = theme ?? _currentTheme;
        _isPaused = petPaused;
        _isHiddenByDndOrLock = petHiddenByDndOrLock;
        RecomputeCachedRenderState();
        ApplyEffectiveVisibility();
    }

    public void UpdateData(HqState state, ThemePack theme)
    {
        _currentState = state ?? _currentState;
        _currentTheme = theme ?? _currentTheme;
        RecomputeCachedRenderState();
        ApplyEffectiveVisibility();
    }

    private void RecomputeCachedRenderState()
    {
        // Check if dismissed agent is still in that dismissed state
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

        if (_dismissedAgentId == null)
        {
            _cachedRenderState = _currentState;
            return;
        }

        // Cache filtered agent state
        var filtered = new List<AgentSnapshot>(_currentState.Agents.Count);
        foreach (var ag in _currentState.Agents)
        {
            if (ag.Id == _dismissedAgentId)
            {
                filtered.Add(ag with { State = AgentState.Idle, QuestionPrompt = null });
            }
            else
            {
                filtered.Add(ag);
            }
        }
        _cachedRenderState = _currentState with { Agents = filtered };
    }

    public void ApplyEffectiveVisibility()
    {
        if (_disposed) return;

        if (_isPaused || _isHiddenByDndOrLock || _idleMode == "off")
        {
            _renderTimer.Stop();
            if (Visible) Hide();
            return;
        }

        if (_idleMode == "auto-hide")
        {
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

            if (!hasActiveAgent)
            {
                _renderTimer.Stop();
                if (Visible) Hide();
                return;
            }
        }

        if (!Visible)
        {
            Show();
        }
        if (!_renderTimer.Enabled)
        {
            _renderTimer.Start();
        }
        RenderAndPresent();
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
        if (_disposed || _isPaused || _isHiddenByDndOrLock || _idleMode == "off") return;

        if (_idleMode == "auto-hide")
        {
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

            if (!hasActiveAgent)
            {
                _renderTimer.Stop();
                if (Visible) Hide();
                return;
            }
        }

        if (!Visible) Show();
        RenderAndPresent();
    }

    public void RenderAndPresent()
    {
        if (_disposed || !IsHandleCreated) return;

        float timeSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
        bool showIdle = _idleMode != "off";

        _renderer.Render(_petBuffer, _cachedRenderState, _currentTheme, timeSeconds, showIdle);

        if (_scale == 1)
        {
            _dibSurface.CopyFrom(_petBuffer);
        }
        else
        {
            _dibSurface.BlitNearestNeighborFrom(_petBuffer, _scale);
        }

        _dibSurface.UpdateLayeredWindow(Handle, Left, Top);
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
                    AgentSnapshot? soleTerminal = null;
                    int workingCount = 0;
                    var agents = _currentState.Agents;
                    for (int i = 0; i < agents.Count; i++)
                    {
                        if (agents[i].State == AgentState.Working)
                        {
                            workingCount++;
                            if (string.Equals(agents[i].Source, "process", StringComparison.OrdinalIgnoreCase) && agents[i].ProcessId.HasValue)
                            {
                                soleTerminal = agents[i];
                            }
                        }
                    }

                    if (workingCount == 1 && soleTerminal != null && soleTerminal.ProcessId.HasValue)
                    {
                        WindowFocuser.TryFocusProcessWindow(soleTerminal.ProcessId.Value);
                    }
                    else
                    {
                        WindowFocuser.TryFocusOrca();
                    }
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
                    if (agent == null)
                    {
                        agent = new AgentSnapshot(
                            Id: agentId,
                            Name: hit.Label ?? "Shinobi Agent",
                            Role: AgentRole.Leader,
                            SpriteKey: hit.Label?.ToLowerInvariant() ?? "kakashi",
                            State: AgentState.Idle,
                            Source: "fallback"
                        );
                    }

                    if (AgentClicked != null)
                    {
                        AgentClicked.Invoke(agent);
                    }
                    else
                    {
                        AgentDetailToast.ShowToast(agent);
                    }
                    return;
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
                _dibSurface.UpdateLayeredWindow(Handle, Left, Top);
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

    public void SimulateMouseDown(int x, int y, MouseButtons button = MouseButtons.Left)
    {
        OnMouseDown(new MouseEventArgs(button, 1, x, y, 0));
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
                RecomputeCachedRenderState();
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
            _dibSurface.Dispose();
        }

        base.Dispose(disposing);
    }
}
