using System.Drawing;
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

public class V22MultiMonitorWakeUpMediaAndUiTests
{
    private sealed class FakeMediaProbe : IMediaPlaybackProbe
    {
        public MediaPlaybackSnapshot NextSnapshot { get; set; }
        public bool ShouldThrow { get; set; }

        public MediaPlaybackSnapshot Probe()
        {
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
        // Secondary monitor positioned to the LEFT of Primary (-1920..0)
        var virtualScreen = new Rectangle(-1920, -1080, 3840, 2160);
        var leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
        var primaryMonitor = new Rectangle(0, 0, 1920, 1080);

        var leftOffset = MonitorLayoutHelper.ComputeWallpaperVirtualOffset(leftMonitor, virtualScreen);
        Assert.Equal(new Rectangle(0, 1080, 1920, 1080), leftOffset);

        var primaryOffset = MonitorLayoutHelper.ComputeWallpaperVirtualOffset(primaryMonitor, virtualScreen);
        Assert.Equal(new Rectangle(1920, 1080, 1920, 1080), primaryOffset);
    }

    [Fact]
    public void MonitorLayoutHelper_TaskbarDocking_AdaptsToBottomTopAndSecondaryMonitorEdges()
    {
        // Secondary monitor on right with bottom taskbar (40px)
        var monBounds = new Rectangle(1920, 0, 2560, 1440);
        var bottomWork = new Rectangle(1920, 0, 2560, 1400);
        var dockedBottom = MonitorLayoutHelper.ComputeTaskbarDockedBounds(monBounds, bottomWork, 768, 256, relativeX: 200);
        Assert.Equal(2120, dockedBottom.Left);
        Assert.Equal(1400 - 256, dockedBottom.Top);

        // Top taskbar (40px at top)
        var topWork = new Rectangle(1920, 40, 2560, 1400);
        var dockedTop = MonitorLayoutHelper.ComputeTaskbarDockedBounds(monBounds, topWork, 768, 256, relativeX: 9999);
        Assert.Equal(topWork.Right - 768, dockedTop.Left); // clamped safely inside work area
        Assert.Equal(40, dockedTop.Top);

        // Point-to-monitor resolution across virtual desktop
        var monitors = new[]
        {
            new MonitorDescriptor(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96),
            new MonitorDescriptor(@"\\.\DISPLAY2", false, monBounds, bottomWork, 144)
        };
        Assert.Equal(@"\\.\DISPLAY2", MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, new Point(2500, 1200)).DeviceName);
        Assert.Equal(@"\\.\DISPLAY1", MonitorLayoutHelper.ResolveMonitorAtPoint(monitors, new Point(500, 900)).DeviceName);
    }

    [Fact]
    public void SingleInstanceWakeUp_UiDispatcherFormReceivesMessageAndRaisesEvent()
    {
        Assert.NotEqual(0u, SingleInstanceWakeUp.MessageId);

        using var form = new UiDispatcherForm();
        _ = form.Handle;
        Assert.Equal(SingleInstanceWakeUp.DispatcherWindowTitle, form.Text);

        bool wokeUp = false;
        form.WakeUpRequested += () => wokeUp = true;

        Assert.True(SingleInstanceWakeUp.TrySignalRunningInstance(form.Handle));
        for (int i = 0; i < 20 && !wokeUp; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.True(wokeUp);
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
    public void PetOverlayForm_MixiPokeAndQuickActivityMenu_WorkEndToEnd()
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
        petForm.SimulateMouseDown(mixiHit.X + mixiHit.Width / 2, mixiHit.Y + mixiHit.Height / 2, MouseButtons.Left);
        Assert.True(poked);

        // Verify right-click quick activity picker menu has Auto Cycle + all 5 Mixi activities + Poke
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
    public void MediaSessionMonitor_TracksPlaybackWithHysteresisAndSurvivesProbeErrors()
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
}
