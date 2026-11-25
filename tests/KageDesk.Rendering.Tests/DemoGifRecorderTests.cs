using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Export;
using Xunit;

namespace KageDesk.Rendering.Tests;

public class DemoGifRecorderTests
{
    [Fact]
    public void RecordGif_ShowcaseScenario_ProducesValid75FrameGif()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"kagedesk-test-showcase-{Guid.NewGuid():N}.gif");
        try
        {
            var theme = ThemePackLoader.BuiltInFallback;
            int progressReports = 0;

            DemoGifRecorder.RecordGif(
                tmpPath,
                theme,
                scenario: "showcase",
                scale: 1,
                onProgress: (cur, tot) => progressReports++);

            Assert.True(File.Exists(tmpPath));
            var fi = new FileInfo(tmpPath);
            Assert.True(fi.Length > 1024, $"GIF size was {fi.Length} bytes, expected > 1024 bytes");
            Assert.True(fi.Length < 1024 * 1024, $"GIF size was {fi.Length} bytes, expected < 1 MB");
            Assert.Equal(75, DemoGifRecorder.TotalFrames);
            Assert.Equal(75, progressReports);

            // Verify with GDI+ decoder across ALL 75 frames
            using var img = Image.FromFile(tmpPath);
            Assert.Equal(384, img.Width);
            Assert.Equal(216, img.Height);

            var dimension = new FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = img.GetFrameCount(dimension);
            Assert.Equal(75, frameCount);

            using var composite = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(composite);
            for (int f = 0; f < frameCount; f++)
            {
                img.SelectActiveFrame(dimension, f);
                g.DrawImage(img, 0, 0);
                var p = composite.GetPixel(10, 10);
                Assert.True(p.A > 0, $"Frame {f} composite pixel had 0 alpha");
            }
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    [Fact]
    public void RecordGif_HandoffScenario_ProducesValidGif()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"kagedesk-test-handoff-{Guid.NewGuid():N}.gif");
        try
        {
            var theme = ThemePackLoader.BuiltInFallback;

            DemoGifRecorder.RecordGif(
                tmpPath,
                theme,
                scenario: "handoff",
                scale: 1);

            Assert.True(File.Exists(tmpPath));
            var fi = new FileInfo(tmpPath);
            Assert.True(fi.Length > 1024);

            using var img = Image.FromFile(tmpPath);
            var dimension = new FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = img.GetFrameCount(dimension);
            Assert.Equal(75, frameCount);

            using var composite = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(composite);
            for (int f = 0; f < frameCount; f++)
            {
                img.SelectActiveFrame(dimension, f);
                g.DrawImage(img, 0, 0);
                var p = composite.GetPixel(10, 10);
                Assert.True(p.A > 0, $"Handoff frame {f} composite pixel had 0 alpha");
            }
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    [Fact]
    public void RecordGif_CancelledMidRecording_ThrowsOperationCanceledException_AndLeavesNoTmpOrOutputFile()
    {
        string outDir = Path.Combine(Path.GetTempPath(), $"kagedesk-cancel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "test.gif");

        try
        {
            var theme = ThemePackLoader.BuiltInFallback;
            using var cts = new CancellationTokenSource();

            Assert.Throws<OperationCanceledException>(() =>
            {
                DemoGifRecorder.RecordGif(
                    outPath,
                    theme,
                    scenario: "showcase",
                    scale: 1,
                    onProgress: (frame, total) =>
                    {
                        if (frame >= 2)
                        {
                            cts.Cancel();
                        }
                    },
                    cancellationToken: cts.Token);
            });

            Assert.False(File.Exists(outPath));

            var tmpFiles = Directory.GetFiles(outDir, "*.tmp");
            Assert.Empty(tmpFiles);
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                try { Directory.Delete(outDir, true); } catch { }
            }
        }
    }
}

