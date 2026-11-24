using System.Security.AccessControl;
using System.Security.Principal;
using KageDesk.Core.Bridge;

namespace KageDesk.Core.Tests;

public class BridgeTokenStoreTests
{
    [Fact]
    public void VerifyRawSecurityDescriptor_CallbackOrCustomAce_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;

        // 1. Callback ACE (AceType.AccessAllowedCallback)
        var rawDaclCallback = new RawAcl(RawAcl.AclRevision, 2);
        rawDaclCallback.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        var callbackAce = new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.Read,
            currentUser,
            isCallback: true,
            opaque: new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(AceType.AccessAllowedCallback, callbackAce.AceType);
        rawDaclCallback.InsertAce(1, callbackAce);

        var rawSdCallback = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDaclCallback);

        byte[] callbackBytes = new byte[rawSdCallback.BinaryLength];
        rawSdCallback.GetBinaryForm(callbackBytes, 0);
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(callbackBytes, currentUser));

        // 2. CustomAce (user-defined ACE type)
        var rawDaclCustom = new RawAcl(RawAcl.AclRevision, 2);
        rawDaclCustom.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        rawDaclCustom.InsertAce(1, new CustomAce(
            (AceType)0xFF,
            AceFlags.None,
            new byte[] { 0x01, 0x02, 0x03, 0x04 }));

        var rawSdCustom = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDaclCustom);

        byte[] customBytes = new byte[rawSdCustom.BinaryLength];
        rawSdCustom.GetBinaryForm(customBytes, 0);
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(customBytes, currentUser));
    }

    [Fact]
    public void VerifyRawSecurityDescriptor_InheritedForeignAce_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var foreignSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        var rawDacl = new RawAcl(RawAcl.AclRevision, 2);
        rawDacl.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        rawDacl.InsertAce(1, new CommonAce(
            AceFlags.Inherited,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.Read,
            foreignSid,
            isCallback: false,
            opaque: null));

        var rawSd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDacl);

        byte[] bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);

        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(bytes, currentUser));
    }

    [Fact]
    public void VerifyRawSecurityDescriptor_ForeignOwner_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var foreignOwner = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var rawDacl = new RawAcl(RawAcl.AclRevision, 1);
        rawDacl.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));

        var rawSd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            foreignOwner,
            foreignOwner,
            systemAcl: null,
            discretionaryAcl: rawDacl);

        byte[] bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);

        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(bytes, currentUser));
    }

    [Fact]
    public void VerifyRawSecurityDescriptor_ValidCurrentUserOnlyProtected_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;

        var rawDacl = new RawAcl(RawAcl.AclRevision, 1);
        rawDacl.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));

        var rawSd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDacl);

        byte[] bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);

        Assert.True(BridgeTokenStore.VerifyRawSecurityDescriptor(bytes, currentUser));
    }

    [Fact]
    public void VerifyRawSecurityDescriptor_InvalidArguments_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(null, currentUser));
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(Array.Empty<byte>(), currentUser));
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(new byte[] { 0x01, 0x02, 0x03 }, currentUser));
        Assert.False(BridgeTokenStore.VerifyRawSecurityDescriptor(new byte[100], null));
    }

    [Fact]
    public void GetOrCreateToken_FreshlyCreatedFile_PassesValidationViaGetSecurityInfo()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempPath = Path.Combine(Path.GetTempPath(), $"kagedesk_token_roundtrip_{Guid.NewGuid():N}.token");
        try
        {
            string token1 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.True(BridgeTokenStore.IsValidToken(token1));
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Reading an existing valid token file uses GetSecurityInfo raw descriptor verification
            string token2 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.Equal(token1, token2);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

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
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var foreignSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            // Deterministically grant an inheritable foreign SID rule on the temporary parent folder
            var dirInfo = new DirectoryInfo(tempDir);
            var dirSec = dirInfo.GetAccessControl();
            dirSec.AddAccessRule(new FileSystemAccessRule(
                foreignSid,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(dirSec);

            string token1 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            // Re-enable inheritance from parent directory, then protect while preserving inherited ACEs
            var fileInfo = new FileInfo(tempPath);
            var sec = fileInfo.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            fileInfo.SetAccessControl(sec);

            sec = fileInfo.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            fileInfo.SetAccessControl(sec);

            var inspected = fileInfo.GetAccessControl();
            var rawInspected = new RawSecurityDescriptor(inspected.GetSecurityDescriptorBinaryForm(), 0);
            Assert.NotNull(rawInspected.DiscretionaryAcl);
            bool foundForeignSid = false;
            foreach (GenericAce ace in rawInspected.DiscretionaryAcl)
            {
                if (ace is CommonAce commonAce && foreignSid.Equals(commonAce.SecurityIdentifier))
                {
                    foundForeignSid = true;
                    break;
                }
            }
            Assert.True(foundForeignSid, "Expected inherited foreign SID ACE to be present in raw DACL");

            // Deterministically verify rejection and regeneration with a clean current-user-only raw DACL
            Assert.False(BridgeTokenStore.VerifySecurity(inspected, currentUser));
            Assert.False(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            string token2 = BridgeTokenStore.GetOrCreateToken(tempPath);
            Assert.NotEqual(token1, token2);
            Assert.True(BridgeTokenStore.IsValidToken(token2));
            Assert.True(BridgeTokenStore.HasStrictCurrentUserOnlyAcl(tempPath));

            var regeneratedSec = new FileInfo(tempPath).GetAccessControl();
            var rawRegenerated = new RawSecurityDescriptor(regeneratedSec.GetSecurityDescriptorBinaryForm(), 0);
            Assert.Equal(currentUser, rawRegenerated.Owner);
            Assert.True(rawRegenerated.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.NotNull(rawRegenerated.DiscretionaryAcl);
            Assert.True(rawRegenerated.DiscretionaryAcl.Count > 0);
            foreach (GenericAce ace in rawRegenerated.DiscretionaryAcl)
            {
                var commonAce = Assert.IsType<CommonAce>(ace);
                Assert.Equal(AceType.AccessAllowed, commonAce.AceType);
                Assert.Equal(currentUser, commonAce.SecurityIdentifier);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void VerifySecurity_RawDaclWithInheritedForeignCommonAce_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        var rawDacl = new RawAcl(RawAcl.AclRevision, 2);
        rawDacl.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        rawDacl.InsertAce(1, new CommonAce(
            AceFlags.Inherited,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.Read,
            worldSid,
            isCallback: false,
            opaque: null));

        var rawSd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDacl);

        byte[] binary = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(binary, 0);

        var fileSecurity = new FileSecurity();
        fileSecurity.SetSecurityDescriptorBinaryForm(binary);

        Assert.False(BridgeTokenStore.VerifySecurity(fileSecurity, currentUser));
    }

    [Fact]
    public void VerifySecurity_RawDaclWithCallbackOrNonCommonAce_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentUser = WindowsIdentity.GetCurrent().User!;

        var rawDacl = new RawAcl(RawAcl.AclRevision, 2);
        rawDacl.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        // Callback Allow ACE: FileSecurity.GetAccessRules silently drops it, but raw DACL inspection MUST reject it
        rawDacl.InsertAce(1, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.Read,
            currentUser,
            isCallback: true,
            opaque: new byte[] { 0, 0, 0, 0 }));

        var rawSd = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDacl);

        byte[] binary = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(binary, 0);

        var fileSecurity = new FileSecurity();
        fileSecurity.SetSecurityDescriptorBinaryForm(binary);

        Assert.False(BridgeTokenStore.VerifySecurity(fileSecurity, currentUser));

        // Non-CommonAce (ObjectAce): must also be rejected by raw DACL inspection
        var rawDaclWithObjectAce = new RawAcl(RawAcl.AclRevisionDS, 2);
        rawDaclWithObjectAce.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            currentUser,
            isCallback: false,
            opaque: null));
        rawDaclWithObjectAce.InsertAce(1, new ObjectAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.Read,
            currentUser,
            ObjectAceFlags.None,
            Guid.Empty,
            Guid.Empty,
            isCallback: false,
            opaque: null));

        var rawSdObj = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: rawDaclWithObjectAce);

        byte[] objBinary = new byte[rawSdObj.BinaryLength];
        rawSdObj.GetBinaryForm(objBinary, 0);

        var fileSecurityObj = new FileSecurity();
        fileSecurityObj.SetSecurityDescriptorBinaryForm(objBinary);

        Assert.False(BridgeTokenStore.VerifySecurity(fileSecurityObj, currentUser));
    }
}
