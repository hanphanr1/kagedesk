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
    public void RecordGif_ShowcaseScenario_ProducesValid72FrameGif()
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
            Assert.Equal(DemoGifRecorder.TotalFrames, progressReports);

            // Verify with GDI+ decoder
            using var img = Image.FromFile(tmpPath);
            Assert.Equal(384, img.Width);
            Assert.Equal(216, img.Height);

            var dimension = new FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = img.GetFrameCount(dimension);
            Assert.Equal(72, frameCount);
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
            Assert.Equal(72, frameCount);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
