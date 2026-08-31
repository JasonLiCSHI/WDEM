using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wdem.Windows.Persistence;

internal static class SecureBoundedFileReader
{
  private const FileShare ReplaceableReadShare = FileShare.Read | FileShare.Delete;
  private const uint GenericRead = 0x80000000;
  private const uint GenericWrite = 0x40000000;
  private const uint DeleteAccess = 0x00010000;
  private const uint CreateNew = 1;
  private const uint OpenExisting = 3;
  private const uint OpenAlways = 4;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagDeleteOnClose = 0x04000000;
  private const uint FileFlagOverlapped = 0x40000000;
  private const uint FileFlagSequentialScan = 0x08000000;
  private const uint FileNameNormalized = 0;
  private const int ErrorFileNotFound = 2;
  private const int ErrorPathNotFound = 3;

  public static async Task<byte[]> ReadAsync(
      string path,
      string expectedDirectory,
      int maximumBytes,
      string artifactName,
      CancellationToken cancellationToken,
      Action? beforeOpen = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(expectedDirectory);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);

    beforeOpen?.Invoke();
    await using var stream = OpenExistingWithoutFollowingLinks(
        path,
        expectedDirectory,
        artifactName);
    var attributes = File.GetAttributes(stream.SafeFileHandle);
    if (attributes.HasFlag(FileAttributes.Directory) ||
        attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new InvalidDataException($"The {artifactName} is not a regular file.");
    }

