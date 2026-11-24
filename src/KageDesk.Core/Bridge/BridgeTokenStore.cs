using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace KageDesk.Core.Bridge;

public static class BridgeTokenStore
{
    public static string GetOrCreateToken(string? customPath = null)
    {
        string path = customPath ?? GetDefaultTokenPath();

        if (File.Exists(path))
        {
            try
            {
                string token = File.ReadAllText(path).Trim();
                if (IsValidToken(token))
                {
                    return token;
                }
            }
            catch
            {
                // Re-create if corrupt
            }
        }

        string newToken = GenerateSecureToken();
        SaveTokenSecurely(path, newToken);
        return newToken;
    }

    public static string GetDefaultTokenPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KageDesk",
            "bridge.token");
    }

    public static bool IsValidToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
            return false;

        foreach (char c in token)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    private static string GenerateSecureToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void SaveTokenSecurely(string filePath, string token)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, token);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                ApplyWindowsCurrentUserAcl(filePath);
            }
            catch
            {
                // Best-effort ACL application (e.g. FAT32 or network shares may not support NTFS ACLs)
            }
        }
    }

    private static void ApplyWindowsCurrentUserAcl(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return;

        var fileInfo = new FileInfo(filePath);
        var security = fileInfo.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User;
        if (user != null)
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
    }
}
