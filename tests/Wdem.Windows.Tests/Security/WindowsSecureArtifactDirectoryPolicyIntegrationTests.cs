using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class WindowsSecureArtifactDirectoryPolicyIntegrationTests
{
  [WindowsFact]
  public void CreatePlanArtifactDirectory_AllowsCurrentUserAndTrustedElevatedIdentities()
  {
    var path = new WindowsPlanArtifactDirectoryPolicy().CreateRestrictedStagingDirectory();

    try
    {
      WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory(path);
      File.WriteAllText(Path.Combine(path, "probe"), "writable");
      Assert.True(File.Exists(Path.Combine(path, "probe")));
    }
    finally
    {
      Directory.Delete(path, recursive: true);
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

  private static bool HasFullControl(
      FileSystemAccessRule rule,
      SecurityIdentifier identity) => identity.Equals(rule.IdentityReference) &&
      rule.AccessControlType == AccessControlType.Allow &&
      rule.FileSystemRights == FileSystemRights.FullControl &&
      rule.InheritanceFlags ==
          (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
      rule.PropagationFlags == PropagationFlags.None;

  private static SecurityIdentifier Administrators { get; } = new(
      WellKnownSidType.BuiltinAdministratorsSid,
      null);

  private static SecurityIdentifier LocalSystem { get; } = new(
      WellKnownSidType.LocalSystemSid,
      null);

  private static SecurityIdentifier TestSid(int relativeId) => new(
      $"S-1-5-21-111111111-222222222-333333333-{relativeId}");
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