    string finalPath = NormalizeExtendedPath(ResolveFinalPath(stream.SafeFileHandle));
    string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory));
    string? actualDirectory = Path.GetDirectoryName(finalPath);
    if (!string.Equals(actualDirectory, expected, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidDataException(
          $"The {artifactName} resolves outside its expected directory.");
    }

    long length = stream.Length;
    if (length <= 0)
    {
      throw new InvalidDataException($"The {artifactName} is empty.");
    }

    EnsureLengthWithinMaximum(length, maximumBytes, artifactName);

    var bytes = new byte[(int)length];
    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
    var trailing = new byte[1];
    if (await stream.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
    {
      throw new InvalidDataException($"The {artifactName} changed while it was being read.");
    }

    return bytes;
  }

  internal static void EnsureLengthWithinMaximum(
      long length,
      int maximumBytes,
      string artifactName)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
    if (length > maximumBytes)
    {
      throw new InvalidDataException(
          $"The {artifactName} exceeds its maximum allowed size of {maximumBytes} bytes.");
    }
  }

  internal static SecureDirectoryLease OpenDirectoryLease(
      string path,
      string directoryName)
  {
    return OpenDirectoryLease(path, directoryName, allowMissing: false)!;
  }

  internal static SecureDirectoryLease? TryOpenDirectoryLease(
      string path,
      string directoryName)
  {
    return OpenDirectoryLease(path, directoryName, allowMissing: true);
  }

  internal static FileStream OpenMutableFile(
      string path,
      string expectedDirectory,
      FileMode mode,
      FileAccess access,
      string artifactName)
  {
    return OpenMutableFile(path, expectedDirectory, mode, access, artifactName, false)!;
  }

  internal static FileStream? TryOpenMutableFile(
      string path,
      string expectedDirectory,
      FileAccess access,
      string artifactName)
  {
    return OpenMutableFile(
        path,
        expectedDirectory,
        FileMode.Open,
        access,
        artifactName,
        allowMissing: true);
  }

  internal static FileStream OpenLockFile(
      string path,
      string expectedDirectory,
      bool deleteOnClose,
      string artifactName)
  {
    return OpenMutableFile(
        path,
        expectedDirectory,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        artifactName,
        allowMissing: false,
        FileShare.None,
        deleteOnClose ? DeleteAccess : 0,
        deleteOnClose ? FileFlagDeleteOnClose : 0)!;
  }

  private static FileStream? OpenMutableFile(
      string path,
      string expectedDirectory,
      FileMode mode,
      FileAccess access,
      string artifactName,
      bool allowMissing,
      FileShare shareMode = ReplaceableReadShare,
      uint additionalAccess = 0,
      uint additionalFlags = 0)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(expectedDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
    uint creationDisposition = mode switch
    {
      FileMode.CreateNew => CreateNew,
      FileMode.Open => OpenExisting,
      FileMode.OpenOrCreate => OpenAlways,
      _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    uint desiredAccess = access switch
    {
      FileAccess.Read => GenericRead,
      FileAccess.Write => GenericWrite,
      FileAccess.ReadWrite => GenericRead | GenericWrite,
      _ => throw new ArgumentOutOfRangeException(nameof(access))
    };
    var handle = CreateFile(
        path,
        desiredAccess | additionalAccess,
        shareMode,
        IntPtr.Zero,
        creationDisposition,
        FileFlagOpenReparsePoint | FileFlagOverlapped | additionalFlags,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      int error = Marshal.GetLastWin32Error();
      handle.Dispose();
      if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound)
      {
        return null;
      }

      throw new IOException(
          $"Could not securely open the {artifactName} '{path}'.",
          unchecked((int)(0x80070000u | (uint)error)));
    }

    try
    {
      var attributes = File.GetAttributes(handle);
      if (attributes.HasFlag(FileAttributes.Directory) ||
          attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new InvalidDataException($"The {artifactName} is not a regular file.");
      }

      string finalPath = NormalizeExtendedPath(ResolveFinalPath(handle));
      string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory));
      if (!string.Equals(
              Path.GetDirectoryName(finalPath),
              expected,
              StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidDataException(
            $"The {artifactName} resolves outside its expected directory.");
      }

      if (!GetFileInformationByHandle(handle, out var information))
      {
        int error = Marshal.GetLastWin32Error();
        throw new IOException(
            $"Could not inspect the {artifactName} '{path}'.",
            new Win32Exception(error));
      }

      if (information.NumberOfLinks != 1)
      {
        throw new InvalidDataException($"The {artifactName} has multiple hard links.");
      }

      return new FileStream(handle, access, bufferSize: 4096, isAsync: true);
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static SecureDirectoryLease? OpenDirectoryLease(
      string path,
      string directoryName,
      bool allowMissing)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
    var handle = CreateFile(
        path,
        GenericRead,
        FileShare.Read | FileShare.Write,
        IntPtr.Zero,
        OpenExisting,
        FileFlagOpenReparsePoint | FileFlagBackupSemantics,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      int error = Marshal.GetLastWin32Error();
      handle.Dispose();
      if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound)
      {
        return null;
      }

      throw new IOException(
          $"Could not securely open the {directoryName} directory '{path}'.",
          new Win32Exception(error));
    }

    try
    {
      var attributes = File.GetAttributes(handle);
      if (!attributes.HasFlag(FileAttributes.Directory) ||
          attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new InvalidDataException(
            $"The {directoryName} directory is not an owned regular directory.");
      }

      string finalPath = NormalizeExtendedPath(ResolveFinalPath(handle));
      string expectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidDataException(
            $"The {directoryName} directory resolves outside its expected location.");
      }

      return new SecureDirectoryLease(handle);
    }
    catch
    {
      handle.Dispose();
      throw;
    }
  }

  private static FileStream OpenExistingWithoutFollowingLinks(
      string path,
      string expectedDirectory,
      string artifactName)
  {
    return OpenMutableFile(
        path,
        expectedDirectory,
        FileMode.Open,
        FileAccess.Read,
        artifactName,
        allowMissing: false,
        additionalFlags: FileFlagSequentialScan)!;
  }

  private static string ResolveFinalPath(SafeFileHandle handle)
  {
    int capacity = 512;
    while (true)
    {
      var buffer = new StringBuilder(capacity);
      uint result = GetFinalPathNameByHandle(
          handle,
          buffer,
          (uint)buffer.Capacity,
          FileNameNormalized);
      if (result == 0)
      {
        int error = Marshal.GetLastWin32Error();
        throw new IOException(
            "Could not resolve the securely opened file path.",
            new Win32Exception(error));
      }

      if (result < buffer.Capacity)
      {
        return buffer.ToString();
      }

      capacity = checked((int)result + 1);
    }
  }

  private static string NormalizeExtendedPath(string path)
  {
    const string extendedUncPrefix = @"\\?\UNC\";
    const string extendedPrefix = @"\\?\";
    if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
    {
      return @"\\" + path[extendedUncPrefix.Length..];
    }

    return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
        ? path[extendedPrefix.Length..]
        : path;
  }

  [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
      SetLastError = true)]
  private static extern SafeFileHandle CreateFile(
      string fileName,
      uint desiredAccess,
      FileShare shareMode,
      IntPtr securityAttributes,
      uint creationDisposition,
      uint flagsAndAttributes,
      IntPtr templateFile);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandle(
      SafeFileHandle file,
      StringBuilder filePath,
      uint filePathLength,
      uint flags);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandle(
      SafeFileHandle file,
      out ByHandleFileInformation fileInformation);

  [StructLayout(LayoutKind.Sequential)]
  private struct ByHandleFileInformation
  {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
  }
}

internal sealed class SecureDirectoryLease(SafeFileHandle handle) : IDisposable
{
  public void Dispose() => handle.Dispose();
}
