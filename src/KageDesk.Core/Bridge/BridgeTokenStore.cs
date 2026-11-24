using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("KageDesk.Core.Tests")]

namespace KageDesk.Core.Bridge;

public static class BridgeTokenStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const uint SE_FILE_OBJECT = 1u;
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001u;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004u;

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInfo,
        out IntPtr pSidOwner,
        out IntPtr pSidGroup,
        out IntPtr pDacl,
        out IntPtr pSacl,
        out IntPtr ppSecurityDescriptor);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSecurityDescriptor(IntPtr pSecurityDescriptor);

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr pSecurityDescriptor);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

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
            if (!VerifyFileHandleSecurity(fs.SafeFileHandle, currentUser))
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

    [SupportedOSPlatform("windows")]
    private static bool VerifyFileHandleSecurity(SafeFileHandle handle, SecurityIdentifier currentUser)
    {
        IntPtr pSd = IntPtr.Zero;
        try
        {
            uint result = GetSecurityInfo(
                handle,
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                out _,
                out _,
                out _,
                out _,
                out pSd);

            if (result != 0 || pSd == IntPtr.Zero || !IsValidSecurityDescriptor(pSd))
            {
                return false;
            }

            uint length = GetSecurityDescriptorLength(pSd);
            if (length == 0 || length > int.MaxValue)
            {
                return false;
            }

            byte[] rawBytes = new byte[(int)length];
            Marshal.Copy(pSd, rawBytes, 0, rawBytes.Length);
            return VerifyRawSecurityDescriptor(rawBytes, currentUser);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pSd != IntPtr.Zero)
            {
                LocalFree(pSd);
            }
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
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return VerifyFileHandleSecurity(fs.SafeFileHandle, currentUser);
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
        return VerifyRawSecurityDescriptor(rawBytes, currentUser);
    }

    [SupportedOSPlatform("windows")]
    internal static bool VerifyRawSecurityDescriptor(byte[]? rawDescriptorBytes, SecurityIdentifier? currentUser)
    {
        if (rawDescriptorBytes == null || rawDescriptorBytes.Length == 0 || currentUser == null)
        {
            return false;
        }

        RawSecurityDescriptor rawSd;
        try
        {
            rawSd = new RawSecurityDescriptor(rawDescriptorBytes, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return false;
        }

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

            if (commonAce.IsInherited)
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
