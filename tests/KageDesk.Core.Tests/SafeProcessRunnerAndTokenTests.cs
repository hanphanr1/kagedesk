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

    [Fact]
    public void SafeProcessRunner_PathFullyQualified_RejectsDriveRelativePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(Path.IsPathFullyQualified(@"\relative\path"));
            Assert.False(Path.IsPathFullyQualified(@"C:relative\path"));
            Assert.True(Path.IsPathFullyQualified(@"C:\fully\qualified\path"));
        }
    }

    [Fact]
    public void ExecutableResolver_RejectsCmdAndBatFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk_cmd_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "script.cmd"), "@echo off\r\n");
            File.WriteAllText(Path.Combine(tempDir, "script.bat"), "@echo off\r\n");

            string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            try
            {
                Environment.SetEnvironmentVariable("PATH", tempDir + ";" + oldPath);
                ExecutableResolver.ClearCache();

                Assert.Null(ExecutableResolver.Resolve("script"));
                Assert.Null(ExecutableResolver.Resolve("script.cmd"));
                Assert.Null(ExecutableResolver.Resolve("script.bat"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
                ExecutableResolver.ClearCache();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BridgeTokenStore_FailClosedAcl_RecreatesCleanlyOnPermissiveAcl()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_acl_test_{Guid.NewGuid():N}.token");
        try
        {
            string token1 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Create permissive ACL (enable inheritance and allow Everyone)
            var fileInfo = new FileInfo(tempPath);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                everyone,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            fileInfo.SetAccessControl(security);

            // Permissive ACL should be detected as invalid
            Assert.False(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Calling GetOrCreateToken fails closed: deletes invalid file and recreates with strict ACL
            string token2 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.NotEqual(token1, token2);
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));
            Assert.True(BridgeTokenStore.IsValidToken(token2));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void BridgeTokenStore_WritesBomFreeUtf8()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_nobom_{Guid.NewGuid():N}.token");
        try
        {
            string token = BridgeTokenStore.GetOrCreateToken(tempPath);
            byte[] bytes = File.ReadAllBytes(tempPath);

            Assert.Equal(64, bytes.Length);
            // Assert written bytes do NOT start with UTF-8 BOM (0xEF, 0xBB, 0xBF)
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "Token file must not contain a UTF-8 BOM");
            Assert.Equal(token, System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void BridgeTokenStore_ReadsLegacyBomPrefixedToken_Successfully()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_bomlegacy_{Guid.NewGuid():N}.token");
        try
        {
            string rawToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            // Write legacy BOM + token + \r\n
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(rawToken + "\r\n");
            byte[] fullBytes = bom.Concat(tokenBytes).ToArray();

            // Create file with strict ACL
            BridgeTokenStore.CreateTokenFileWithStrictAcl(tempPath, rawToken);
            // Overwrite content with BOM-prefixed content while keeping ACL
            File.WriteAllBytes(tempPath, fullBytes);

            string readToken = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.Equal(rawToken, readToken);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
