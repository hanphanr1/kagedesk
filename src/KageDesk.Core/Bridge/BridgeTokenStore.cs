using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KageDesk.Core.Bridge;

public static class BridgeTokenStore
{
    public static string GetOrCreateToken(string? customPath = null)
    {
        string path = customPath ?? GetDefaultTokenPath();

        if (File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                if (!HasStrictCurrentUserOnlyAcl(path))
                {
                    // Fail closed: invalid or permissive ACL, delete file to recreate with strict ACL
                    File.Delete(path);
                }
                else
                {
                    try
                    {
                        string existing = File.ReadAllText(path).Trim();
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
            else
            {
                try
                {
                    string existing = File.ReadAllText(path).Trim();
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
            if (OperatingSystem.IsWindows() && !HasStrictCurrentUserOnlyAcl(path))
            {
                throw new InvalidOperationException($"Token file at '{path}' has invalid or permissive ACL.");
            }
            string token = File.ReadAllText(path).Trim();
            if (IsValidToken(token)) return token;
            throw;
        }
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
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();
            if (!fileSecurity.AreAccessRulesProtected)
            {
                // Inheritance is not disabled
                return false;
            }

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return false;

            var rules = fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            if (rules.Count == 0) return false;

            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

            bool hasCurrentUser = false;

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    return false;

                if (rule.IdentityReference is SecurityIdentifier sid)
                {
                    if (sid == usersSid || sid == everyoneSid || sid == authUsersSid)
                    {
                        return false;
                    }

                    if (sid == currentUser)
                    {
                        hasCurrentUser = true;
                    }
                    else if (sid != systemSid && sid != adminSid)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            return hasCurrentUser;
        }
        catch
        {
            return false;
        }
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
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(token);
            writer.Flush();
        }
        else
        {
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
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
