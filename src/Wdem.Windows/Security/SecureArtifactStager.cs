using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Wdem.Core.Execution;

namespace Wdem.Windows.Security;

public enum SecureArtifactKind
{
  Executable,
  VisualStudioConfiguration
}

public interface ISecureArtifactDirectoryPolicy
{
  string CreateRestrictedStagingDirectory();
}

public interface ISecureArtifactStager
{
  Task<SecureArtifactStageResult> StageVerifiedAsync(
      string sourcePath,
      string expectedSha256,
      SecureArtifactKind kind,
      CancellationToken cancellationToken);
}

public sealed record SecureArtifactStageResult(
    SecureStagedArtifact? Artifact,
    StructuredError? Error);

public sealed class SecureStagedArtifact : IAsyncDisposable
{
  private readonly string _directoryPath;
  private FileStream? _readLock;
  private ArtifactLease? _artifactLease;

  internal SecureStagedArtifact(
      string directoryPath,
      string path,
      string sha256,
      FileStream readLock,
      ArtifactLease artifactLease)
  {
    _directoryPath = directoryPath;
    Path = path;
    Sha256 = sha256;
    _readLock = readLock;
    _artifactLease = artifactLease;
  }

  public string Path { get; }
  public string Sha256 { get; }

  public ValueTask DisposeAsync()
  {
    var readLock = Interlocked.Exchange(ref _readLock, null);
    if (readLock is null)
    {
      return ValueTask.CompletedTask;
    }

    readLock.Dispose();
    Interlocked.Exchange(ref _artifactLease, null)?.Dispose();
    TryDeleteDirectory(_directoryPath);
    return ValueTask.CompletedTask;
  }

  internal static void TryDeleteDirectory(string path)
    => ArtifactCleanupQueue.Shared.DeleteDirectory(path);
}

public sealed class SecureArtifactStager : ISecureArtifactStager
{
  private const long MaxExecutableBytes = 64L * 1024 * 1024;
  private const long MaxVisualStudioConfigurationBytes = 1024L * 1024;
  private readonly ISecureArtifactDirectoryPolicy _directoryPolicy;
  private readonly ITrustedFileVerifier _verifier;

  public SecureArtifactStager(
      ISecureArtifactDirectoryPolicy? directoryPolicy = null,
      ITrustedFileVerifier? verifier = null)
  {
    _directoryPolicy = directoryPolicy ?? new WindowsSecureArtifactDirectoryPolicy();
    _verifier = verifier ?? new TrustedFileVerifier();
  }

  internal Action? AfterSourceLengthChecked { get; init; }

