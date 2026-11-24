using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using Xunit;

namespace KageDesk.Core.Tests;

public class SafeProcessRunnerAndTokenTests
{
    [Fact]
    public void BridgeTokenStore_GeneratesValid64CharHexToken_AndPersists()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_token_{Guid.NewGuid():N}.token");
        try
        {
            string token1 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.NotNull(token1);
            Assert.Equal(64, token1.Length);
            Assert.True(BridgeTokenStore.IsValidToken(token1));

            // Calling again loads the same token
            string token2 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.Equal(token1, token2);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)] // 'g' is not hex
    public void BridgeTokenStore_IsValidToken_ValidatesStrictly(string? token, bool expectedValid)
    {
        Assert.Equal(expectedValid, BridgeTokenStore.IsValidToken(token));
    }

    [Theory]
    [InlineData("dotnet/runtime", true)]
    [InlineData("hanphanr1/kagedesk", true)]
    [InlineData("org_1/repo-name.test", true)]
    [InlineData("invalid-repo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("../owner/repo", false)]
    [InlineData("owner/repo;echo test", false)]
    public void SafeProcessRunner_IsValidGitHubRepo_ValidatesStrictly(string? repo, bool expectedValid)
    {
        Assert.Equal(expectedValid, SafeProcessRunner.IsValidGitHubRepo(repo));
    }

    [Fact]
    public void ExecutableResolver_ResolvesKnownSystemExe_FromPathOnly()
    {
        ExecutableResolver.ClearCache();
        string? resolved = ExecutableResolver.Resolve("cmd");
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.EndsWith(".exe", resolved, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExecutableResolver_NonExistentCommand_ReturnsNull()
    {
        ExecutableResolver.ClearCache();
        string? resolved = ExecutableResolver.Resolve("definitely_non_existent_binary_xyz_123");
        Assert.Null(resolved);
    }
}
