using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Wdem.Windows.Security;

internal sealed class WindowsPlanArtifactDirectoryPolicy : ISecureArtifactDirectoryPolicy
{
  private const int ErrorAlreadyExists = 183;

  public string CreateRestrictedStagingDirectory()
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException(
          "Restricted plan-artifact staging requires Windows access controls.");
    }

    using var identity = WindowsIdentity.GetCurrent();
    var currentUser = identity.User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
    var security = CreateSecurity(currentUser, administrators, system);
    var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localData))
    {
      throw new InvalidOperationException("The current user's application-data path is unavailable.");
    }

    var rootPath = Path.Combine(localData, "Wdem", "PlanArtifacts");
    CreateRestrictedDirectory(
        rootPath,
        security,
        currentUser,
        mustCreate: false);
    var stagingPath = Path.Combine(rootPath, Guid.NewGuid().ToString("N"));
    CreateRestrictedDirectory(
        stagingPath,
        security,
        currentUser,
        mustCreate: true);
    return stagingPath;
  }

  internal static void ValidateRestrictedDirectory(string path)
  {
    using var identity = WindowsIdentity.GetCurrent();
    var currentUser = identity.User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    ValidateRestrictedDirectory(
        path,
        currentUser,
        currentUser,
        new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
  }

  internal static string GetCurrentUserSid()
  {
    using var identity = WindowsIdentity.GetCurrent();
    return identity.User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
  }

  internal static void ValidateRestrictedDirectory(string path, string creatorSid)
  {
    var creator = new SecurityIdentifier(creatorSid);
    using var identity = WindowsIdentity.GetCurrent();
    var claimant = identity.User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    ValidateRestrictedDirectory(
        path,
        creator,
        claimant,
        new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
  }

  internal static DirectorySecurity CreateSecurity(
      SecurityIdentifier currentUser,
      SecurityIdentifier administrators,
      SecurityIdentifier system)
  {
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(currentUser);
    foreach (var identity in new[] { currentUser, administrators, system })
    {
      security.AddAccessRule(new FileSystemAccessRule(
          identity,
          FileSystemRights.FullControl,
          inheritance,
          PropagationFlags.None,
          AccessControlType.Allow));
    }

    return security;
  }

  private static void CreateRestrictedDirectory(
      string path,
      DirectorySecurity security,
      SecurityIdentifier currentUser,
      bool mustCreate)
  {
    var parent = Path.GetDirectoryName(path);
    if (parent is not null)
    {
      Directory.CreateDirectory(parent);
    }

    var descriptor = security.GetSecurityDescriptorBinaryForm();
    var descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
    try
    {
      var attributes = new SecurityAttributes
      {
        Length = Marshal.SizeOf<SecurityAttributes>(),
        SecurityDescriptor = descriptorHandle.AddrOfPinnedObject()
      };
      if (!NativeMethods.CreateDirectory(path, ref attributes))
      {
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorAlreadyExists || mustCreate)
        {
          throw new Win32Exception(error, $"Could not create restricted directory '{path}'.");
        }
      }
    }
    finally
    {
      descriptorHandle.Free();
    }

    ValidateRestrictedDirectory(
        path,
        currentUser,
        currentUser,
        claimantIsAdministrator: false);
  }

  private static void ValidateRestrictedDirectory(
      string path,
      SecurityIdentifier creator,
      SecurityIdentifier claimant,
      bool claimantIsAdministrator)
  {
    var info = new DirectoryInfo(path);
    if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new SecurityException(
          $"The restricted plan-artifact directory '{path}' is unavailable or redirected.");
    }

    var security = info.GetAccessControl(
        AccessControlSections.Access | AccessControlSections.Owner);
    ValidateRestrictedSecurity(security, creator, claimant, claimantIsAdministrator);
  }

  internal static void ValidateRestrictedSecurity(
      DirectorySecurity security,
      SecurityIdentifier creator,
      SecurityIdentifier claimant,
      bool claimantIsAdministrator)
  {
    ArgumentNullException.ThrowIfNull(security);
    ArgumentNullException.ThrowIfNull(creator);
    ArgumentNullException.ThrowIfNull(claimant);
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    if (!creator.Equals(claimant) && !system.Equals(claimant) && !claimantIsAdministrator)
    {
      throw new SecurityException("The current identity cannot claim this plan artifact.");
    }

    if (!creator.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
        !security.AreAccessRulesProtected)
    {
      throw new SecurityException("The restricted plan-artifact directory has untrusted ownership.");
    }

    var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    if (rules.Length != 3 ||
        !HasRequiredRule(rules, creator) ||
        !HasRequiredRule(rules, administrators) ||
        !HasRequiredRule(rules, system))
    {
      throw new SecurityException("The restricted plan-artifact directory grants unexpected access.");
    }
  }

  private static bool HasRequiredRule(
      IReadOnlyList<FileSystemAccessRule> rules,
      SecurityIdentifier identity) => rules.Any(rule =>
          identity.Equals(rule.IdentityReference) &&
          rule.AccessControlType == AccessControlType.Allow &&
          rule.FileSystemRights == FileSystemRights.FullControl &&
          rule.InheritanceFlags ==
              (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
          rule.PropagationFlags == PropagationFlags.None);

  [StructLayout(LayoutKind.Sequential)]
  private struct SecurityAttributes
  {
    public int Length;
    public IntPtr SecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)]
    public bool InheritHandle;
  }

  private static class NativeMethods
  {
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateDirectory(
        string path,
        ref SecurityAttributes securityAttributes);
  }
}
