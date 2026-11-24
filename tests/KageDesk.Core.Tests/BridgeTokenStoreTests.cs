using System.Security.AccessControl;
using System.Security.Principal;
using KageDesk.Core.Bridge;

namespace KageDesk.Core.Tests;

public class BridgeTokenStoreTests
{
    [Fact]
    public void VerifySecurity_ProtectedDaclWithRetainedForeignAce_FailsAndGetOrCreateReplacesCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_dacl_foreign_{Guid.NewGuid():N}.token");
        try
        {
            string originalToken = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            var currentUser = WindowsIdentity.GetCurrent().User!;
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            // Keep DACL protected, owned by current user, with current user Allow ACE + a retained/foreign ACE
            var fileInfo = new FileInfo(tempPath);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.Read,
                AccessControlType.Allow));
            fileInfo.SetAccessControl(security);

            var reloadedSecurity = fileInfo.GetAccessControl();
            Assert.True(reloadedSecurity.AreAccessRulesProtected);
            Assert.False(BridgeTokenStore.VerifySecurity(reloadedSecurity, currentUser));
            Assert.False(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Fail closed: GetOrCreateToken must replace the file with a fresh token and strict current-user-only ACL
            string replacedToken = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.NotEqual(originalToken, replacedToken);
            Assert.True(BridgeTokenStore.IsValidToken(replacedToken));
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void VerifySecurity_ProtectedDaclWithSystemOrAdminAce_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var secWithSystem = new FileSecurity();
        secWithSystem.SetOwner(currentUser);
        secWithSystem.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        secWithSystem.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        secWithSystem.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(BridgeTokenStore.VerifySecurity(secWithSystem, currentUser));

        var secWithAdmin = new FileSecurity();
        secWithAdmin.SetOwner(currentUser);
        secWithAdmin.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        secWithAdmin.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        secWithAdmin.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(BridgeTokenStore.VerifySecurity(secWithAdmin, currentUser));
    }

    [Fact]
    public void VerifySecurity_ProtectedDaclWithDenyRule_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.Write, AccessControlType.Deny));

        Assert.False(BridgeTokenStore.VerifySecurity(security, currentUser));
    }

    [Fact]
    public void VerifySecurity_ProtectedDaclWithPreservedParentInheritance_FailsAndReplacesOnLoad()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk_dacl_inh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, "bridge.token");
        try
        {
            string token1 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Re-enable inheritance from parent temp directory, then protect while preserving inherited ACEs
            var fileInfo = new FileInfo(tempPath);
            var sec = fileInfo.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            fileInfo.SetAccessControl(sec);

            sec = fileInfo.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            fileInfo.SetAccessControl(sec);

            var currentUser = WindowsIdentity.GetCurrent().User!;
            var inspected = fileInfo.GetAccessControl();
            // On standard Windows temp directories, preserved ACEs include SYSTEM / Administrators
            var rules = inspected.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            bool hasForeignSid = false;
            foreach (AuthorizationRule r in rules)
            {
                if (!currentUser.Equals(r.IdentityReference))
                {
                    hasForeignSid = true;
                    break;
                }
            }

            if (hasForeignSid)
            {
                Assert.False(BridgeTokenStore.VerifySecurity(inspected, currentUser));

                string token2 = BridgeTokenStore.GetOrCreateToken(tempPath);
                Assert.NotEqual(token1, token2);
                Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
