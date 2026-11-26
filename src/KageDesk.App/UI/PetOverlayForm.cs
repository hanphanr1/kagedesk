using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using KageDesk.App.Native;
using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;
using KageDesk.Rendering.Sprites;

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
    private int _currentDpi = 96;
    private string _idleMode = "chill"; // "chill" | "auto-hide" | "off"
    private bool _showMixiWhenAllIdle = true;
    private string? _monitorDeviceName;
    private int? _petRelativeX;
    private int? _petX;
    private bool _isPaused;
    private bool _isHiddenByDndOrLock;
    private bool _disposed;

    private bool _isDragging;
    private int _dragStartCursorX;
    private int _dragStartWindowLeft;
    private float _wakeUpPulseUntilSeconds = -1f;
    private ContextMenuStrip? _activeMixiMenu;
    private readonly Dictionary<string, AgentState> _dismissedAlerts = new(StringComparer.OrdinalIgnoreCase);

    public int PetScale => _scale;
    public int CurrentDpi => _currentDpi;
    public int? PetX => _petX;
    public string? MonitorDeviceName => _monitorDeviceName;
    public int? PetRelativeX => _petRelativeX;
    public string IdleMode => _idleMode;
    public bool ShowMixiWhenAllIdle => _showMixiWhenAllIdle;
    public bool IsHandoffActive => _renderer.IsHandoffActive;
    public bool IsDramaActive => _renderer.IsDramaActive;
    public bool IsCelebrationActive => _renderer.IsCelebrationActive;
    public bool IsMixiChillActive => _renderer.IsMixiChillActive;
    public bool IsWakeUpPulseActive => _wakeUpPulseUntilSeconds > (float)_stopwatch.Elapsed.TotalSeconds;
    public Func<SceneEffectsState>? GetSceneEffects { get; set; }

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
    public event Action<string, int, int>? PetPlacementChanged;
    public event Action<AgentSnapshot>? AgentClicked;
    public event Action? MixiPoked;
    public event Action<MixiChillActivity?>? MixiActivityPinned;
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

    public PetOverlayForm(
        ThemePack initialTheme,
        int initialScale = 2,
        int? initialPetX = null,
        string idleMode = "chill",
        int targetFps = 15,
        bool showMixiWhenAllIdle = true,
        string? monitorDeviceName = null,
        int? initialRelativeX = null,
        TeamDramaController? sharedDramaController = null)
    {
        _currentTheme = initialTheme ?? throw new ArgumentNullException(nameof(initialTheme));
        _scale = Math.Clamp(initialScale, 1, 2);
        _monitorDeviceName = string.IsNullOrWhiteSpace(monitorDeviceName) ||
                             string.Equals(monitorDeviceName.Trim(), "primary", StringComparison.OrdinalIgnoreCase)
            ? null
            : monitorDeviceName.Trim();
        _petRelativeX = initialRelativeX;
        _petX = initialPetX;
        _idleMode = idleMode;
        _showMixiWhenAllIdle = showMixiWhenAllIdle;

        _currentState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);
        _cachedRenderState = _currentState;

        var initialMonitor = MonitorLayoutHelper.ResolveMonitorFromSystem(_monitorDeviceName);
        _currentDpi = Math.Clamp(initialMonitor.Dpi, 96, 384);
        Size initSize = MonitorLayoutHelper.ComputeDpiScaledPetSize(
            TaskbarPetRenderer.CanvasWidth,
            TaskbarPetRenderer.CanvasHeight,
            _scale,
            _currentDpi);

        try
        {
            _petBuffer = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
            _dibSurface = new LayeredDibSurface(initSize.Width, initSize.Height);
            _renderer = new TaskbarPetRenderer(sharedDramaController);
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_disposed) return;

        int liveDpi = MonitorLayoutHelper.QueryWindowDpi(Handle, _currentDpi);
        if (liveDpi != _currentDpi)
        {
            ApplyDpiChange(liveDpi, suggestedBounds: null);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32Interop.WM_DPICHANGED && !_disposed)
        {
            int newDpi = (int)(((long)m.WParam >> 16) & 0xFFFF);
            if (newDpi <= 0)
            {
                newDpi = (int)((long)m.WParam & 0xFFFF);
            }

            Rectangle? suggested = null;
            if (m.LParam != IntPtr.Zero)
            {
                try
                {
                    var rc = Marshal.PtrToStructure<Win32Interop.RECT>(m.LParam);
                    if (rc.Width > 0 && rc.Height > 0)
                    {
                        suggested = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                    }
                }
                catch { }
            }

            ApplyDpiChange(newDpi, suggested);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
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

        Size newSize = MonitorLayoutHelper.ComputeDpiScaledPetSize(
            TaskbarPetRenderer.CanvasWidth,
            TaskbarPetRenderer.CanvasHeight,
            newScale,
            _currentDpi);

        try
        {
            _dibSurface.Resize(newSize.Width, newSize.Height);
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

    internal bool ApplyDpiChangeForTest(int newDpi, Rectangle? suggestedBounds = null) =>
        ApplyDpiChange(newDpi, suggestedBounds);

    private bool ApplyDpiChange(int newDpi, Rectangle? suggestedBounds)
    {
        if (_disposed) return false;

        int clampedDpi = Math.Clamp(newDpi, 96, 384);
        Size newSize = MonitorLayoutHelper.ComputeDpiScaledPetSize(
            TaskbarPetRenderer.CanvasWidth,
            TaskbarPetRenderer.CanvasHeight,
            _scale,
            clampedDpi);

        try
        {
            _dibSurface.Resize(newSize.Width, newSize.Height);
        }
        catch
        {
            return false;
        }

        _currentDpi = clampedDpi;

        try
        {
            if (suggestedBounds.HasValue)
            {
                var monitors = MonitorLayoutHelper.GetConnectedMonitors();
                var center = new Point(
                    suggestedBounds.Value.Left + suggestedBounds.Value.Width / 2,
                    suggestedBounds.Value.Top + suggestedBounds.Value.Height / 2);
                var mon = MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, center);
                Rectangle docked = MonitorLayoutHelper.ComputeTaskbarDockedBounds(
                    mon.Bounds,
                    mon.WorkingArea,
                    newSize.Width,
                    newSize.Height,
                    _petRelativeX,
                    suggestedBounds.Value.Left);
                SetBounds(docked.Left, docked.Top, docked.Width, docked.Height);
            }
            else
            {
                UpdateWindowBounds();
            }

            if (Visible && !_isPaused && !_isHiddenByDndOrLock && IsHandleCreated)
            {
                RenderAndPresent();
            }
        }
        catch
        {
        }

        return true;
    }

    public void SetIdleMode(string idleMode)
    {
        _idleMode = idleMode;
        ApplyEffectiveVisibility();
    }

    public void SetShowMixiWhenAllIdle(bool enabled)
    {
        _showMixiWhenAllIdle = enabled;
        ApplyEffectiveVisibility();
    }

    public void SetPetX(int? x)
    {
        _petX = x;
        _petRelativeX = null;
        UpdateWindowBounds();
    }

    public void SetMonitorAndPosition(string? deviceName, int? relativeX = null, int? legacyAbsoluteX = null)
    {
        _monitorDeviceName = string.IsNullOrWhiteSpace(deviceName) ||
                             string.Equals(deviceName.Trim(), "primary", StringComparison.OrdinalIgnoreCase)
            ? null
            : deviceName.Trim();
        _petRelativeX = relativeX;
        _petX = legacyAbsoluteX;

        var monitor = MonitorLayoutHelper.ResolveMonitorFromSystem(_monitorDeviceName);
        if (monitor.Dpi != _currentDpi)
        {
            ApplyDpiChange(monitor.Dpi, suggestedBounds: null);
        }
        else
        {
            UpdateWindowBounds();
            if (Visible && !_isPaused && !_isHiddenByDndOrLock)
            {
                RenderAndPresent();
            }
        }
    }

    public void HandleDisplaySettingsChanged()
    {
        if (_disposed) return;

        var monitor = MonitorLayoutHelper.ResolveMonitorFromSystem(_monitorDeviceName);
        int liveDpi = IsHandleCreated
            ? MonitorLayoutHelper.QueryWindowDpi(Handle, monitor.Dpi)
            : monitor.Dpi;

        if (liveDpi != _currentDpi)
        {
            ApplyDpiChange(liveDpi, suggestedBounds: null);
            return;
        }

        UpdateWindowBounds();
        if (Visible && !_isPaused && !_isHiddenByDndOrLock)
        {
            RenderAndPresent();
        }
    }

    public void TriggerWakeUpPulse(float durationSeconds = 3.0f)
    {
        if (_disposed) return;

        float now = (float)_stopwatch.Elapsed.TotalSeconds;
        _wakeUpPulseUntilSeconds = now + Math.Clamp(durationSeconds, 0.5f, 8.0f);
        UpdateWindowBounds();
        ApplyEffectiveVisibility();
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
        // Retain each dismissed (AgentId -> AgentState) entry only while that agent stays in its dismissed state
        if (_dismissedAlerts.Count > 0)
        {
            List<string>? toRemove = null;
            var agents = _currentState.Agents;
            foreach (var kv in _dismissedAlerts)
            {
                bool stillDismissed = false;
                for (int i = 0; i < agents.Count; i++)
                {
                    if (string.Equals(agents[i].Id, kv.Key, StringComparison.OrdinalIgnoreCase) &&
                        agents[i].State == kv.Value)
                    {
                        stillDismissed = true;
                        break;
                    }
                }

                if (!stillDismissed)
                {
                    toRemove ??= new List<string>();
                    toRemove.Add(kv.Key);
                }
            }

            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                {
                    _dismissedAlerts.Remove(toRemove[i]);
                }
            }
        }

        // Never rewrite dismissed NeedsApproval/Failed states to Idle: keep _currentState unfiltered
        // so MixiChillRenderer.ShouldRenderMixiChillCorner stays strictly gated on real agent state.
        _cachedRenderState = _currentState;
    }

    internal IReadOnlyList<PetHitRegion> CurrentHitRegions => _renderer.HitRegions;
    internal ContextMenuStrip? ActiveMixiMenuForTest => _activeMixiMenu;
    internal Size DibSurfaceSizeForTest => new(_dibSurface.Width, _dibSurface.Height);

    internal Point MapClientToCanvas(int clientX, int clientY)
    {
        int cw = Math.Max(1, Width);
        int ch = Math.Max(1, Height);
        int cx = (int)((long)clientX * TaskbarPetRenderer.CanvasWidth / cw);
        int cy = (int)((long)clientY * TaskbarPetRenderer.CanvasHeight / ch);
        return new Point(cx, cy);
    }

    internal bool ShouldStayVisibleInAutoHide()
    {
        // Stay visible during single-instance wake-up pulse
        if (IsWakeUpPulseActive)
        {
            return true;
        }

        // Stay visible when Do Mixi All-Idle Chill Mode is enabled or explicitly demoed
        if (_showMixiWhenAllIdle ||
            string.Equals(_currentState.DemoScenario, "mixi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Stay visible ONLY while a living handoff, review drama, or celebration is active or pending
        if (_renderer.IsHandoffActive ||
            _renderer.IsDramaActive ||
            _renderer.IsCelebrationActive ||
            _renderer.HasUnconsumedHandoffEvent(_currentState))
        {
            return true;
        }

        var agents = _currentState.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            if (agents[i].State != AgentState.Idle)
            {
                return true;
            }
        }

        return false;
    }

    public void ApplyEffectiveVisibility()
    {
        if (_disposed) return;

        if (_isPaused || _isHiddenByDndOrLock || _idleMode == "off")
        {
            _renderTimer.Stop();
            CloseAndDisposeActiveMixiMenu();
            if (Visible) Hide();
            return;
        }

        if (_idleMode == "auto-hide" && !ShouldStayVisibleInAutoHide())
        {
            _renderTimer.Stop();
            CloseAndDisposeActiveMixiMenu();
            if (Visible) Hide();
            return;
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
        Size scaled = MonitorLayoutHelper.ComputeDpiScaledPetSize(
            TaskbarPetRenderer.CanvasWidth,
            TaskbarPetRenderer.CanvasHeight,
            _scale,
            _currentDpi);

        if (_dibSurface.Width != scaled.Width || _dibSurface.Height != scaled.Height)
        {
            _dibSurface.Resize(scaled.Width, scaled.Height);
        }

        var monitor = MonitorLayoutHelper.ResolveMonitorFromSystem(_monitorDeviceName);
        Rectangle docked = MonitorLayoutHelper.ComputeTaskbarDockedBounds(
            monitor.Bounds,
            monitor.WorkingArea,
            scaled.Width,
            scaled.Height,
            _petRelativeX,
            _petX);

        SetBounds(docked.Left, docked.Top, docked.Width, docked.Height);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_disposed || _isPaused || _isHiddenByDndOrLock || _idleMode == "off") return;

        if (_idleMode == "auto-hide" && !ShouldStayVisibleInAutoHide())
        {
            _renderTimer.Stop();
            CloseAndDisposeActiveMixiMenu();
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();
        RenderAndPresent();
    }

    public void RenderAndPresent()
    {
        if (_disposed || !IsHandleCreated) return;

        float timeSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
        bool showIdle = _idleMode != "off";

        SceneEffectsState effects = GetSceneEffects?.Invoke() ?? default;
        float localPulse = _wakeUpPulseUntilSeconds > timeSeconds
            ? _wakeUpPulseUntilSeconds - timeSeconds
            : 0f;
        if (localPulse > effects.WakeUpPulseRemainingSeconds)
        {
            effects = effects with { WakeUpPulseRemainingSeconds = localPulse };
        }

        long monotonicTicks = Stopwatch.GetTimestamp();
        _renderer.Render(
            _petBuffer,
            _currentState,
            _currentTheme,
            timeSeconds,
            showIdle,
            DateTimeOffset.UtcNow,
            _showMixiWhenAllIdle,
            _dismissedAlerts,
            effects,
            monotonicTicks);

        // Detach/close Mixi quick menu promptly whenever Mixi is no longer active
        if (!_renderer.IsMixiChillActive && _activeMixiMenu != null)
        {
            CloseAndDisposeActiveMixiMenu();
        }

        _dibSurface.BlitNearestNeighborScaledFrom(_petBuffer);
        _dibSurface.UpdateLayeredWindow(Handle, Left, Top);
    }

    internal ContextMenuStrip BuildMixiQuickMenu(MixiChillActivity? currentPinned)
    {
        var menu = new ContextMenuStrip();

        var autoItem = new ToolStripMenuItem("Auto Cycle (5 Activities)", null, (_, _) =>
        {
            MixiActivityPinned?.Invoke(null);
        })
        {
            Checked = !currentPinned.HasValue
        };
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());

        (string Label, MixiChillActivity Activity)[] activities =
        {
            ("\u0102n m\u00ec (Eating Noodles)", MixiChillActivity.EatingNoodles),
            ("Coi YouTube", MixiChillActivity.WatchingYouTube),
            ("Ch\u01a1i game", MixiChillActivity.Gaming),
            ("N\u00f3i chuy\u1ec7n stream", MixiChillActivity.StreamingTalk),
            ("H\u00e1t karaoke", MixiChillActivity.SingingKaraoke)
        };

        for (int i = 0; i < activities.Length; i++)
        {
            var entry = activities[i];
            var item = new ToolStripMenuItem(entry.Label, null, (_, _) =>
            {
                MixiActivityPinned?.Invoke(entry.Activity);
            })
            {
                Checked = currentPinned == entry.Activity
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Poke \u0110\u1ed9 Mixi!", null, (_, _) =>
        {
            MixiPoked?.Invoke();
            RenderAndPresent();
        }));

        if (StudioContextMenu != null)
        {
            menu.Items.Add(new ToolStripMenuItem("KageDesk Studio Menu...", null, (_, _) =>
            {
                StudioContextMenu?.Show(Cursor.Position);
            }));
        }

        return menu;
    }

    internal ContextMenuStrip ShowMixiQuickMenu(Point screenLocation)
    {
        CloseAndDisposeActiveMixiMenu();

        MixiChillActivity? currentPinned = GetSceneEffects?.Invoke().PinnedMixiActivity;
        ContextMenuStrip menu = BuildMixiQuickMenu(currentPinned);
        _activeMixiMenu = menu;
        menu.Closed += (_, _) =>
        {
            // Capture the exact closing instance so rapid close/reopen never disposes the new menu
            ContextMenuStrip closingInstance = menu;
            if (!_disposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(() => DisposeMixiMenuInstance(closingInstance)));
                }
                catch
                {
                    DisposeMixiMenuInstance(closingInstance);
                }
            }
            else
            {
                DisposeMixiMenuInstance(closingInstance);
            }
        };
        menu.Show(screenLocation);
        return menu;
    }

    private void CloseAndDisposeActiveMixiMenu()
    {
        var current = _activeMixiMenu;
        if (current == null) return;
        _activeMixiMenu = null;

        try
        {
            if (!current.IsDisposed && current.Visible)
            {
                current.Close(ToolStripDropDownCloseReason.AppFocusChange);
            }
        }
        catch { }

        DisposeMixiMenuInstance(current);
    }

    private void DisposeMixiMenuInstance(ContextMenuStrip menu)
    {
        if (ReferenceEquals(_activeMixiMenu, menu))
        {
            _activeMixiMenu = null;
        }

        if (menu.IsDisposed) return;

        try
        {
            for (int i = menu.Items.Count - 1; i >= 0; i--)
            {
                menu.Items[i].Dispose();
            }
            menu.Dispose();
        }
        catch { }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        Point canvasPt = MapClientToCanvas(e.X, e.Y);
        int unscaledX = canvasPt.X;
        int unscaledY = canvasPt.Y;

        if (e.Button == MouseButtons.Right)
        {
            if (FindHitRegion(unscaledX, unscaledY) is { } rightHit &&
                string.Equals(rightHit.ActionId, "agent:mixi-chill", StringComparison.OrdinalIgnoreCase))
            {
                ShowMixiQuickMenu(Cursor.Position);
                return;
            }

            StudioContextMenu?.Show(Cursor.Position);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            if (FindHitRegion(unscaledX, unscaledY) is { } hit)
            {
                if (hit.ActionId == "open_orca")
                {
                    WindowFocuser.TryFocusOrca();
                    return;
                }
                if (hit.ActionId.StartsWith("focus_agent:"))
                {
                    string agentId = hit.ActionId["focus_agent:".Length..];
                    var agent = _currentState.Agents.FirstOrDefault(a => a.Id == agentId);
                    if (agent is { ProcessId: > 0, ProcessStartTicks: > 0 })
                    {
                        if (!WindowFocuser.TryFocusProcessWindow(agent.ProcessId.Value, agent.ProcessStartTicks))
                        {
                            AgentDetailToast.ShowToast(agent);
                        }
                    }
                    else if (agent != null)
                    {
                        AgentDetailToast.ShowToast(agent);
                    }
                    return;
                }
                if (hit.ActionId == "dismiss")
                {
                    if (!string.IsNullOrEmpty(hit.TargetAgentId) && hit.TargetAgentState.HasValue)
                    {
                        DismissAlert(hit.TargetAgentId, hit.TargetAgentState.Value);
                    }
                    else
                    {
                        DismissActiveApprovalOrFail();
                    }
                    return;
                }
                if (hit.ActionId.StartsWith("agent:"))
                {
                    string agentId = hit.ActionId["agent:".Length..];
                    var agent = _currentState.Agents.FirstOrDefault(a => a.Id == agentId);
                    if (string.Equals(agentId, "mixi-chill", StringComparison.OrdinalIgnoreCase))
                    {
                        MixiPoked?.Invoke();
                        RenderAndPresent();

                        if (AgentClicked != null)
                        {
                            if (agent == null)
                            {
                                var activity = _renderer.ActiveMixiActivity ?? MixiChillActivity.EatingNoodles;
                                string activityLabel = MixiChillRenderer.GetFullBadge(activity);
                                agent = new AgentSnapshot(
                                    Id: "mixi-chill",
                                    Name: "Do Mixi Gaming",
                                    Role: AgentRole.Clone,
                                    SpriteKey: "mixi",
                                    State: AgentState.Idle,
                                    TaskTitle: $"Chill Mode: {activityLabel}",
                                    Source: "idle-chill"
                                );
                            }
                            AgentClicked.Invoke(agent);
                        }
                        return;
                    }

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

            // Start multi-monitor taskbar dragging
            _isDragging = true;
            _dragStartCursorX = Cursor.Position.X;
            _dragStartWindowLeft = Left;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isDragging)
        {
            Point cursorPos = Cursor.Position;
            var monitors = MonitorLayoutHelper.GetConnectedMonitors();
            var targetMonitor = MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, cursorPos);

            if (targetMonitor.Dpi != _currentDpi)
            {
                ApplyDpiChange(targetMonitor.Dpi, suggestedBounds: null);
            }

            int desiredLeft = _dragStartWindowLeft + (cursorPos.X - _dragStartCursorX);
            Rectangle docked = MonitorLayoutHelper.ComputeTaskbarDockedBounds(
                targetMonitor.Bounds,
                targetMonitor.WorkingArea,
                Width,
                Height,
                relativeX: null,
                legacyAbsoluteX: desiredLeft);

            if (docked.Left != Left || docked.Top != Top)
            {
                Left = docked.Left;
                Top = docked.Top;
                _dibSurface.UpdateLayeredWindow(Handle, Left, Top);
            }
        }
        else
        {
            Point canvasPt = MapClientToCanvas(e.X, e.Y);
            Cursor = FindHitRegion(canvasPt.X, canvasPt.Y).HasValue ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_isDragging)
        {
            _isDragging = false;
            var monitors = MonitorLayoutHelper.GetConnectedMonitors();
            var centerPt = new Point(Left + Width / 2, Top + Height / 2);
            var targetMonitor = MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, centerPt);
            CommitDropPlacement(targetMonitor, Left);
        }
    }

    internal void CommitDropPlacement(MonitorDescriptor targetMonitor, int finalLeft)
    {
        // Align live & persisted primary-monitor marker: empty string "" / null for primary, explicit deviceName for secondary
        string persistedDeviceName = targetMonitor.IsPrimary ? "" : targetMonitor.DeviceName;
        _monitorDeviceName = targetMonitor.IsPrimary ? null : targetMonitor.DeviceName;
        _petRelativeX = finalLeft - targetMonitor.WorkingArea.Left;
        _petX = finalLeft;

        PetPlacementChanged?.Invoke(persistedDeviceName, _petRelativeX.Value, finalLeft);
        PetPositionChanged?.Invoke(finalLeft);
    }

    public void SimulateMouseDown(int x, int y, MouseButtons button = MouseButtons.Left)
    {
        OnMouseDown(new MouseEventArgs(button, 1, x, y, 0));
    }

    private PetHitRegion? FindHitRegion(int unscaledX, int unscaledY)
    {
        return _renderer.HitTest(unscaledX, unscaledY);
    }

    internal void DismissAlert(string agentId, AgentState state)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        _dismissedAlerts[agentId] = state;
        RecomputeCachedRenderState();
        RenderAndPresent();
    }

    internal void DismissActiveApprovalOrFail()
    {
        if (!string.IsNullOrEmpty(_renderer.ActiveAlertAgentId) && _renderer.ActiveAlertAgentState.HasValue)
        {
            DismissAlert(_renderer.ActiveAlertAgentId, _renderer.ActiveAlertAgentState.Value);
            return;
        }

        AgentSnapshot? target = null;
        foreach (var ag in _currentState.Agents)
        {
            bool alreadyDismissed = _dismissedAlerts.TryGetValue(ag.Id, out var dismissedState) &&
                ag.State == dismissedState;
            if (alreadyDismissed) continue;

            if (ag.State == AgentState.NeedsApproval)
            {
                target = ag;
                break;
            }
            if (ag.State == AgentState.Failed && target == null)
            {
                target = ag;
            }
        }

        if (target != null)
        {
            DismissAlert(target.Id, target.State);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            CloseAndDisposeActiveMixiMenu();
            _renderTimer.Stop();
            _renderTimer.Dispose();
            _petBuffer.Dispose();
            _dibSurface.Dispose();
        }

        base.Dispose(disposing);
    }
}
