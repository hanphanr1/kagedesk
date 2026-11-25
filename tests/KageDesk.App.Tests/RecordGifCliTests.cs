using System.IO;
using KageDesk.App;
using Xunit;

namespace KageDesk.App.Tests;

public class RecordGifCliTests
{
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--bogus")]
    [InlineData("--bad-flag")]
    public void HandleRecordGif_UnknownFlag_Returns2(string flag)
    {
        int code = Program.HandleRecordGif(new[] { "--record-gif", flag });
        Assert.Equal(2, code);
    }

    [Theory]
    [InlineData("--scale")]
    [InlineData("--theme")]
    [InlineData("--scenario")]
    [InlineData("--out")]
    public void HandleRecordGif_MissingValue_Returns2(string flag)
    {
        int code = Program.HandleRecordGif(new[] { "--record-gif", flag });
        Assert.Equal(2, code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void HandleRecordGif_InvalidScale_Returns2(string scaleStr)
    {
        int code = Program.HandleRecordGif(new[] { "--record-gif", "--scale", scaleStr });
        Assert.Equal(2, code);
    }

    [Fact]
    public void HandleRecordGif_UnknownScenario_Returns2()
    {
        int code = Program.HandleRecordGif(new[] { "--record-gif", "--scenario", "invalid-scenario" });
        Assert.Equal(2, code);
    }

    [Fact]
    public void HandleRecordGif_UnknownTheme_Returns2()
    {
        int code = Program.HandleRecordGif(new[] { "--record-gif", "--theme", "nonexistent-theme-xyz" });
        Assert.Equal(2, code);
    }

    [Fact]
    public void HandleRecordGif_DirectoryDestination_Returns2()
    {
        string dir = Path.GetTempPath();
        int code = Program.HandleRecordGif(new[] { "--record-gif", dir });
        Assert.Equal(2, code);
    }
}
