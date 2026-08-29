using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Wdem.Windows.Security;

internal sealed class WindowsPlanArtifactDirectoryPolicy : ISecureArtifactDirectoryPolicy
{
  private const int ErrorAlreadyExists = 183;
  private const uint ErrorSuccess = 0;
  private const uint ReadControl = 0x00020000;
  private const uint OpenExisting = 3;
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private readonly string _rootPath;

  public WindowsPlanArtifactDirectoryPolicy()
      : this(GetIdentityNeutralPlanArtifactRoot())
  {
  }

  internal WindowsPlanArtifactDirectoryPolicy(string rootPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
    if (!Path.IsPathFullyQualified(rootPath))
    {
      throw new ArgumentException("The plan-artifact root must be fully qualified.", nameof(rootPath));
    }

    _rootPath = Path.GetFullPath(rootPath);
  }

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
    var productPath = Path.GetDirectoryName(_rootPath) ??
        throw new SecurityException("The shared plan-artifact product root is invalid.");
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(_rootPath);
    var stagingPath = Path.Combine(_rootPath, Guid.NewGuid().ToString("N"));
    CreateRestrictedDirectory(
        stagingPath,
        security,
        currentUser,
        mustCreate: true);
    return stagingPath;
  }

  internal static string GetIdentityNeutralPlanArtifactRoot()
  {
    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    if (string.IsNullOrWhiteSpace(commonData))
    {
      throw new InvalidOperationException("The shared Windows application-data path is unavailable.");
    }

    return Path.Combine(commonData, "Wdem", "PlanArtifacts");
  }

  internal static void ProvisionIdentityNeutralRoot(string rootPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException(
          "Shared plan-artifact root provisioning requires Windows access controls.");
    }

    if (!Path.IsPathFullyQualified(rootPath))
    {
      throw new ArgumentException("The plan-artifact root must be fully qualified.", nameof(rootPath));
    }

    using var identity = WindowsIdentity.GetCurrent();
    var currentUser = identity.User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    if (!system.Equals(currentUser) &&
        !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
      throw new SecurityException(
          "The shared plan-artifact root must be provisioned by an elevated installer.");
    }

    var fullRootPath = Path.GetFullPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath) ??
        throw new ArgumentException("The plan-artifact product root is invalid.", nameof(rootPath));
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    CreateDirectoryWithSecurity(
        productPath,
        CreateProductRootSecurity(administrators),
        allowExisting: true);
    using var productHandle = OpenValidatedProductRoot(productPath);
    CreateDirectoryWithSecurity(
        fullRootPath,
        CreateIdentityNeutralRootSecurity(administrators),
        allowExisting: true);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
  }

  private static SafeFileHandle OpenValidatedIdentityNeutralRoot(string rootPath)
      => OpenValidatedDirectory(
          rootPath,
          ValidateIdentityNeutralRootSecurity,
          "shared plan-artifact root");

  private static SafeFileHandle OpenValidatedProductRoot(string productPath)
      => OpenValidatedDirectory(
          productPath,
          ValidateProductRootSecurity,
          "shared plan-artifact product root");

  private static SafeFileHandle OpenValidatedDirectory(
      string path,
      Action<DirectorySecurity> validateSecurity,
      string description)
  {
    var handle = NativeMethods.CreateFile(
        path,
        ReadControl,
        FileShare.Read | FileShare.Write,
        IntPtr.Zero,
        OpenExisting,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = Marshal.GetLastWin32Error();
      handle.Dispose();
      throw new SecurityException(
          $"The {description} is missing or cannot be securely opened; run elevated provisioning.",
          new Win32Exception(error));
    }

    try
    {
      if (!NativeMethods.GetFileInformationByHandleEx(
              handle,
              FileInfoByHandleClass.FileAttributeTagInfo,
              out var attributes,
              (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
      {
        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"The {description} attributes could not be read.");
      }

      if (!attributes.FileAttributes.HasFlag(FileAttributes.Directory) ||
          attributes.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new SecurityException($"The {description} is redirected.");
      }

      validateSecurity(ReadSecurity(handle));
      return handle;
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static DirectorySecurity ReadSecurity(SafeFileHandle handle)
  {
    var error = NativeMethods.GetSecurityInfo(
        handle,
        SeObjectType.FileObject,
        SecurityInfos.Owner | SecurityInfos.DiscretionaryAcl,
        out _,
        out _,
        out _,
        out _,
        out var descriptor);
    if (error != ErrorSuccess)
    {
      throw new Win32Exception((int)error, "The shared plan-artifact root ACL could not be read.");
    }

    try
    {
      var length = NativeMethods.GetSecurityDescriptorLength(descriptor);
      if (length == 0 || length > int.MaxValue)
      {
        throw new SecurityException("The shared plan-artifact root ACL is invalid.");
      }

      var bytes = new byte[(int)length];
      Marshal.Copy(descriptor, bytes, 0, bytes.Length);
      var security = new DirectorySecurity();
      security.SetSecurityDescriptorBinaryForm(bytes);
      return security;
    }
    finally
    {
      _ = NativeMethods.LocalFree(descriptor);
    }
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

  internal static DirectorySecurity CreateIdentityNeutralRootSecurity(
      SecurityIdentifier owner)
  {
    ArgumentNullException.ThrowIfNull(owner);
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(owner);
    foreach (var identity in new[] { administrators, system })
    {
      security.AddAccessRule(new FileSystemAccessRule(
          identity,
          FileSystemRights.FullControl,
          inheritance,
          PropagationFlags.None,
          AccessControlType.Allow));
    }

    security.AddAccessRule(new FileSystemAccessRule(
        users,
        FileSystemRights.ReadAndExecute |
            FileSystemRights.CreateDirectories |
            FileSystemRights.Synchronize,
        InheritanceFlags.None,
        PropagationFlags.None,
        AccessControlType.Allow));
    return security;
  }

  internal static DirectorySecurity CreateProductRootSecurity(SecurityIdentifier owner)
  {
    var security = CreateIdentityNeutralRootSecurity(owner);
    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    security.RemoveAccessRuleAll(new FileSystemAccessRule(
        users,
        FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
        AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(
        users,
        FileSystemRights.ReadAndExecute,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow));
    return security;
  }

  internal static void ValidateIdentityNeutralRootSecurity(DirectorySecurity security)
  {
    ArgumentNullException.ThrowIfNull(security);
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var trustedInstaller = new SecurityIdentifier(
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
        throw new SecurityException("The shared plan-artifact root owner is unavailable.");
    if ((!owner.Equals(administrators) &&
         !owner.Equals(system) &&
         !owner.Equals(trustedInstaller)) ||
        !security.AreAccessRulesProtected)
    {
      throw new SecurityException("The shared plan-artifact root has untrusted ownership.");
    }

    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    var usersRights = FileSystemRights.ReadAndExecute |
        FileSystemRights.CreateDirectories |
        FileSystemRights.Synchronize;
    if (rules.Length != 3 ||
        !HasRequiredRule(rules, administrators) ||
        !HasRequiredRule(rules, system) ||
        !rules.Any(rule =>
            users.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights == usersRights &&
            rule.InheritanceFlags == InheritanceFlags.None &&
            rule.PropagationFlags == PropagationFlags.None))
    {
      throw new SecurityException("The shared plan-artifact root grants unexpected access.");
    }
  }

  internal static void ValidateProductRootSecurity(DirectorySecurity security)
  {
    ArgumentNullException.ThrowIfNull(security);
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var trustedInstaller = new SecurityIdentifier(
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
        throw new SecurityException("The shared plan-artifact product root owner is unavailable.");
    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    if ((!owner.Equals(administrators) &&
         !owner.Equals(system) &&
         !owner.Equals(trustedInstaller)) ||
        !security.AreAccessRulesProtected ||
        rules.Length != 3 ||
        !HasRequiredRule(rules, administrators) ||
        !HasRequiredRule(rules, system) ||
        !rules.Any(rule =>
            users.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights ==
                (FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize) &&
            rule.InheritanceFlags ==
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
            rule.PropagationFlags == PropagationFlags.None))
    {
      throw new SecurityException("The shared plan-artifact product root is not securely provisioned.");
    }
  }

  private static void CreateRestrictedDirectory(
      string path,
      DirectorySecurity security,
      SecurityIdentifier currentUser,
      bool mustCreate)
  {
    CreateDirectoryWithSecurity(path, security, allowExisting: !mustCreate);
    ValidateRestrictedDirectory(
        path,
        currentUser,
        currentUser,
        claimantIsAdministrator: false);
  }

  private static void CreateDirectoryWithSecurity(
      string path,
      DirectorySecurity security,
      bool allowExisting)
  {
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
        if (error != ErrorAlreadyExists || !allowExisting)
        {
          throw new Win32Exception(error, $"Could not create restricted directory '{path}'.");
        }
      }
    }
    finally
    {
      descriptorHandle.Free();
    }
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

  private enum FileInfoByHandleClass
  {
    FileAttributeTagInfo = 9
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileAttributeTagInfo
  {
    public FileAttributes FileAttributes;
    public uint ReparseTag;
  }

  private enum SeObjectType
  {
    FileObject = 1
  }

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

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateDirectory(
        string path,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("advapi32.dll")]
    public static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        SeObjectType objectType,
        SecurityInfos securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    public static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);
  }
}

public static class WindowsPlanArtifactRootProvisioner
{
  public static void Provision() =>
      WindowsPlanArtifactDirectoryPolicy.ProvisionIdentityNeutralRoot(
          WindowsPlanArtifactDirectoryPolicy.GetIdentityNeutralPlanArtifactRoot());
}
