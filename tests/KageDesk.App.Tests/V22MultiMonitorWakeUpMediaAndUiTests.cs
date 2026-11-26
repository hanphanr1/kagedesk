using System.Drawing;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using KageDesk.App;
using KageDesk.App.Audio;
using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Scenes;
using KageDesk.Rendering.Sprites;
using Xunit;

namespace KageDesk.App.Tests;

[Collection("WinFormsGdi")]
public class V22MultiMonitorWakeUpMediaAndUiTests
{
    private sealed class FakeMediaProbe : IMediaPlaybackProbe
    {
        public MediaPlaybackSnapshot NextSnapshot { get; set; }
        public bool ShouldThrow { get; set; }
        public Action? OnProbe { get; set; }

        public MediaPlaybackSnapshot Probe()
        {
            OnProbe?.Invoke();
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated COM audio failure");
            }
            return NextSnapshot;
        }
    }

    [Fact]
    public void MonitorLayoutHelper_ResolveMonitor_SelectsSecondaryAndFallsBackToPrimaryWhenDisconnected()
    {
        var monitors = new[]
        {
            new MonitorDescriptor(@"\\.\DISPLAY1", IsPrimary: true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96),
            new MonitorDescriptor(@"\\.\DISPLAY2", IsPrimary: false, new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144)
        };

        var secondary = MonitorLayoutHelper.ResolveMonitor(monitors, @"\\.\DISPLAY2");
        Assert.Equal(@"\\.\DISPLAY2", secondary.DeviceName);
        Assert.Equal(144, secondary.Dpi);

        // Primary defaults
        Assert.Equal(@"\\.\DISPLAY1", MonitorLayoutHelper.ResolveMonitor(monitors, "").DeviceName);
        Assert.Equal(@"\\.\DISPLAY1", MonitorLayoutHelper.ResolveMonitor(monitors, "primary").DeviceName);

        // Disconnected external monitor cleanly falls back to Primary without throwing
        var disconnectedFallback = MonitorLayoutHelper.ResolveMonitor(monitors, @"\\.\DISPLAY9");
        Assert.Equal(@"\\.\DISPLAY1", disconnectedFallback.DeviceName);
        Assert.True(disconnectedFallback.IsPrimary);

        // Menu label formatting includes Primary tag, resolution, and non-100% DPI tag
        Assert.Equal("Display 1 (Primary 1920x1080)", MonitorLayoutHelper.FormatMonitorMenuLabel(monitors[0], 0));
        Assert.Equal("Display 2 (2560x1440 @150%)", MonitorLayoutHelper.FormatMonitorMenuLabel(monitors[1], 1));
    }

    [Fact]
    public void MonitorLayoutHelper_WallpaperVirtualOffset_HandlesNegativeLeftAndTopMonitors()
    {
        // Secondary monitor positioned to the LEFT and ABOVE Primary
        var virtualScreen = new Rectangle(-1920, -1080, 3840, 2160);
        var leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
        var topLeftMonitor = new Rectangle(-1920, -1080, 1920, 1080);
        var primaryMonitor = new Rectangle(0, 0, 1920, 1080);

        Assert.Equal(new Rectangle(0, 1080, 1920, 1080), MonitorLayoutHelper.ComputeWallpaperVirtualOffset(leftMonitor, virtualScreen));
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), MonitorLayoutHelper.ComputeWallpaperVirtualOffset(topLeftMonitor, virtualScreen));
        Assert.Equal(new Rectangle(1920, 1080, 1920, 1080), MonitorLayoutHelper.ComputeWallpaperVirtualOffset(primaryMonitor, virtualScreen));
    }

    [Fact]
    public void MonitorLayoutHelper_TaskbarDocking_AllFourEdgesNegativeCoordinatesAndDpiScaling()
    {
        // Negative-X/Y secondary monitor with Bottom taskbar
        var negBounds = new Rectangle(-1920, -1080, 1920, 1080);
        var negBottomWork = new Rectangle(-1920, -1080, 1920, 1040);
        Assert.Equal(TaskbarEdge.Bottom, MonitorLayoutHelper.DetectTaskbarEdge(negBounds, negBottomWork));
        var dockedNegBottom = MonitorLayoutHelper.ComputeTaskbarDockedBounds(negBounds, negBottomWork, 768, 256, relativeX: 120);
        Assert.Equal(-1800, dockedNegBottom.Left);
        Assert.Equal(-40 - 256, dockedNegBottom.Top);

        // Top taskbar (40px at top)
        var monBounds = new Rectangle(1920, 0, 2560, 1440);
        var topWork = new Rectangle(1920, 40, 2560, 1400);
        Assert.Equal(TaskbarEdge.Top, MonitorLayoutHelper.DetectTaskbarEdge(monBounds, topWork));
        var dockedTop = MonitorLayoutHelper.ComputeTaskbarDockedBounds(monBounds, topWork, 768, 256, relativeX: 9999);
        Assert.Equal(topWork.Right - 768, dockedTop.Left);
        Assert.Equal(40, dockedTop.Top);

        // Left taskbar (64px on left)
        var leftWork = new Rectangle(1984, 0, 2496, 1440);
        Assert.Equal(TaskbarEdge.Left, MonitorLayoutHelper.DetectTaskbarEdge(monBounds, leftWork));
        var dockedLeft = MonitorLayoutHelper.ComputeTaskbarDockedBounds(monBounds, leftWork, 768, 256);
        Assert.Equal(leftWork.Left, dockedLeft.Left);
        Assert.Equal(leftWork.Bottom - 256, dockedLeft.Top);

        // Right taskbar (64px on right)
        var rightWork = new Rectangle(1920, 0, 2496, 1440);
        Assert.Equal(TaskbarEdge.Right, MonitorLayoutHelper.DetectTaskbarEdge(monBounds, rightWork));
        var dockedRight = MonitorLayoutHelper.ComputeTaskbarDockedBounds(monBounds, rightWork, 768, 256);
        Assert.Equal(rightWork.Right - 768, dockedRight.Left);
        Assert.Equal(rightWork.Bottom - 256, dockedRight.Top);

        // DPI scaling: 100% (96 DPI) vs 150% (144 DPI)
        Assert.Equal(new Size(384, 128), MonitorLayoutHelper.ComputeDpiScaledPetSize(384, 128, 1, 96));
        Assert.Equal(new Size(576, 192), MonitorLayoutHelper.ComputeDpiScaledPetSize(384, 128, 1, 144));
        Assert.Equal(new Size(768, 256), MonitorLayoutHelper.ComputeDpiScaledPetSize(384, 128, 2, 96));
        Assert.Equal(new Size(1152, 384), MonitorLayoutHelper.ComputeDpiScaledPetSize(384, 128, 2, 144));

        // Point-to-monitor resolution across negative & positive monitors
        var monitors = new[]
        {
            new MonitorDescriptor(@"\\.\DISPLAY_NEG", false, negBounds, negBottomWork, 96),
            new MonitorDescriptor(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96),
            new MonitorDescriptor(@"\\.\DISPLAY2", false, monBounds, topWork, 144)
        };
        Assert.Equal(@"\\.\DISPLAY_NEG", MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, new Point(-1000, -500)).DeviceName);
        Assert.Equal(@"\\.\DISPLAY2", MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, new Point(2500, 1200)).DeviceName);
        Assert.Equal(@"\\.\DISPLAY1", MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, new Point(500, 900)).DeviceName);
    }

    [Fact]
    public void Win32WallpaperBridge_TryResolveMappedRect_DistinguishesZeroDisplacementFromWin32Error()
    {
        var raw = new Win32Interop.RECT { Left = 10, Top = 20, Right = 1930, Bottom = 1100 };

        // MapWindowPoints returns 0 with LastError == 0 -> valid zero displacement!
        Assert.True(Win32WallpaperBridge.TryResolveMappedRect(0, 0, raw, out var mappedZero));
        Assert.Equal(new Rectangle(10, 20, 1920, 1080), mappedZero);

        // MapWindowPoints returns 0 with non-zero Win32 error -> failure!
        Assert.False(Win32WallpaperBridge.TryResolveMappedRect(0, 1400, raw, out _));

        // Non-zero displacement -> valid mapped rectangle
        Assert.True(Win32WallpaperBridge.TryResolveMappedRect(0x00010001, 0, raw, out var mappedShift));
        Assert.Equal(new Rectangle(10, 20, 1920, 1080), mappedShift);
    }

    [Fact]
    public void DesktopOcclusionMonitor_DualMonitorSplit_WallpaperOcclusionVsFullscreenGameDnd()
    {
        var mon1 = new Rectangle(0, 0, 1920, 1080);
        var mon2 = new Rectangle(1920, 0, 2560, 1440);
        var monitors = new[]
        {
            new MonitorDescriptor(@"\\.\DISPLAY1", true, mon1, new Rectangle(0, 0, 1920, 1040)),
            new MonitorDescriptor(@"\\.\DISPLAY2", false, mon2, new Rectangle(1920, 0, 2560, 1400))
        };

        // Case 1: Maximized browser on Monitor 1 (wallpaper monitor) while Pet is on Monitor 2
        // -> Wallpaper occluded = true, Fullscreen Game DND = false!
        var browserWin = new WindowSnapshot(new IntPtr(100), 9999, "Chrome_WidgetWin_1", IsVisible: true, IsIconic: false, IsCloaked: false, ExStyle: 0, Bounds: new Rectangle(0, 0, 1920, 1040));
        var (wpOcc1, gameDnd1) = DesktopOcclusionMonitor.EvaluateDualMonitorOcclusionAndDnd(
            monitors,
            wallpaperMonitorDeviceName: @"\\.\DISPLAY1",
            petMonitorDeviceName: @"\\.\DISPLAY2",
            quns: Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS,
            windows: new[] { browserWin },
            ownPid: 1234);
        Assert.True(wpOcc1);
        Assert.False(gameDnd1);

        // Case 2: Fullscreen game covering Monitor 2 (pet monitor) while Wallpaper is on Monitor 1
        // -> Wallpaper occluded = false, Fullscreen Game DND = true!
        var gameWin = new WindowSnapshot(new IntPtr(200), 8888, "UnrealWindow", IsVisible: true, IsIconic: false, IsCloaked: false, ExStyle: 0, Bounds: mon2);
        var (wpOcc2, gameDnd2) = DesktopOcclusionMonitor.EvaluateDualMonitorOcclusionAndDnd(
            monitors,
            wallpaperMonitorDeviceName: @"\\.\DISPLAY1",
            petMonitorDeviceName: @"\\.\DISPLAY2",
            quns: Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY,
            windows: new[] { gameWin },
            ownPid: 1234);
        Assert.False(wpOcc2);
        Assert.True(gameDnd2);

        // Case 3: OS reports QUNS_RUNNING_D_3D_FULL_SCREEN -> Game DND = true even without matching rect
        var (_, gameDnd3) = DesktopOcclusionMonitor.EvaluateDualMonitorOcclusionAndDnd(
            monitors,
            wallpaperMonitorDeviceName: @"\\.\DISPLAY1",
            petMonitorDeviceName: @"\\.\DISPLAY2",
            quns: Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D_3D_FULL_SCREEN,
            windows: Array.Empty<WindowSnapshot>(),
            ownPid: 1234);
        Assert.True(gameDnd3);

        // Case 4: QUNS_BUSY without any covering window -> Game DND = false
        var (_, gameDnd4) = DesktopOcclusionMonitor.EvaluateDualMonitorOcclusionAndDnd(
            monitors,
            wallpaperMonitorDeviceName: @"\\.\DISPLAY1",
            petMonitorDeviceName: @"\\.\DISPLAY2",
            quns: Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY,
            windows: Array.Empty<WindowSnapshot>(),
            ownPid: 1234);
        Assert.False(gameDnd4);
    }

    [Fact]
    public async Task SingleInstanceWakeUp_NamedPipeAcl_SignalsRateLimitsRejectsInvalidOpcodeAndRetriesStartupRace()
    {
        string pipeName = $"KageDesk_TestWake_{Guid.NewGuid():N}";
        int wakeCount = 0;
        using var wokeSignal = new ManualResetEventSlim(false);
        var now = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

        using var server = new SingleInstanceWakeUpServer(
            onWakeUp: () =>
            {
                Interlocked.Increment(ref wakeCount);
                wokeSignal.Set();
            },
            pipeName: pipeName,
            rateLimitWindow: TimeSpan.FromSeconds(2),
            clock: () => now);

        // 1. Bounded client retry handles startup race (client starts ~120ms before server starts)
        using var clientEntered = new ManualResetEventSlim(false);
        var raceClientTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientThread = new Thread(() =>
        {
            clientEntered.Set();
            try
            {
                bool ok = SingleInstanceWakeUp.TrySignalRunningInstance(pipeName, maxRetryWindow: TimeSpan.FromMilliseconds(2500));
                raceClientTcs.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                raceClientTcs.TrySetException(ex);
            }
        })
        { IsBackground = true };
        clientThread.Start();

        Assert.True(clientEntered.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(120);
        server.Start();

        bool delivered = await raceClientTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delivered, $"TrySignalRunningInstance returned false. Server LastListenException={server.LastListenException}");
        Assert.True(wokeSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref wakeCount));

        // 2. Second immediate signal within 2s rate-limit window is ACKed without double-triggering UI wake-up
        Assert.True(SingleInstanceWakeUp.TrySignalRunningInstance(pipeName, maxRetryWindow: TimeSpan.FromMilliseconds(1000)));
        await Task.Delay(80);
        Assert.Equal(1, Volatile.Read(ref wakeCount));

        // 3. Invalid opcode is rejected (does NOT trigger callback!)
        now = now.AddSeconds(5);
        using (var badClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            badClient.Connect(1000);
            byte[] badOpcode = Encoding.ASCII.GetBytes("BAD_OP00");
            badClient.Write(badOpcode, 0, badOpcode.Length);
            badClient.Flush();
            int ack = badClient.ReadByte();
            Assert.NotEqual(1, ack);
        }
        await Task.Delay(80);
        Assert.Equal(1, Volatile.Read(ref wakeCount));

        // 4. After rate-limit window passes, valid wake-up fires again!
        wokeSignal.Reset();
        Assert.True(SingleInstanceWakeUp.TrySignalRunningInstance(pipeName, maxRetryWindow: TimeSpan.FromMilliseconds(1000)));
        Assert.True(wokeSignal.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref wakeCount));
    }

    [Fact]
    public void PetOverlayForm_WakeUpPulse_ForcesVisibilityEvenInAutoHideWithMixiOff()
    {
        using var petForm = new PetOverlayForm(
            ThemePackLoader.BuiltInFallback,
            initialScale: 1,
            idleMode: "auto-hide",
            showMixiWhenAllIdle: false);

        Assert.False(petForm.ShouldStayVisibleInAutoHide());
        Assert.False(petForm.IsWakeUpPulseActive);

        petForm.TriggerWakeUpPulse(3.0f);

        Assert.True(petForm.IsWakeUpPulseActive);
        Assert.True(petForm.ShouldStayVisibleInAutoHide());
    }

    [Fact]
    public void PetOverlayForm_PerMonitorDpiChangeAndPrimaryDragMarkerAlignment()
    {
        using var petForm = new PetOverlayForm(
            ThemePackLoader.BuiltInFallback,
            initialScale: 1,
            idleMode: "chill",
            showMixiWhenAllIdle: true);

        _ = petForm.Handle;

        // Apply 150% DPI (144 DPI) -> DIB surface & form bounds resize to 576x192!
        Assert.True(petForm.ApplyDpiChangeForTest(144));
        Assert.Equal(144, petForm.CurrentDpi);
        Assert.Equal(new Size(576, 192), petForm.DibSurfaceSizeForTest);
        Assert.Equal(576, petForm.Width);
        Assert.Equal(192, petForm.Height);

        // Hit-test mapping scales 576x192 client point back to 384x128 canvas accurately
        Point mapped = petForm.MapClientToCanvas(288, 96);
        Assert.Equal(new Point(192, 64), mapped);

        // RenderAndPresent at 150% DPI succeeds cleanly via BlitNearestNeighborScaledFrom
        petForm.RenderAndPresent();

        // Drag drop onto primary monitor persists "" (empty marker) aligned with live MonitorDeviceName == null
        string? persistedDevice = "unset";
        petForm.PetPlacementChanged += (dev, _, _) => persistedDevice = dev;

        var primaryMon = new MonitorDescriptor(@"\\.\DISPLAY1", IsPrimary: true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96);
        petForm.CommitDropPlacement(primaryMon, 400);
        Assert.Null(petForm.MonitorDeviceName);
        Assert.Equal("", persistedDevice);

        var secondaryMon = new MonitorDescriptor(@"\\.\DISPLAY2", IsPrimary: false, new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144);
        petForm.CommitDropPlacement(secondaryMon, 2100);
        Assert.Equal(@"\\.\DISPLAY2", petForm.MonitorDeviceName);
        Assert.Equal(@"\\.\DISPLAY2", persistedDevice);
    }

    [Fact]
    public void PetOverlayForm_MixiPokeAndQuickActivityMenu_RapidReopenSafeAndDetachesWhenIneligible()
    {
        using var petForm = new PetOverlayForm(
            ThemePackLoader.BuiltInFallback,
            initialScale: 1,
            idleMode: "chill",
            showMixiWhenAllIdle: true);

        _ = petForm.Handle;
        petForm.RenderAndPresent();

        bool poked = false;
        petForm.MixiPoked += () => poked = true;

        var mixiHit = petForm.CurrentHitRegions.First(h => h.ActionId == "agent:mixi-chill");
        int clientX = (int)((long)(mixiHit.X + mixiHit.Width / 2) * petForm.Width / TaskbarPetRenderer.CanvasWidth);
        int clientY = (int)((long)(mixiHit.Y + mixiHit.Height / 2) * petForm.Height / TaskbarPetRenderer.CanvasHeight);
        petForm.SimulateMouseDown(clientX, clientY, MouseButtons.Left);
        Assert.True(poked);

        // Rapid close/reopen: opening menu2 while menu1 closes MUST NOT dispose menu2!
        var menu1 = petForm.ShowMixiQuickMenu(new Point(100, 100));
        var menu2 = petForm.ShowMixiQuickMenu(new Point(110, 110));
        Application.DoEvents();

        Assert.True(menu1.IsDisposed);
        Assert.False(menu2.IsDisposed);
        Assert.Same(menu2, petForm.ActiveMixiMenuForTest);

        // When state transitions to a Working agent (Mixi ineligible), active Mixi menu closes & detaches!
        var workingState = new HqState(
            Agents: new[] { new AgentSnapshot("minato", "Minato", AgentRole.Coder, "minato", AgentState.Working, "Coding", "CODE") },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);
        petForm.UpdateData(workingState, ThemePackLoader.BuiltInFallback);
        petForm.RenderAndPresent();
        Application.DoEvents();

        Assert.Null(petForm.ActiveMixiMenuForTest);
        Assert.True(menu2.IsDisposed);

        // Verify right-click quick activity picker menu items & callbacks
        MixiChillActivity? pinnedSelected = MixiChillActivity.EatingNoodles;
        bool pinRaised = false;
        petForm.MixiActivityPinned += act =>
        {
            pinRaised = true;
            pinnedSelected = act;
        };

        using var quickMenu = petForm.BuildMixiQuickMenu(MixiChillActivity.SingingKaraoke);
        var menuItems = quickMenu.Items.OfType<ToolStripMenuItem>().ToList();
        Assert.True(menuItems.Count >= 7); // Auto + 5 activities + Poke

        var karaokeItem = menuItems.First(m => (m.Text ?? "").Contains("karaoke", StringComparison.OrdinalIgnoreCase));
        Assert.True(karaokeItem.Checked);

        var autoItem = menuItems.First(m => (m.Text ?? "").Contains("Auto Cycle", StringComparison.OrdinalIgnoreCase));
        Assert.False(autoItem.Checked);
        autoItem.PerformClick();

        Assert.True(pinRaised);
        Assert.Null(pinnedSelected);
    }

    [Fact]
    public void WallpaperHostForm_HitTestAndLeftClickOnMixi_RaisesMixiPoked()
    {
        using var wpForm = new WallpaperHostForm(ThemePackLoader.BuiltInFallback, targetFps: 15, showMixiWhenAllIdle: true);
        wpForm.Size = new Size(768, 432);
        wpForm.RenderFrameNow();

        Assert.True(wpForm.IsMixiChillActive);

        bool poked = false;
        wpForm.MixiPoked += () => poked = true;

        // Canvas Mixi corner center is roughly x=192, y=170 -> at 2x (768x432) that is (384, 340)
        Assert.True(wpForm.HitTestMixi(384, 340));
        wpForm.SimulateMouseDown(384, 340, MouseButtons.Left);
        Assert.True(poked);
    }

    [Fact]
    public void MediaSessionMonitor_TracksPlaybackWithHysteresisGenerationGateAndSurvivesProbeErrors()
    {
        var probe = new FakeMediaProbe();
        using var monitor = new MediaSessionMonitor(enabled: true, pollIntervalMs: 5000, pauseHysteresisTicks: 2, probe: probe);

        var transitions = new List<MediaPlaybackSnapshot>();
        monitor.PlaybackChanged += (_, snap) => transitions.Add(snap);

        // Playing triggers immediately
        probe.NextSnapshot = new MediaPlaybackSnapshot(true, "spotify");
        monitor.PollOnce();
        Assert.True(monitor.IsPlaying);
        Assert.Equal("spotify", monitor.CurrentSource);
        Assert.Single(transitions);

        // 1st silent tick held by 2-tick hysteresis
        probe.NextSnapshot = new MediaPlaybackSnapshot(false, null);
        monitor.PollOnce();
        Assert.True(monitor.IsPlaying);

        // 2nd silent tick drops to paused
        monitor.PollOnce();
        Assert.False(monitor.IsPlaying);
        Assert.Equal(2, transitions.Count);

        // Probe exceptions never crash the monitor
        probe.ShouldThrow = true;
        monitor.PollOnce();
        Assert.False(monitor.IsPlaying);

        // Monotonic enableGeneration gate: if disabled mid-probe, stale playing sample MUST be discarded!
        probe.ShouldThrow = false;
        probe.NextSnapshot = new MediaPlaybackSnapshot(true, "spotify");
        probe.OnProbe = () => monitor.SetEnabled(false);
        monitor.PollOnce();
        probe.OnProbe = null;
        Assert.False(monitor.IsPlaying);

        // Allowlist privacy check: only known media apps pass
        Assert.Equal("Spotify", CoreAudioMediaPlaybackProbe.MapAllowlistedMediaApp("Spotify"));
        Assert.Equal("Chrome", CoreAudioMediaPlaybackProbe.MapAllowlistedMediaApp("chrome"));
        Assert.Null(CoreAudioMediaPlaybackProbe.MapAllowlistedMediaApp("keepass"));
        Assert.Null(CoreAudioMediaPlaybackProbe.MapAllowlistedMediaApp("notepad"));
    }

    [Theory]
    [InlineData(SoundKind.MixiChirp)]
    [InlineData(SoundKind.Drama)]
    [InlineData(SoundKind.Celebrate)]
    public void RetroSoundEngine_V22Sounds_GenerateValidWavBuffers(SoundKind kind)
    {
        byte[] wav = RetroSoundEngine.BuildWavBytes(kind);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
    }

    [Theory]
    [InlineData("--setup-hooks", true)]
    [InlineData("--uninstall-hooks", true)]
    [InlineData("--benchmark", true)]
    [InlineData("--snapshot", true)]
    [InlineData("--record-gif", true)]
    [InlineData("--version", true)]
    [InlineData("-v", true)]
    [InlineData("--help", true)]
    [InlineData("-h", true)]
    [InlineData("/?", true)]
    [InlineData("--no-wallpaper", false)]
    public void Program_IsOneShotCliCommand_ClassifiesFlagsCorrectly(string flag, bool expected)
    {
        Assert.Equal(expected, Program.IsOneShotCliCommand(new[] { flag }));
    }

    [Fact]
    public void Program_HandleHookCommand_IsolatedTempHome_InstallsAndUninstallsCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-cli-hooks-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        Directory.CreateDirectory(fakeHome);

        try
        {
            int installCode = Program.HandleHookCommand(install: true, userProfileDir: fakeHome, appDataDir: fakeAppData, bridgePort: 4319);
            Assert.Equal(0, installCode);
            Assert.True(File.Exists(Path.Combine(fakeHome, ".claude", "settings.json")));

            int uninstallCode = Program.HandleHookCommand(install: false, userProfileDir: fakeHome, appDataDir: fakeAppData, bridgePort: 4319);
            Assert.Equal(0, uninstallCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrayController_RebuildMenu_ExposesMonitorDramaHooksSessionLogAndMediaSyncItems()
    {
        using var tray = new TrayController(() => { })
        {
            GetConfig = () => new AppConfig(),
            GetConnectedMonitors = () => new[]
            {
                new MonitorDescriptor(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96),
                new MonitorDescriptor(@"\\.\DISPLAY2", false, new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144)
            }
        };

        tray.RebuildMenu();
        var topItems = tray.ContextMenu.Items.OfType<ToolStripMenuItem>().ToList();

        var monitorMenu = topItems.First(i => i.Text == "Monitor");
        var wpMonMenu = monitorMenu.DropDownItems.OfType<ToolStripMenuItem>().First(i => i.Text == "Wallpaper Monitor");
        var petMonMenu = monitorMenu.DropDownItems.OfType<ToolStripMenuItem>().First(i => i.Text == "Taskbar Pet Monitor");
        Assert.Equal(3, wpMonMenu.DropDownItems.Count); // Primary (Auto) + Display 1 + Display 2
        Assert.Equal(3, petMonMenu.DropDownItems.Count);

        var demoMenu = topItems.First(i => i.Text == "Demo Studio");
        var demoTexts = demoMenu.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();
        Assert.Contains(demoTexts, t => t.Contains("Review Drama", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(demoTexts, t => t.Contains("Squad Celebration", StringComparison.OrdinalIgnoreCase));

        var settingsMenu = topItems.First(i => i.Text == "Settings");
        var settingsTexts = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();
        Assert.Contains(settingsTexts, t => t.Contains("Now-Playing Media Sync", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(settingsTexts, t => t.Contains("Live Session Log Watcher", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(settingsTexts, t => t.Contains("Install CLI Hooks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(settingsTexts, t => t.Contains("Remove CLI Hooks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaSync_DisableRacingLatePlayingCallback_NeverLeavesAnimationsStuckOn()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-mediarace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new KageDesk.Core.Config.ConfigStore(Path.Combine(tempDir, "config.json"));
            store.Save(new AppConfig
            {
                EnableMediaSync = true,
                WallpaperEnabled = false,
                PetOverlayEnabled = false,
                EnableProcessAgentCollector = false,
                EnableSessionLogWatcher = false,
                EnableGitHubCiCollector = false,
                LocalBridgePort = 0
            });

            var probe = new FakeMediaProbe
            {
                NextSnapshot = new MediaPlaybackSnapshot(true, "Spotify")
            };

            using var runtime = new KageDesk.App.Services.KageDeskRuntime(store, noWallpaper: true, mediaProbe: probe);
            using var reachedBeforeInvoke = new ManualResetEventSlim(false);
            using var allowInvoke = new ManualResetEventSlim(false);

            runtime.MediaMonitor.BeforeInvokePlaybackChangedForTest = snap =>
            {
                if (snap.IsPlaying)
                {
                    reachedBeforeInvoke.Set();
                    allowInvoke.Wait(TimeSpan.FromSeconds(5));
                }
            };

            // 1. Background poll observes Playing=true and pauses right before firing PlaybackChanged(true)
            Task pollTask = Task.Run(() => runtime.MediaMonitor.PollOnce());
            Assert.True(reachedBeforeInvoke.Wait(TimeSpan.FromSeconds(3)));

            // 2. UI disables Media Sync while PlaybackChanged(true) is in-flight!
            runtime.SetMediaSyncEnabledForTest(false);
            Assert.False(runtime.MediaMonitor.IsMediaPlaying);
            Assert.False(runtime.GetCurrentSceneEffects().IsMediaPlaying);

            // 3. Release the late PlaybackChanged(true) callback into KageDeskRuntime.OnMediaPlaybackChanged
            allowInvoke.Set();
            await pollTask.WaitAsync(TimeSpan.FromSeconds(3));

            // Even after the late Playing=true event fires, runtime MUST stay false (never stuck playing!)
            Assert.False(runtime.MediaMonitor.IsMediaPlaying);
            Assert.False(runtime.GetCurrentSceneEffects().IsMediaPlaying);

            // Direct simulated late callback with Playing=true also cannot override authoritative monitor state
            runtime.SimulateMediaPlaybackCallbackForTest(new MediaPlaybackSnapshot(true, "Spotify"));
            Assert.False(runtime.GetCurrentSceneEffects().IsMediaPlaying);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SingleInstanceWakeUp_RejectsTrailingBytesBeforeAck_BoundsStalledServerTimeout_AndDisposesWithoutLeak()
    {
        // 1. Trailing bytes in single message OR queued second message MUST be rejected BEFORE sending ACK!
        string pipeName = $"KageDesk_TestTrail_{Guid.NewGuid():N}";
        int wakeCount = 0;
        using (var server = new SingleInstanceWakeUpServer(
            onWakeUp: () => Interlocked.Increment(ref wakeCount),
            pipeName: pipeName,
            rateLimitWindow: TimeSpan.Zero))
        {
            server.Start();

            // 1a. Oversized message with trailing bytes ("KDWK0001EXTRA")
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                client.Connect(1000);
                byte[] oversized = Encoding.ASCII.GetBytes("KDWK0001EXTRA");
                client.Write(oversized, 0, oversized.Length);
                client.Flush();
                int ack = client.ReadByte();
                Assert.NotEqual(1, ack);
            }
            await Task.Delay(60);
            Assert.Equal(0, Volatile.Read(ref wakeCount));

            // 1b. Exact 8 bytes followed immediately by extra trailing byte on same connection
            using (var client2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                client2.Connect(1000);
                byte[] extra = Encoding.ASCII.GetBytes("KDWK0001!");
                client2.Write(extra, 0, extra.Length);
                client2.Flush();
                int ack = client2.ReadByte();
                Assert.NotEqual(1, ack);
            }
            await Task.Delay(60);
            Assert.Equal(0, Volatile.Read(ref wakeCount));
        }

        // 2. Stalled server (accepts pipe connection and never replies) MUST NOT hang TrySignalRunningInstance past retry budget!
        string stalledPipe = $"KageDesk_TestStall_{Guid.NewGuid():N}";
        using (var stalledServer = NamedPipeServerStreamAcl.Create(
            stalledPipe,
            PipeDirection.InOut,
            2,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous,
            64,
            64,
            SingleInstanceWakeUp.CreateCurrentUserPipeSecurity()))
        {
            using var stallCts = new CancellationTokenSource();
            Task acceptTask = stalledServer.WaitForConnectionAsync(stallCts.Token);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool signaled = SingleInstanceWakeUp.TrySignalRunningInstance(stalledPipe, maxRetryWindow: TimeSpan.FromMilliseconds(350));
            sw.Stop();

            stallCts.Cancel();
            try { await acceptTask; } catch { }

            Assert.False(signaled);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected stalled pipe signal to abort promptly within retry budget, took {sw.ElapsedMilliseconds}ms.");
        }

        // 3. Immediate Start() + Dispose() rapid cycle MUST NOT leak pipe handles and allows immediate reuse
        string recyclePipe = $"KageDesk_TestRecycle_{Guid.NewGuid():N}";
        for (int i = 0; i < 10; i++)
        {
            var srv = new SingleInstanceWakeUpServer(() => { }, pipeName: recyclePipe);
            srv.Start();
            srv.Dispose();
        }

        using var finalServer = new SingleInstanceWakeUpServer(() => Interlocked.Increment(ref wakeCount), pipeName: recyclePipe);
        finalServer.Start();
        Assert.True(SingleInstanceWakeUp.TrySignalRunningInstance(recyclePipe, maxRetryWindow: TimeSpan.FromMilliseconds(1000)));
    }

    [Fact]
    public void Win32WallpaperBridge_TryComputeHostClientRect_ReturnsFalseOnMapWindowPointsFailureWithoutFallback()
    {
        var monitorBounds = new Rectangle(-1920, 0, 1920, 1080);
        var fakeHwnd = new IntPtr(0x1234);

        // 1. Zero or dead window handle -> false (NO virtualOffset fallback!)
        Assert.False(Win32WallpaperBridge.TryComputeHostClientRect(IntPtr.Zero, monitorBounds, out var empty1));
        Assert.Equal(Rectangle.Empty, empty1);

        Assert.False(Win32WallpaperBridge.TryComputeHostClientRect(
            fakeHwnd,
            monitorBounds,
            out var empty2,
            isWindow: _ => false));
        Assert.Equal(Rectangle.Empty, empty2);

        // 2. MapWindowPoints fails (result == 0, LastError != 0) -> returns false so caller rolls back or triggers recovery!
        Assert.False(Win32WallpaperBridge.TryComputeHostClientRect(
            fakeHwnd,
            monitorBounds,
            out var empty3,
            isWindow: _ => true,
            mapWindowPoints: (_, r) => (0, 1400, r)));
        Assert.Equal(Rectangle.Empty, empty3);

        // 3. Valid mapping (including zero-displacement result == 0 with LastError == 0) -> returns true with exact mapped rect
        Assert.True(Win32WallpaperBridge.TryComputeHostClientRect(
            fakeHwnd,
            monitorBounds,
            out var mapped,
            isWindow: _ => true,
            mapWindowPoints: (_, _) => (0, 0, new Win32Interop.RECT(0, 0, 1920, 1080))));
        Assert.Equal(new Rectangle(0, 0, 1920, 1080), mapped);
    }

    [Fact]
    public async Task SingleInstanceWakeUp_CrossElevationSameUserAllowed_DifferentUserRejected_AndProtectedReadWriteDacl()
    {
        SecurityIdentifier currentUserSid = WindowsIdentity.GetCurrent().User!;
        var adminsGroupSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var differentUserSid = new SecurityIdentifier("S-1-5-21-3623811015-3361044348-30300820-9999");

        // 1. Protected per-user DACL: ReadWrite ONLY to current user, explicit Owner SID == current user
        PipeSecurity sec = SingleInstanceWakeUp.CreateCurrentUserPipeSecurity();
        Assert.True(sec.AreAccessRulesProtected);
        Assert.Equal(currentUserSid, sec.GetOwner(typeof(SecurityIdentifier)));

        var rules = sec.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();
        Assert.Single(rules);
        Assert.Equal(currentUserSid, rules[0].IdentityReference);
        Assert.Equal(AccessControlType.Allow, rules[0].AccessControlType);
        Assert.True((rules[0].PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);
        Assert.Equal(0, (int)(rules[0].PipeAccessRights & PipeAccessRights.ChangePermissions));
        Assert.Equal(0, (int)(rules[0].PipeAccessRights & PipeAccessRights.TakeOwnership));

        // 2. Same-user mixed-elevation policy:
        //    Standard user token (User=U1, DefaultOwner=U1) vs Elevated token (User=U1, DefaultOwner=BUILTIN\Administrators) -> ALLOWED!
        var standardUserToken = new SingleInstanceWakeUp.PeerTokenIdentity(currentUserSid, currentUserSid);
        var elevatedSameUserToken = new SingleInstanceWakeUp.PeerTokenIdentity(currentUserSid, adminsGroupSid);
        var differentStandardUserToken = new SingleInstanceWakeUp.PeerTokenIdentity(differentUserSid, differentUserSid);
        var differentElevatedUserToken = new SingleInstanceWakeUp.PeerTokenIdentity(differentUserSid, adminsGroupSid);

        Assert.True(SingleInstanceWakeUp.IsAuthorizedCrossElevationPeer(standardUserToken, elevatedSameUserToken));
        Assert.True(SingleInstanceWakeUp.IsAuthorizedCrossElevationPeer(elevatedSameUserToken, standardUserToken));
        Assert.False(SingleInstanceWakeUp.IsAuthorizedCrossElevationPeer(standardUserToken, differentStandardUserToken));
        Assert.False(SingleInstanceWakeUp.IsAuthorizedCrossElevationPeer(elevatedSameUserToken, differentElevatedUserToken));

        // 3. Live Named Pipe server-side peer User SID verification:
        //    3a. Same-user peer (simulating elevated client with identical User SID) -> ALLOWED & ACKed
        string pipeAllow = $"KageDesk_TestElevAllow_{Guid.NewGuid():N}";
        int allowCount = 0;
        using var allowSignal = new ManualResetEventSlim(false);
        using (var allowServer = new SingleInstanceWakeUpServer(
            onWakeUp: () =>
            {
                Interlocked.Increment(ref allowCount);
                allowSignal.Set();
            },
            pipeName: pipeAllow,
            rateLimitWindow: TimeSpan.Zero,
            clock: null,
            expectedUserSid: currentUserSid,
            resolvePeerUserSidOverride: _ => currentUserSid))
        {
            allowServer.Start();
            var attempts = new List<string>();
            bool ok = SingleInstanceWakeUp.TrySignalRunningInstanceCore(
                pipeAllow,
                maxRetryWindow: TimeSpan.FromMilliseconds(2500),
                expectedUserSid: currentUserSid,
                resolveServerOwnerSid: null,
                onAttemptFailure: attempts.Add);
            Assert.True(ok, $"Expected same-user wake to succeed. LastListenException={allowServer.LastListenException} Attempts=[{string.Join(" | ", attempts)}]");
            Assert.True(allowSignal.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, Volatile.Read(ref allowCount));
        }

        //    3b. Different User SID connecting to server -> REJECTED before ACK, callback NEVER invoked!
        string pipeRejectServer = $"KageDesk_TestElevRejectSrv_{Guid.NewGuid():N}";
        int rejectServerCount = 0;
        using (var rejectServer = new SingleInstanceWakeUpServer(
            onWakeUp: () => Interlocked.Increment(ref rejectServerCount),
            pipeName: pipeRejectServer,
            rateLimitWindow: TimeSpan.Zero,
            clock: null,
            expectedUserSid: currentUserSid,
            resolvePeerUserSidOverride: _ => differentUserSid))
        {
            rejectServer.Start();
            bool ok = SingleInstanceWakeUp.TrySignalRunningInstanceCore(
                pipeRejectServer,
                maxRetryWindow: TimeSpan.FromMilliseconds(300),
                expectedUserSid: currentUserSid,
                resolveServerOwnerSid: null);
            Assert.False(ok);
            await Task.Delay(60);
            Assert.Equal(0, Volatile.Read(ref rejectServerCount));
        }

        //    3c. Client connecting to pipe owned by a different User SID (e.g. Administrators or another user) -> REJECTED immediately!
        string pipeRejectClient = $"KageDesk_TestElevRejectCli_{Guid.NewGuid():N}";
        int rejectClientCount = 0;
        using (var server = new SingleInstanceWakeUpServer(
            onWakeUp: () => Interlocked.Increment(ref rejectClientCount),
            pipeName: pipeRejectClient,
            rateLimitWindow: TimeSpan.Zero))
        {
            server.Start();
            bool ok = SingleInstanceWakeUp.TrySignalRunningInstanceCore(
                pipeRejectClient,
                maxRetryWindow: TimeSpan.FromMilliseconds(1000),
                expectedUserSid: currentUserSid,
                resolveServerOwnerSid: _ => differentUserSid);
            Assert.False(ok);
            await Task.Delay(60);
            Assert.Equal(0, Volatile.Read(ref rejectClientCount));
        }
    }
}