  public async Task<SecureArtifactStageResult> StageVerifiedAsync(
      string sourcePath,
      string expectedSha256,
      SecureArtifactKind kind,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (string.IsNullOrWhiteSpace(sourcePath) ||
        sourcePath.Any(char.IsControl) ||
        !Path.IsPathFullyQualified(sourcePath) ||
        expectedSha256.Length != 64 ||
        !expectedSha256.All(Uri.IsHexDigit))
    {
      return Failure("The source path or expected SHA-256 is invalid.");
    }

    string? directoryPath = null;
    string? partialPath = null;
    string? finalPath = null;
    FileStream? readLock = null;
    ArtifactLease? artifactLease = null;
    try
    {
      directoryPath = _directoryPolicy.CreateRestrictedStagingDirectory();
      partialPath = Path.Combine(directoryPath, $".{Guid.NewGuid():N}.partial");
      finalPath = Path.Combine(
          directoryPath,
          kind == SecureArtifactKind.Executable ? "installer.exe" : "profile.vsconfig");
      if (!Path.IsPathFullyQualified(directoryPath) ||
          !Directory.Exists(directoryPath) ||
          Directory.EnumerateFileSystemEntries(directoryPath).Any())
      {
        throw new InvalidOperationException(
            "The secure staging policy did not create a new empty directory.");
      }

      artifactLease = ArtifactLease.Create(directoryPath);

      await using (var source = new FileStream(
                       Path.GetFullPath(sourcePath),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      await using (var destination = new FileStream(
                       partialPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        var maxBytes = kind == SecureArtifactKind.Executable
            ? MaxExecutableBytes
            : MaxVisualStudioConfigurationBytes;
        if (source.Length > maxBytes)
        {
          throw new ArtifactTooLargeException(maxBytes);
        }

        AfterSourceLengthChecked?.Invoke();
        await CopyWithinLimitAsync(
            source,
            destination,
            maxBytes,
            cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
      }

      File.Move(partialPath, finalPath);
      readLock = new FileStream(
          finalPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 1,
          FileOptions.SequentialScan);
      var verification = await _verifier.VerifySha256Async(
          finalPath,
          expectedSha256,
          cancellationToken).ConfigureAwait(false);
      if (!verification.IsTrusted)
      {
        readLock.Dispose();
        readLock = null;
        artifactLease.Dispose();
        artifactLease = null;
        SecureStagedArtifact.TryDeleteDirectory(directoryPath);
        return new SecureArtifactStageResult(null, verification.Error);
      }

      var artifact = new SecureStagedArtifact(
              directoryPath,
              verification.VerifiedPath!,
              verification.Sha256!,
              readLock,
              artifactLease);
      artifactLease = null;
      return new SecureArtifactStageResult(artifact, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      readLock?.Dispose();
      artifactLease?.Dispose();
      if (directoryPath is not null)
      {
        SecureStagedArtifact.TryDeleteDirectory(directoryPath);
      }

      throw;
    }
    catch (ArtifactTooLargeException exception)
    {
      readLock?.Dispose();
      artifactLease?.Dispose();
      if (directoryPath is not null)
      {
        SecureStagedArtifact.TryDeleteDirectory(directoryPath);
      }

      return Failure($"The artifact exceeds the {exception.MaxBytes} byte staging limit.");
    }
    catch (Exception exception) when (exception is IOException or
        Win32Exception or
        UnauthorizedAccessException or InvalidOperationException or
        PlatformNotSupportedException or SecurityException)
    {
      readLock?.Dispose();
      artifactLease?.Dispose();
      if (directoryPath is not null)
      {
        SecureStagedArtifact.TryDeleteDirectory(directoryPath);
      }

      return Failure("The artifact could not be copied into restricted staging.");
    }
  }

  private static async Task CopyWithinLimitAsync(
      Stream source,
      Stream destination,
      long maxBytes,
      CancellationToken cancellationToken)
  {
    var buffer = new byte[81920];
    long copiedBytes = 0;
    while (true)
    {
      var remainingBytes = maxBytes - copiedBytes;
      var bytesToRead = (int)Math.Min(buffer.Length, remainingBytes + 1);
      var bytesRead = await source.ReadAsync(
          buffer.AsMemory(0, bytesToRead),
          cancellationToken).ConfigureAwait(false);
      if (bytesRead == 0)
      {
        return;
      }

      if (bytesRead > remainingBytes)
      {
        throw new ArtifactTooLargeException(maxBytes);
      }

      await destination.WriteAsync(
          buffer.AsMemory(0, bytesRead),
          cancellationToken).ConfigureAwait(false);
      copiedBytes += bytesRead;
    }
  }

  private static SecureArtifactStageResult Failure(string detail) => new(
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "Secure artifact staging failed.",
          detail));

  private sealed class ArtifactTooLargeException(long maxBytes) : IOException
  {
    public long MaxBytes { get; } = maxBytes;
  }
}

public sealed class WindowsSecureArtifactDirectoryPolicy : ISecureArtifactDirectoryPolicy
{
  private const int ErrorAlreadyExists = 183;

  public string CreateRestrictedStagingDirectory()
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException(
          "Restricted artifact staging requires Windows access controls.");
    }

    var administrators = new SecurityIdentifier(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    security.AddAccessRule(new FileSystemAccessRule(
        administrators,
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(
        system,
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    if (string.IsNullOrWhiteSpace(commonData))
    {
      throw new InvalidOperationException("The shared Windows application-data path is unavailable.");
    }

    var productPath = Path.Combine(commonData, "Wdem");
    CreateRestrictedDirectory(
        productPath,
        security,
        administrators,
        system,
        mustCreate: false);
    var rootPath = Path.Combine(productPath, "SecureArtifacts");
    CreateRestrictedDirectory(
        rootPath,
        security,
        administrators,
        system,
        mustCreate: false);
    var stagingPath = Path.Combine(rootPath, Guid.NewGuid().ToString("N"));
    CreateRestrictedDirectory(
        stagingPath,
        security,
        administrators,
        system,
        mustCreate: true);
    return stagingPath;
  }

  private static void CreateRestrictedDirectory(
      string path,
      DirectorySecurity security,
      SecurityIdentifier administrators,
      SecurityIdentifier system,
      bool mustCreate)
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

    ValidateRestrictedDirectory(path, administrators, system);
  }

  private static void ValidateRestrictedDirectory(
      string path,
      SecurityIdentifier administrators,
      SecurityIdentifier system)
  {
    var info = new DirectoryInfo(path);
    if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new SecurityException(
          $"The restricted staging directory '{path}' is unavailable or redirected.");
    }

    var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
    var owner = security.GetOwner(typeof(SecurityIdentifier));
    if (!administrators.Equals(owner) && !system.Equals(owner))
    {
      throw new SecurityException(
          $"The restricted staging directory '{path}' has an untrusted owner.");
    }

    if (!security.AreAccessRulesProtected)
    {
      throw new SecurityException(
          $"The restricted staging directory '{path}' inherits access rules.");
    }

    var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    if (rules.Length != 2 ||
        !HasRequiredRule(rules, administrators) ||
        !HasRequiredRule(rules, system))
    {
      throw new SecurityException(
          $"The restricted staging directory '{path}' grants unexpected access.");
    }
  }

  internal static void ValidateRestrictedDirectory(string path) =>
      ValidateRestrictedDirectory(
          path,
          new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
          new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));

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
