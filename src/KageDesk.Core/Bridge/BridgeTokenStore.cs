using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KageDesk.Core.Bridge;

public static class BridgeTokenStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GetOrCreateToken(string? customPath = null)
    {
        string path = customPath ?? GetDefaultTokenPath();

        if (File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                string? validToken = TryReadTokenZeroToctou(path);
                if (validToken != null)
                {
                    return validToken;
                }

                // Fail closed: invalid ACL, owner, or token content. Delete and recreate.
                try { File.Delete(path); } catch { }
            }
            else
            {
                try
                {
                    string existing = File.ReadAllText(path).TrimStart('\uFEFF').Trim();
                    if (IsValidToken(existing))
                    {
                        return existing;
                    }
                    File.Delete(path);
                }
                catch
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        string newToken = GenerateSecureToken();
        try
        {
            CreateTokenFileWithStrictAcl(path, newToken);
            return newToken;
        }
        catch (IOException) when (File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                string? validToken = TryReadTokenZeroToctou(path);
                if (validToken != null)
                {
                    return validToken;
                }
                throw new InvalidOperationException($"Token file at '{path}' has invalid owner, ACL, or token.");
            }

            string token = File.ReadAllText(path).TrimStart('\uFEFF').Trim();
            if (IsValidToken(token)) return token;
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadTokenZeroToctou(string path)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var security = FileSystemAclExtensions.GetAccessControl(fs);
            if (!VerifySecurity(security, currentUser))
            {
                return null;
            }

            using var reader = new StreamReader(fs, Utf8NoBom);
            string content = reader.ReadToEnd();
            string token = content.TrimStart('\uFEFF').Trim();
            if (IsValidToken(token))
            {
                return token;
            }
        }
        catch
        {
            return null;
        }

        return null;
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

    public static bool HasStrictCurrentUserOnlyAcl(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return false;

            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();
            return VerifySecurity(fileSecurity, currentUser);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool VerifySecurity(FileSecurity security, SecurityIdentifier currentUser)
    {
        if (security == null || currentUser == null)
        {
            return false;
        }

        byte[] rawBytes = security.GetSecurityDescriptorBinaryForm();
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return false;
        }

        var rawSd = new RawSecurityDescriptor(rawBytes, 0);

        // 1. Owner SID must match current user
        if (rawSd.Owner == null || !currentUser.Equals(rawSd.Owner))
        {
            return false;
        }

        // 2. DACL protection must be enabled (inheritance blocked)
        if (!rawSd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
        {
            return false;
        }

        // 3. Discretionary ACL must be present and non-empty
        if (rawSd.DiscretionaryAcl == null || rawSd.DiscretionaryAcl.Count == 0)
        {
            return false;
        }

        // 4. Inspect EVERY raw ACE in the DACL (never silently drop unknown/callback/compound/object ACEs)
        foreach (GenericAce ace in rawSd.DiscretionaryAcl)
        {
            if (ace is not CommonAce commonAce || commonAce.AceType != AceType.AccessAllowed)
            {
                return false;
            }

            if (!currentUser.Equals(commonAce.SecurityIdentifier))
            {
                return false;
            }
        }

        return true;
    }

    private static string GenerateSecureToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void CreateTokenFileWithStrictAcl(string filePath, string token)
    {
        EnsureDirectoryWithAcl(filePath);

        if (OperatingSystem.IsWindows())
        {
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user SID could not be resolved.");

            var fileSecurity = new FileSecurity();
            fileSecurity.SetOwner(currentUser);
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            var fileInfo = new FileInfo(filePath);
            using var stream = FileSystemAclExtensions.Create(
                fileInfo,
                FileMode.CreateNew,
                FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.Delete,
                FileShare.None,
                4096,
                FileOptions.None,
                fileSecurity);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(token);
            writer.Flush();
        }
        else
        {
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(token);
            writer.Flush();
        }
    }

    private static void EnsureDirectoryWithAcl(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;

        if (OperatingSystem.IsWindows())
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                var dirSec = new DirectorySecurity();
                dirSec.SetOwner(currentUser);
                dirSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                dirSec.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                FileSystemAclExtensions.Create(new DirectoryInfo(dir), dirSec);
                return;
            }
        }

        Directory.CreateDirectory(dir);
    }
}
