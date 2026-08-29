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
  private const uint GenericRead = 0x80000000;
  private const uint GenericWrite = 0x40000000;
  private const uint Synchronize = 0x00100000;
  private const uint CreateNew = 1;
  private const uint OpenExisting = 3;
  private const uint FileAttributeNormal = 0x00000080;
  private const uint FileFlagWriteThrough = 0x80000000;
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const uint LockFileExclusiveLock = 0x00000002;
  internal const string RevocationLedgerFileName = ".wdem-vsix-revocations";
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
    ProvisionRevocationLedger(fullRootPath);
  }

  internal static void AppendRevocation(
      string rootPath,
      string ownershipToken,
      string directoryName)
  {
    var record = VsixPlanArtifactLedger.CreateRevokedRecord(ownershipToken, directoryName);
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericRead | GenericWrite | ReadControl | Synchronize);
    LockLedger(ledgerHandle);
    try
    {
      if (ReadLedgerTerminalStatus(ledgerHandle, ownershipToken, directoryName) is null)
      {
        WriteRevocationRecordCore(ledgerHandle, record);
      }
    }
    finally
    {
      UnlockLedger(ledgerHandle);
    }
  }

  internal static void AppendIssuance(
      string rootPath,
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds)
  {
    var record = VsixPlanArtifactLedger.CreateIssuedRecord(
        ownershipToken,
        directoryName,
        expiresAtUtc,
        activationCommitment,
        bootIdentifier,
        expiresAtUptimeMilliseconds);
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericWrite | ReadControl | Synchronize);
    WriteRevocationRecord(ledgerHandle, record);
  }

  internal static void AppendActivation(
      string rootPath,
      string ownershipToken,
      string directoryName) =>
      AppendLedgerRecord(
          rootPath,
          VsixPlanArtifactLedger.CreateActivatedRecord(ownershipToken, directoryName));

  internal static void AppendClaimStarted(
      string rootPath,
      string ownershipToken,
      string directoryName,
      string claimNonce) =>
      AppendLedgerRecord(
          rootPath,
          VsixPlanArtifactLedger.CreateClaimStartedRecord(
              ownershipToken,
              directoryName,
              claimNonce));

  internal static void ConsumeClaim(
      string rootPath,
      string ownershipToken,
      string directoryName,
      string claimNonce,
      string activationCommitment,
      DateTimeOffset utcNow,
      Guid bootIdentifier,
      long uptimeMilliseconds)
  {
    var record = VsixPlanArtifactLedger.CreateConsumedRecord(ownershipToken, directoryName);
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericRead | GenericWrite | ReadControl | Synchronize);
    LockLedger(ledgerHandle);
    try
    {
      var state = ReadLedgerState(ledgerHandle, ownershipToken, directoryName);
      if (!VsixPlanArtifactLedger.IsAuthorizedClaimForConsumption(
              state,
              claimNonce,
              activationCommitment,
              utcNow,
              bootIdentifier,
              uptimeMilliseconds))
      {
        throw new SecurityException(
            "The durable VSIX claim is no longer authorized for consumption.");
      }

      WriteRevocationRecordCore(ledgerHandle, record);
    }
    finally
    {
      UnlockLedger(ledgerHandle);
    }
  }

  internal static bool ContainsRevocation(
      string rootPath,
      string ownershipToken,
      string directoryName)
  {
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericRead | ReadControl);
    using var stream = new FileStream(ledgerHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    return VsixPlanArtifactLedger.ReadFirstTerminalStatus(
        stream,
        ownershipToken,
        directoryName) == VsixPlanArtifactLedgerStatus.Revoked;
  }

  internal static DateTimeOffset GetIssuedExpiry(
      string rootPath,
      string ownershipToken,
      string directoryName)
  {
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericRead | ReadControl);
    using var stream = new FileStream(ledgerHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    return VsixPlanArtifactLedger.GetIssuedExpiry(stream, ownershipToken, directoryName);
  }

  internal static VsixPlanArtifactLedgerState GetLedgerState(
      string rootPath,
      string ownershipToken,
      string directoryName)
  {
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericRead | ReadControl);
    using var stream = new FileStream(ledgerHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    return VsixPlanArtifactLedger.ReadState(stream, ownershipToken, directoryName);
  }

  internal static void WriteRevocationRecord(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName) =>
      WriteRevocationRecord(
          ledgerHandle,
          VsixPlanArtifactLedger.CreateRevokedRecord(ownershipToken, directoryName));

  internal static void WriteIssuanceRecord(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds) =>
      WriteRevocationRecord(
          ledgerHandle,
          VsixPlanArtifactLedger.CreateIssuedRecord(
              ownershipToken,
              directoryName,
              expiresAtUtc,
              activationCommitment,
              bootIdentifier,
              expiresAtUptimeMilliseconds));

  internal static void WriteActivationRecord(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName) =>
      WriteRevocationRecord(
          ledgerHandle,
          VsixPlanArtifactLedger.CreateActivatedRecord(ownershipToken, directoryName));

  internal static void WriteClaimStartedRecord(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName,
      string claimNonce) =>
      WriteRevocationRecord(
          ledgerHandle,
          VsixPlanArtifactLedger.CreateClaimStartedRecord(
              ownershipToken,
              directoryName,
              claimNonce));

  internal static void WriteConsumedRecord(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName) =>
      WriteRevocationRecord(
          ledgerHandle,
          VsixPlanArtifactLedger.CreateConsumedRecord(ownershipToken, directoryName));

  internal static bool ContainsRevocationRecord(
      ReadOnlySpan<byte> contents,
      string ownershipToken,
      string directoryName) =>
      VsixPlanArtifactLedger.ContainsRevokedRecord(
          contents,
          ownershipToken,
          directoryName);

  private static void WriteRevocationRecord(
      SafeFileHandle ledgerHandle,
      byte[] record)
  {
    ArgumentNullException.ThrowIfNull(ledgerHandle);
    if (ledgerHandle.IsInvalid || ledgerHandle.IsClosed)
    {
      throw new ArgumentException("The VSIX revocation ledger handle is invalid.", nameof(ledgerHandle));
    }

    LockLedger(ledgerHandle);
    try
    {
      WriteRevocationRecordCore(ledgerHandle, record);
    }
    finally
    {
      UnlockLedger(ledgerHandle);
    }
  }

  private static void WriteRevocationRecordCore(
      SafeFileHandle ledgerHandle,
      byte[] record)
  {
    if (!NativeMethods.SetFilePointerEx(
            ledgerHandle,
            distanceToMove: 0,
            out _,
            moveMethod: SeekOrigin.End))
    {
      throw new IOException(
          "The VSIX revocation ledger could not be positioned for append.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }

    if (!NativeMethods.WriteFile(
            ledgerHandle,
            record,
            record.Length,
            out var bytesWritten,
            IntPtr.Zero) ||
        bytesWritten != record.Length)
    {
      throw new IOException(
          "The VSIX revocation record could not be appended atomically.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }

    if (!NativeMethods.FlushFileBuffers(ledgerHandle))
    {
      throw new IOException(
          "The VSIX revocation record could not be committed to durable storage.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }
  }

  private static VsixPlanArtifactLedgerState ReadLedgerState(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName)
  {
    using var borrowedHandle = new SafeFileHandle(
        ledgerHandle.DangerousGetHandle(),
        ownsHandle: false);
    using var stream = new FileStream(
        borrowedHandle,
        FileAccess.Read,
        bufferSize: 4096,
        isAsync: false);
    return VsixPlanArtifactLedger.ReadState(stream, ownershipToken, directoryName);
  }

  private static VsixPlanArtifactLedgerStatus? ReadLedgerTerminalStatus(
      SafeFileHandle ledgerHandle,
      string ownershipToken,
      string directoryName)
  {
    using var borrowedHandle = new SafeFileHandle(
        ledgerHandle.DangerousGetHandle(),
        ownsHandle: false);
    using var stream = new FileStream(
        borrowedHandle,
        FileAccess.Read,
        bufferSize: 4096,
        isAsync: false);
    return VsixPlanArtifactLedger.ReadFirstTerminalStatus(
        stream,
        ownershipToken,
        directoryName);
  }

  private static void LockLedger(SafeFileHandle ledgerHandle)
  {
    var overlapped = default(FileLockOverlapped);
    if (!NativeMethods.LockFileEx(
            ledgerHandle,
            LockFileExclusiveLock,
            reserved: 0,
            uint.MaxValue,
            uint.MaxValue,
            ref overlapped))
    {
      throw new IOException(
          "The VSIX revocation ledger could not be exclusively locked.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }
  }

  private static void UnlockLedger(SafeFileHandle ledgerHandle)
  {
    var overlapped = default(FileLockOverlapped);
    if (!NativeMethods.UnlockFileEx(
            ledgerHandle,
            reserved: 0,
            uint.MaxValue,
            uint.MaxValue,
            ref overlapped))
    {
      throw new IOException(
          "The VSIX revocation ledger could not be unlocked.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }
  }

  private static void AppendLedgerRecord(string rootPath, byte[] record)
  {
    var fullRootPath = ValidateRevocationRootPath(rootPath);
    var productPath = Path.GetDirectoryName(fullRootPath)!;
    using var productHandle = OpenValidatedProductRoot(productPath);
    using var rootHandle = OpenValidatedIdentityNeutralRoot(fullRootPath);
    using var ledgerHandle = OpenValidatedRevocationLedger(
        fullRootPath,
        GenericWrite | ReadControl | Synchronize);
    WriteRevocationRecord(ledgerHandle, record);
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

  internal static IDisposable OpenValidatedRestrictedDirectory(
      string path,
      string creatorSid)
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException(
          "Restricted plan-artifact validation requires Windows access controls.");
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var fullPath = Path.GetFullPath(path);
    var planRoot = Path.GetDirectoryName(fullPath) ??
        throw new SecurityException("The restricted plan-artifact directory is invalid.");
    var productRoot = Path.GetDirectoryName(planRoot) ??
        throw new SecurityException("The shared plan-artifact root is invalid.");
    if (!string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _) ||
        !string.Equals(Path.GetFileName(planRoot), "PlanArtifacts", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetFileName(productRoot), "Wdem", StringComparison.OrdinalIgnoreCase))
    {
      throw new SecurityException("The restricted plan-artifact directory is outside the bounded root.");
    }

    var creator = new SecurityIdentifier(creatorSid);
    using var identity = WindowsIdentity.GetCurrent();
    var claimant = identity.User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var claimantIsAdministrator = new WindowsPrincipal(identity)
        .IsInRole(WindowsBuiltInRole.Administrator);
    SafeFileHandle? productHandle = null;
    SafeFileHandle? rootHandle = null;
    SafeFileHandle? leafHandle = null;
    try
    {
      productHandle = OpenValidatedProductRoot(productRoot);
      rootHandle = OpenValidatedIdentityNeutralRoot(planRoot);
      leafHandle = OpenValidatedDirectory(
          fullPath,
          security => ValidateRestrictedSecurity(
              security,
              creator,
              claimant,
              claimantIsAdministrator),
          "restricted plan-artifact directory");
      var result = new ValidatedDirectoryHierarchy(productHandle, rootHandle, leafHandle);
      productHandle = null;
      rootHandle = null;
      leafHandle = null;
      return result;
    }
    finally
    {
      leafHandle?.Dispose();
      rootHandle?.Dispose();
      productHandle?.Dispose();
    }
  }

  internal static void CreateAdministratorOnlyFile(
      string path,
      ReadOnlySpan<byte> contents)
  {
    using var source = new MemoryStream(contents.ToArray(), writable: false);
    CreateAdministratorOnlyCopy(path, source);
  }

  internal static void CreateOrValidateAdministratorOnlyFile(
      string path,
      ReadOnlySpan<byte> contents)
  {
    try
    {
      CreateAdministratorOnlyFile(path, contents);
    }
    catch (Win32Exception)
    {
      ValidateAdministratorOnlyFile(path, contents);
    }
  }

  internal static void ValidateAdministratorOnlyFile(
      string path,
      ReadOnlySpan<byte> expectedContents)
  {
    var handle = NativeMethods.CreateFile(
        Path.GetFullPath(path),
        GenericRead | ReadControl,
        FileShare.Read,
        IntPtr.Zero,
        OpenExisting,
        FileAttributeNormal | FileFlagOpenReparsePoint,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = Marshal.GetLastWin32Error();
      handle.Dispose();
      throw new SecurityException(
          "The protected plan-artifact state could not be securely opened.",
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
            "The protected plan-artifact state attributes could not be read.");
      }

      if (attributes.FileAttributes.HasFlag(FileAttributes.Directory) ||
          attributes.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new SecurityException("The protected plan-artifact state is redirected.");
      }

      ValidateAdministratorOnlyFileSecurity(ReadSecurity(handle));
      using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
      handle = null!;
      var actual = new byte[expectedContents.Length + 1];
      var length = 0;
      while (length < actual.Length)
      {
        var read = stream.Read(actual, length, actual.Length - length);
        if (read == 0)
        {
          break;
        }

        length += read;
      }

      if (length != expectedContents.Length ||
          !actual.AsSpan(0, length).SequenceEqual(expectedContents))
      {
        throw new SecurityException("The protected plan-artifact state content is invalid.");
      }
    }
    finally
    {
      handle?.Dispose();
    }
  }

  internal static void CreateAdministratorOnlyCopy(
      string path,
      Stream source)
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException(
          "Protected plan-artifact state requires Windows access controls.");
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(source);
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    foreach (var identity in new[] { administrators, system })
    {
      security.AddAccessRule(new FileSystemAccessRule(
          identity,
          FileSystemRights.FullControl,
          AccessControlType.Allow));
    }

    var descriptor = security.GetSecurityDescriptorBinaryForm();
    var descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
    SafeFileHandle? handle = null;
    try
    {
      var attributes = new SecurityAttributes
      {
        Length = Marshal.SizeOf<SecurityAttributes>(),
        SecurityDescriptor = descriptorHandle.AddrOfPinnedObject()
      };
      handle = NativeMethods.CreateFileWithSecurity(
          Path.GetFullPath(path),
          GenericWrite | ReadControl,
          FileShare.Read,
          ref attributes,
          CreateNew,
          FileAttributeNormal | FileFlagWriteThrough,
          IntPtr.Zero);
      if (handle.IsInvalid)
      {
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        handle = null;
        throw new Win32Exception(error, "The protected plan-artifact state could not be created.");
      }

      using (var stream = new FileStream(handle, FileAccess.Write, bufferSize: 1, isAsync: false))
      {
        handle = null;
        source.CopyTo(stream);
        stream.Flush(flushToDisk: true);
        ValidateAdministratorOnlyFileSecurity(ReadSecurity(stream.SafeFileHandle));
      }
    }
    finally
    {
      handle?.Dispose();
      descriptorHandle.Free();
    }
  }

  internal static FileSecurity CreateRevocationLedgerSecurity()
  {
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    foreach (var identity in new[] { administrators, system })
    {
      security.AddAccessRule(new FileSystemAccessRule(
          identity,
          FileSystemRights.FullControl,
          AccessControlType.Allow));
    }

    return security;
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

    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
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

  private static void ValidateAdministratorOnlyFileSecurity(DirectorySecurity security)
  {
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    if (!administrators.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
        !security.AreAccessRulesProtected ||
        rules.Length != 2 ||
        !HasAdministratorOnlyFileRule(rules, administrators) ||
        !HasAdministratorOnlyFileRule(rules, system))
    {
      throw new SecurityException("The protected plan-artifact state grants unexpected access.");
    }
  }

  internal static void ValidateRevocationLedgerSecurity(FileSystemSecurity security)
  {
    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    if (!administrators.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
        !security.AreAccessRulesProtected ||
        rules.Length != 2 ||
        !HasAdministratorOnlyFileRule(rules, administrators) ||
        !HasAdministratorOnlyFileRule(rules, system))
    {
      throw new SecurityException("The VSIX revocation ledger grants unexpected access.");
    }
  }

  private static void ProvisionRevocationLedger(string rootPath)
  {
    var path = Path.Combine(rootPath, RevocationLedgerFileName);
    var security = CreateRevocationLedgerSecurity();
    var descriptor = security.GetSecurityDescriptorBinaryForm();
    var descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
    SafeFileHandle? handle = null;
    try
    {
      var attributes = new SecurityAttributes
      {
        Length = Marshal.SizeOf<SecurityAttributes>(),
        SecurityDescriptor = descriptorHandle.AddrOfPinnedObject()
      };
      handle = NativeMethods.CreateFileWithSecurity(
          path,
          GenericWrite | ReadControl,
          FileShare.Read | FileShare.Write,
          ref attributes,
          CreateNew,
          FileAttributeNormal | FileFlagWriteThrough,
          IntPtr.Zero);
      if (handle.IsInvalid)
      {
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        handle = null;
        if (error != ErrorAlreadyExists)
        {
          throw new Win32Exception(error, "The VSIX revocation ledger could not be provisioned.");
        }

        using var existing = OpenValidatedRevocationLedger(
            rootPath,
            GenericRead | ReadControl);
        return;
      }

      ValidateRevocationLedgerSecurity(ReadSecurity(handle));
    }
    finally
    {
      handle?.Dispose();
      descriptorHandle.Free();
    }
  }

  private static SafeFileHandle OpenValidatedRevocationLedger(
      string rootPath,
      uint desiredAccess)
  {
    var path = Path.Combine(rootPath, RevocationLedgerFileName);
    var handle = NativeMethods.CreateFile(
        path,
        desiredAccess,
        FileShare.Read | FileShare.Write,
        IntPtr.Zero,
        OpenExisting,
        FileAttributeNormal | FileFlagOpenReparsePoint,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = Marshal.GetLastWin32Error();
      handle.Dispose();
      throw new SecurityException(
          "The VSIX revocation ledger is missing or inaccessible; run elevated provisioning.",
          new Win32Exception(error));
    }

    try
    {
      var attributes = File.GetAttributes(handle);
      if (attributes.HasFlag(FileAttributes.Directory) ||
          attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new SecurityException("The VSIX revocation ledger is redirected.");
      }

      ValidateRevocationLedgerSecurity(ReadSecurity(handle));
      return handle;
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static string ValidateRevocationRootPath(string rootPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
    var fullRootPath = Path.GetFullPath(rootPath);
    if (!string.Equals(fullRootPath, rootPath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetFileName(fullRootPath), "PlanArtifacts", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            Path.GetFileName(Path.GetDirectoryName(fullRootPath)),
            "Wdem",
            StringComparison.OrdinalIgnoreCase))
    {
      throw new SecurityException("The VSIX revocation ledger root is invalid.");
    }

    return fullRootPath;
  }

  private static bool HasAdministratorOnlyFileRule(
      IReadOnlyList<FileSystemAccessRule> rules,
      SecurityIdentifier identity) => rules.Any(rule =>
          identity.Equals(rule.IdentityReference) &&
          rule.AccessControlType == AccessControlType.Allow &&
          rule.FileSystemRights == FileSystemRights.FullControl &&
          rule.InheritanceFlags == InheritanceFlags.None &&
          rule.PropagationFlags == PropagationFlags.None);

  private sealed class ValidatedDirectoryHierarchy(
      SafeFileHandle productRoot,
      SafeFileHandle planRoot,
      SafeFileHandle leaf) : IDisposable
  {
    private SafeFileHandle? _productRoot = productRoot;
    private SafeFileHandle? _planRoot = planRoot;
    private SafeFileHandle? _leaf = leaf;

    public void Dispose()
    {
      Interlocked.Exchange(ref _leaf, null)?.Dispose();
      Interlocked.Exchange(ref _planRoot, null)?.Dispose();
      Interlocked.Exchange(ref _productRoot, null)?.Dispose();
    }
  }

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

  [StructLayout(LayoutKind.Sequential)]
  private struct FileLockOverlapped
  {
    public IntPtr Internal;
    public IntPtr InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public IntPtr EventHandle;
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern SafeFileHandle CreateFileWithSecurity(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        ref SecurityAttributes securityAttributes,
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        int numberOfBytesToWrite,
        out int numberOfBytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle file,
        long distanceToMove,
        out long newFilePointer,
        SeekOrigin moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockFileEx(
        SafeFileHandle file,
        uint flags,
        uint reserved,
        uint numberOfBytesToLockLow,
        uint numberOfBytesToLockHigh,
        ref FileLockOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnlockFileEx(
        SafeFileHandle file,
        uint reserved,
        uint numberOfBytesToUnlockLow,
        uint numberOfBytesToUnlockHigh,
        ref FileLockOverlapped overlapped);

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
