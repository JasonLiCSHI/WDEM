using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class WindowsSecureArtifactDirectoryPolicyIntegrationTests
{
  [WindowsFact]
  public void CreatePlanArtifactDirectory_RejectsUserOwnedUnprotectedSharedRoot()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-shared-root-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);

    try
    {
      var policy = new WindowsPlanArtifactDirectoryPolicy(root);

      Assert.Throws<SecurityException>(() => policy.CreateRestrictedStagingDirectory());
      Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    finally
    {
      Directory.Delete(basePath, recursive: true);
    }
  }

  [WindowsFact]
  public void CreatePlanArtifactDirectory_MissingSharedRootFailsClosed()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-missing-root-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");

    try
    {
      var policy = new WindowsPlanArtifactDirectoryPolicy(root);

      var error = Assert.Throws<SecurityException>(
          () => policy.CreateRestrictedStagingDirectory());

      Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
      Assert.False(Directory.Exists(basePath));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void ValidateIdentityNeutralRootSecurity_AllowsOnlyMinimalUsersCreationRights()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateIdentityNeutralRootSecurity(
        Administrators);

    WindowsPlanArtifactDirectoryPolicy.ValidateIdentityNeutralRootSecurity(security);

    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var usersRule = Assert.Single(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>(), rule => users.Equals(rule.IdentityReference));
    Assert.Equal(
        FileSystemRights.ReadAndExecute |
            FileSystemRights.CreateDirectories |
            FileSystemRights.Synchronize,
        usersRule.FileSystemRights);
    Assert.Equal(InheritanceFlags.None, usersRule.InheritanceFlags);
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.Delete));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.ChangePermissions));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.TakeOwnership));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_AllowsUsersOnlyMonotonicAppendRights()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity();

    WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security);

    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var usersRule = Assert.Single(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>(), rule => users.Equals(rule.IdentityReference));
    Assert.Equal(
        FileSystemRights.AppendData |
            FileSystemRights.ReadPermissions |
            FileSystemRights.Synchronize,
        usersRule.FileSystemRights);
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.Delete));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.ChangePermissions));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.TakeOwnership));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_RejectsUsersRewriteAccess()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity();
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        FileSystemRights.WriteData,
        AccessControlType.Allow));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_RejectsUntrustedOwner()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity();
    security.SetOwner(TestSid(1001));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_RejectsUnprotectedDacl()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity();
    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_RejectsInheritedRule()
  {
    var security = AddInheritedFileRule(
        WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity(),
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        FileSystemRights.Read,
        AccessControlType.Allow);

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security));
  }

  [WindowsFact]
  public void ValidateRevocationLedgerSecurity_RejectsDenyRule()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateRevocationLedgerSecurity();
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        FileSystemRights.Read,
        AccessControlType.Deny));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRevocationLedgerSecurity(security));
  }

  [WindowsFact]
  public void RevocationLedger_CorruptAndTruncatedRecordsDoNotHideLaterValidRecord()
  {
    const string ownershipToken = "00112233445566778899AABBCCDDEEFF";
    const string directoryName = "00112233445566778899aabbccddeeff";
    var contents = Encoding.ASCII.GetBytes(
        "garbage\n" +
        "wdem-vsix-revoked-v1:00112233445566778899AABBCCDDEEFF:truncated\n" +
        "wdem-vsix-revoked-v1:00112233445566778899AABBCCDDEEFF:" +
        "00112233445566778899aabbccddeeff\n");

    Assert.True(WindowsPlanArtifactDirectoryPolicy.ContainsRevocationRecord(
        contents,
        ownershipToken,
        directoryName));
  }

  [WindowsFact]
  public void RevocationLedger_TruncatedTargetIsIgnoredAndGarbagePrefixCannotHideRecord()
  {
    const string ownershipToken = "00112233445566778899AABBCCDDEEFF";
    const string directoryName = "00112233445566778899aabbccddeeff";
    const string record = "wdem-vsix-revoked-v1:00112233445566778899AABBCCDDEEFF:" +
        "00112233445566778899aabbccddeeff\n";

    Assert.False(WindowsPlanArtifactDirectoryPolicy.ContainsRevocationRecord(
        Encoding.ASCII.GetBytes(record[..^1]),
        ownershipToken,
        directoryName));
    Assert.True(WindowsPlanArtifactDirectoryPolicy.ContainsRevocationRecord(
        Encoding.ASCII.GetBytes("garbage" + record),
        ownershipToken,
        directoryName));
  }

  [Fact]
  public void RevocationLedger_MalformedIssuanceCannotHideLaterValidRecord()
  {
    const string ownershipToken = "00112233445566778899AABBCCDDEEFF";
    const string directoryName = "00112233445566778899aabbccddeeff";
    var contents = Encoding.ASCII.GetBytes(
        "attacker-controlled-garbage" +
        "wdem-vsix-issued-v1:00112233445566778899AABBCCDDEEFF:" +
        "00112233445566778899aabbccddeeff:9999999999999999999\n" +
        "wdem-vsix-issued-v1:00112233445566778899AABBCCDDEEFF:" +
        "00112233445566778899aabbccddeeff:0638712864000000000\n");
    using var ledger = new MemoryStream(contents, writable: false);

    var expiry = VsixPlanArtifactLedger.GetIssuedExpiry(
        ledger,
        ownershipToken,
        directoryName);

    Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), expiry);
  }

  [WindowsFact]
  public void RevocationLedger_ConcurrentAppendsProduceCompleteDiscoverableRecords()
  {
    const int recordCount = 128;
    var path = Path.Combine(Path.GetTempPath(), $"wdem-revocations-{Guid.NewGuid():N}");
    File.WriteAllBytes(path, []);
    var records = Enumerable.Range(0, recordCount)
        .Select(index => (
            Token: index.ToString("X32"),
            Directory: Guid.NewGuid().ToString("N")))
        .ToArray();

    try
    {
      Parallel.ForEach(records, record =>
      {
        using var handle = OpenAppendOnly(path);
        WindowsPlanArtifactDirectoryPolicy.WriteRevocationRecord(
            handle,
            record.Token,
            record.Directory);
      });

      var contents = File.ReadAllBytes(path);
      Assert.Equal(recordCount, contents.Count(value => value == (byte)'\n'));
      foreach (var record in records)
      {
        Assert.True(WindowsPlanArtifactDirectoryPolicy.ContainsRevocationRecord(
            contents,
            record.Token,
            record.Directory));
      }
    }
    finally
    {
      File.Delete(path);
    }
  }

  [WindowsFact]
  public void RevocationLedger_RecordBeyondFormerSizeLimitRemainsDiscoverable()
  {
    const string ownershipToken = "00112233445566778899AABBCCDDEEFF";
    const string directoryName = "00112233445566778899aabbccddeeff";
    var path = Path.Combine(Path.GetTempPath(), $"wdem-large-revocations-{Guid.NewGuid():N}");
    try
    {
      using (var padding = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
      {
        padding.SetLength((64L * 1024 * 1024) + 1);
      }

      using (var handle = OpenAppendOnly(path))
      {
        WindowsPlanArtifactDirectoryPolicy.WriteRevocationRecord(
            handle,
            ownershipToken,
            directoryName);
      }

      using var ledger = File.OpenRead(path);
      Assert.True(VsixPlanArtifactLedger.ContainsRevokedRecord(
          ledger,
          ownershipToken,
          directoryName));
    }
    finally
    {
      File.Delete(path);
    }
  }

  [WindowsFact]
  public void RevocationLedger_MissingRootFailsClosed()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-missing-ledger-root-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    var ownershipToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    var directoryName = Guid.NewGuid().ToString("N");

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.AppendRevocation(
            root,
            ownershipToken,
            directoryName));
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ContainsRevocation(
            root,
            ownershipToken,
            directoryName));
    Assert.False(Directory.Exists(basePath));
  }

  [WindowsAdministratorFact]
  public void RevocationLedger_MissingLedgerFailsClosed()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-missing-ledger-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    var ownershipToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    var directoryName = Guid.NewGuid().ToString("N");

    try
    {
      WindowsPlanArtifactDirectoryPolicy.ProvisionIdentityNeutralRoot(root);
      File.Delete(Path.Combine(
          root,
          WindowsPlanArtifactDirectoryPolicy.RevocationLedgerFileName));

      Assert.Throws<SecurityException>(() =>
          new WindowsPlanArtifactDirectoryPolicy(root).CreateRestrictedStagingDirectory());
      Assert.Throws<SecurityException>(() =>
          WindowsPlanArtifactDirectoryPolicy.AppendRevocation(
              root,
              ownershipToken,
              directoryName));
      Assert.Throws<SecurityException>(() =>
          WindowsPlanArtifactDirectoryPolicy.ContainsRevocation(
              root,
              ownershipToken,
              directoryName));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void ValidateIdentityNeutralRootSecurity_RejectsUntrustedOwner()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateIdentityNeutralRootSecurity(
        Administrators);
    security.SetOwner(TestSid(1001));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateIdentityNeutralRootSecurity(security));
  }

  [WindowsFact]
  public void ValidateIdentityNeutralRootSecurity_RejectsUsersDeleteRights()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateIdentityNeutralRootSecurity(
        Administrators);
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        FileSystemRights.Delete,
        AccessControlType.Allow));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateIdentityNeutralRootSecurity(security));
  }

  [Theory]
  [InlineData(FileSystemRights.FullControl)]
  [InlineData(FileSystemRights.Delete)]
  [InlineData(FileSystemRights.CreateFiles)]
  [InlineData(FileSystemRights.ChangePermissions)]
  [InlineData(FileSystemRights.TakeOwnership)]
  public void ValidateIdentityNeutralRootSecurity_RejectsInheritedDangerousUsersRights(
      FileSystemRights rights)
  {
    var security = AddInheritedRule(
        WindowsPlanArtifactDirectoryPolicy.CreateIdentityNeutralRootSecurity(Administrators),
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        rights,
        AccessControlType.Allow);

    Assert.True(security.AreAccessRulesProtected);
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateIdentityNeutralRootSecurity(security));
  }

  [Fact]
  public void ValidateIdentityNeutralRootSecurity_RejectsInheritedDenyRule()
  {
    var security = AddInheritedRule(
        WindowsPlanArtifactDirectoryPolicy.CreateIdentityNeutralRootSecurity(Administrators),
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        FileSystemRights.Delete,
        AccessControlType.Deny);

    Assert.True(security.AreAccessRulesProtected);
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateIdentityNeutralRootSecurity(security));
  }

  [WindowsFact]
  public void ValidateProductRootSecurity_DoesNotAllowUsersToReplacePlanArtifactRoot()
  {
    var security = WindowsPlanArtifactDirectoryPolicy.CreateProductRootSecurity(Administrators);

    WindowsPlanArtifactDirectoryPolicy.ValidateProductRootSecurity(security);

    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var usersRule = Assert.Single(security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>(), rule => users.Equals(rule.IdentityReference));
    Assert.Equal(
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        usersRule.FileSystemRights);
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.CreateDirectories));
    Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.DeleteSubdirectoriesAndFiles));
  }

  [Theory]
  [InlineData(FileSystemRights.FullControl)]
  [InlineData(FileSystemRights.Delete)]
  [InlineData(FileSystemRights.CreateFiles)]
  [InlineData(FileSystemRights.ChangePermissions)]
  [InlineData(FileSystemRights.TakeOwnership)]
  public void ValidateProductRootSecurity_RejectsInheritedDangerousUsersRights(
      FileSystemRights rights)
  {
    var security = AddInheritedRule(
        WindowsPlanArtifactDirectoryPolicy.CreateProductRootSecurity(Administrators),
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        rights,
        AccessControlType.Allow);

    Assert.True(security.AreAccessRulesProtected);
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateProductRootSecurity(security));
  }

  [Fact]
  public void ValidateProductRootSecurity_RejectsInheritedUnknownPrincipalRule()
  {
    var security = AddInheritedRule(
        WindowsPlanArtifactDirectoryPolicy.CreateProductRootSecurity(Administrators),
        TestSid(1001),
        FileSystemRights.Read,
        AccessControlType.Allow);

    Assert.True(security.AreAccessRulesProtected);
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateProductRootSecurity(security));
  }

  [WindowsAdministratorFact]
  public void ProvisionIdentityNeutralRoot_CreatesTrustedRootThatCanStageProtectedLeaf()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-provision-root-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(basePath);

    try
    {
      WindowsPlanArtifactDirectoryPolicy.ProvisionIdentityNeutralRoot(root);

      var leaf = new WindowsPlanArtifactDirectoryPolicy(root)
          .CreateRestrictedStagingDirectory();
      var ownershipToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
      var directoryName = Path.GetFileName(leaf);
      WindowsPlanArtifactDirectoryPolicy.AppendRevocation(
          root,
          ownershipToken,
          directoryName);

      Assert.StartsWith(root, leaf, StringComparison.OrdinalIgnoreCase);
      WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory(leaf);
      Assert.True(File.Exists(Path.Combine(
          root,
          WindowsPlanArtifactDirectoryPolicy.RevocationLedgerFileName)));
      Assert.True(WindowsPlanArtifactDirectoryPolicy.ContainsRevocation(
          root,
          ownershipToken,
          directoryName));
    }
    finally
    {
      Directory.Delete(basePath, recursive: true);
    }
  }

  [WindowsAdministratorFact]
  public void CreatePlanArtifactDirectory_RejectsSharedRootReparsePoint()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-shared-link-{Guid.NewGuid():N}");
    var target = Path.Combine(basePath, "target");
    var product = Path.Combine(basePath, "Wdem");
    var link = Path.Combine(product, "PlanArtifacts");
    Directory.CreateDirectory(target);
    Directory.CreateDirectory(product);
    Directory.CreateSymbolicLink(link, target);

    try
    {
      Assert.Throws<SecurityException>(() =>
          new WindowsPlanArtifactDirectoryPolicy(link).CreateRestrictedStagingDirectory());
    }
    finally
    {
      Directory.Delete(link);
      Directory.Delete(basePath, recursive: true);
    }
  }

  [WindowsFact]
  public void ValidatePlanArtifactSecurity_AllowsDifferentAdministratorForRecordedCreator()
  {
    var creator = TestSid(1001);
    var claimant = TestSid(1002);
    var security = WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
        creator,
        Administrators,
        LocalSystem);

    WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
        security,
        creator,
        claimant,
        claimantIsAdministrator: true);
  }

  [WindowsFact]
  public void ValidatePlanArtifactSecurity_AllowsSystemForRecordedCreator()
  {
    var creator = TestSid(1001);
    var security = WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
        creator,
        Administrators,
        LocalSystem);

    WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
        security,
        creator,
        LocalSystem,
        claimantIsAdministrator: false);
  }

  [WindowsFact]
  public void ValidatePlanArtifactSecurity_RejectsUntrustedClaimant()
  {
    var creator = TestSid(1001);
    var security = WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
        creator,
        Administrators,
        LocalSystem);

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
            security,
            creator,
            TestSid(1002),
            claimantIsAdministrator: false));
  }

  [WindowsFact]
  public void ValidatePlanArtifactSecurity_RejectsTamperedOwner()
  {
    var creator = TestSid(1001);
    var security = WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
        creator,
        Administrators,
        LocalSystem);
    security.SetOwner(TestSid(1002));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
            security,
            creator,
            creator,
            claimantIsAdministrator: false));
  }

  [WindowsFact]
  public void ValidatePlanArtifactSecurity_RejectsTamperedAccessRule()
  {
    var creator = TestSid(1001);
    var security = WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
        creator,
        Administrators,
        LocalSystem);
    security.AddAccessRule(new FileSystemAccessRule(
        TestSid(1002),
        FileSystemRights.Read,
        AccessControlType.Allow));

    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
            security,
            creator,
            creator,
            claimantIsAdministrator: false));
  }

  [Theory]
  [InlineData(FileSystemRights.FullControl)]
  [InlineData(FileSystemRights.Delete)]
  [InlineData(FileSystemRights.CreateFiles)]
  [InlineData(FileSystemRights.ChangePermissions)]
  [InlineData(FileSystemRights.TakeOwnership)]
  public void ValidatePlanArtifactSecurity_RejectsInheritedDangerousAccessRule(
      FileSystemRights rights)
  {
    var creator = TestSid(1001);
    var security = AddInheritedRule(
        WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
            creator,
            Administrators,
            LocalSystem),
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        rights,
        AccessControlType.Allow);

    Assert.True(security.AreAccessRulesProtected);
    Assert.Throws<SecurityException>(() =>
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedSecurity(
            security,
            creator,
            creator,
            claimantIsAdministrator: false));
  }

  [WindowsAdministratorFact]
  public void CreateRestrictedStagingDirectory_CreatesProtectedAdministratorSystemAcl()
  {
    var path = new WindowsSecureArtifactDirectoryPolicy().CreateRestrictedStagingDirectory();

    try
    {
      var info = new DirectoryInfo(path);
      var security = info.GetAccessControl(
          AccessControlSections.Access | AccessControlSections.Owner);
      var administrators = new SecurityIdentifier(
          WellKnownSidType.BuiltinAdministratorsSid,
          null);
      var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
      var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
          .Cast<FileSystemAccessRule>()
          .ToArray();

      Assert.True(info.Exists);
      Assert.False(info.Attributes.HasFlag(FileAttributes.ReparsePoint));
      Assert.True(security.AreAccessRulesProtected);
      Assert.Equal(administrators, security.GetOwner(typeof(SecurityIdentifier)));
      Assert.Equal(2, rules.Length);
      Assert.Contains(rules, rule => HasFullControl(rule, administrators));
      Assert.Contains(rules, rule => HasFullControl(rule, system));
    }
    finally
    {
      Directory.Delete(path, recursive: true);
    }
  }

  [WindowsAdministratorFact]
  public void ValidateRestrictedDirectory_RejectsRealDirectoryReparsePoint()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-acl-link-{Guid.NewGuid():N}");
    var target = Path.Combine(root, "target");
    var link = Path.Combine(root, "link");
    Directory.CreateDirectory(target);
    Directory.CreateSymbolicLink(link, target);

    try
    {
      Assert.Throws<SecurityException>(() =>
          WindowsSecureArtifactDirectoryPolicy.ValidateRestrictedDirectory(link));
    }
    finally
    {
      Directory.Delete(link);
      Directory.Delete(root, recursive: true);
    }
  }

  [WindowsAdministratorFact]
  public void OpenValidatedRestrictedDirectory_HoldsHierarchyAgainstReplacement()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-pinned-root-{Guid.NewGuid():N}");
    var productRoot = Path.Combine(basePath, "Wdem");
    var planRoot = Path.Combine(productRoot, "PlanArtifacts");
    WindowsPlanArtifactDirectoryPolicy.ProvisionIdentityNeutralRoot(planRoot);
    var leaf = new WindowsPlanArtifactDirectoryPolicy(planRoot)
        .CreateRestrictedStagingDirectory();

    try
    {
      using var hierarchy = WindowsPlanArtifactDirectoryPolicy.OpenValidatedRestrictedDirectory(
          leaf,
          WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid());

      Assert.Throws<IOException>(() => Directory.Move(
          leaf,
          Path.Combine(planRoot, $"replacement-{Guid.NewGuid():N}")));
      Assert.Throws<IOException>(() => Directory.Move(
          planRoot,
          Path.Combine(productRoot, $"replacement-{Guid.NewGuid():N}")));
      Assert.Throws<IOException>(() => Directory.Move(
          productRoot,
          Path.Combine(basePath, $"replacement-{Guid.NewGuid():N}")));
    }
    finally
    {
      Directory.Delete(basePath, recursive: true);
    }
  }

  [WindowsAdministratorFact]
  public void CreateAdministratorOnlyFile_ProtectsTerminalStateFromCreatorRollback()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-terminal-state-{Guid.NewGuid():N}");
    var planRoot = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    WindowsPlanArtifactDirectoryPolicy.ProvisionIdentityNeutralRoot(planRoot);
    var path = Path.Combine(planRoot, $".{Guid.NewGuid():N}.wdem-vsix-terminal");

    try
    {
      WindowsPlanArtifactDirectoryPolicy.CreateAdministratorOnlyFile(
          path,
          "terminal-state"u8);
      var security = new FileInfo(path).GetAccessControl(
          AccessControlSections.Access | AccessControlSections.Owner);
      var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
          .Cast<FileSystemAccessRule>()
          .ToArray();

      Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
      Assert.True(security.AreAccessRulesProtected);
      Assert.Equal(2, rules.Length);
      Assert.Contains(rules, rule =>
          Administrators.Equals(rule.IdentityReference) &&
          rule.AccessControlType == AccessControlType.Allow &&
          rule.FileSystemRights == FileSystemRights.FullControl);
      Assert.Contains(rules, rule =>
          LocalSystem.Equals(rule.IdentityReference) &&
          rule.AccessControlType == AccessControlType.Allow &&
          rule.FileSystemRights == FileSystemRights.FullControl);
    }
    finally
    {
      Directory.Delete(basePath, recursive: true);
    }
  }

  private static bool HasFullControl(
      FileSystemAccessRule rule,
      SecurityIdentifier identity) => identity.Equals(rule.IdentityReference) &&
      rule.AccessControlType == AccessControlType.Allow &&
      rule.FileSystemRights == FileSystemRights.FullControl &&
      rule.InheritanceFlags ==
          (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
      rule.PropagationFlags == PropagationFlags.None;

  private static DirectorySecurity AddInheritedRule(
      DirectorySecurity security,
      SecurityIdentifier identity,
      FileSystemRights rights,
      AccessControlType accessControlType)
  {
    var descriptor = new RawSecurityDescriptor(
        security.GetSecurityDescriptorBinaryForm(),
        offset: 0);
    descriptor.DiscretionaryAcl!.InsertAce(
        descriptor.DiscretionaryAcl.Count,
        new CommonAce(
            AceFlags.Inherited,
            accessControlType == AccessControlType.Allow
                ? AceQualifier.AccessAllowed
                : AceQualifier.AccessDenied,
            (int)rights,
            identity,
            isCallback: false,
            opaque: null));
    var bytes = new byte[descriptor.BinaryLength];
    descriptor.GetBinaryForm(bytes, offset: 0);
    var result = new DirectorySecurity();
    result.SetSecurityDescriptorBinaryForm(bytes);
    return result;
  }

  private static FileSecurity AddInheritedFileRule(
      FileSecurity security,
      SecurityIdentifier identity,
      FileSystemRights rights,
      AccessControlType accessControlType)
  {
    var descriptor = new RawSecurityDescriptor(
        security.GetSecurityDescriptorBinaryForm(),
        offset: 0);
    descriptor.DiscretionaryAcl!.InsertAce(
        descriptor.DiscretionaryAcl.Count,
        new CommonAce(
            AceFlags.Inherited,
            accessControlType == AccessControlType.Allow
                ? AceQualifier.AccessAllowed
                : AceQualifier.AccessDenied,
            (int)rights,
            identity,
            isCallback: false,
            opaque: null));
    var bytes = new byte[descriptor.BinaryLength];
    descriptor.GetBinaryForm(bytes, offset: 0);
    var result = new FileSecurity();
    result.SetSecurityDescriptorBinaryForm(bytes);
    return result;
  }

  private static SafeFileHandle OpenAppendOnly(string path)
  {
    var handle = NativeMethods.CreateFile(
        path,
        desiredAccess: 0x00000004,
        FileShare.ReadWrite,
        IntPtr.Zero,
        creationDisposition: 3,
        flagsAndAttributes: 0x00000080 | 0x80000000,
        IntPtr.Zero);
    Assert.False(handle.IsInvalid);
    return handle;
  }

  private static SecurityIdentifier Administrators { get; } = new(
      WellKnownSidType.BuiltinAdministratorsSid,
      null);

  private static SecurityIdentifier LocalSystem { get; } = new(
      WellKnownSidType.LocalSystemSid,
      null);

  private static SecurityIdentifier TestSid(int relativeId) => new(
      $"S-1-5-21-111111111-222222222-333333333-{relativeId}");

  private static class NativeMethods
  {
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
  }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
  public WindowsFactAttribute()
  {
    if (!OperatingSystem.IsWindows())
    {
      Skip = "Requires Windows.";
    }
  }
}

internal sealed class WindowsAdministratorFactAttribute : FactAttribute
{
  public WindowsAdministratorFactAttribute()
  {
    if (!OperatingSystem.IsWindows())
    {
      Skip = "Requires Windows.";
      return;
    }

    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
      Skip = "Requires an elevated Windows process.";
    }
  }
}
