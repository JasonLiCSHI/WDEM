using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wdem.Core.Profiles;

internal static class ProfilePathBoundary
{
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint OpenExisting = 3;

  public static string ResolveRoot(string rootPath) => ResolveDirectory(rootPath);

  public static bool IsOpenFileWithinResolvedRoot(
      SafeFileHandle fileHandle,
      string sourcePath,
      string resolvedRoot)
  {
    var resolvedFile = ResolveOpenFile(fileHandle, sourcePath);
    var comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    var relative = Path.GetRelativePath(resolvedRoot, resolvedFile);
    return !Path.IsPathRooted(relative) &&
        !relative.Equals("..", comparison) &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison) &&
        !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", comparison);
  }

  private static string ResolveOpenFile(SafeFileHandle fileHandle, string sourcePath)
  {
    if (OperatingSystem.IsWindows())
    {
      return GetFinalPath(fileHandle);
    }

    if (OperatingSystem.IsLinux())
    {
      var descriptorPath = $"/proc/self/fd/{fileHandle.DangerousGetHandle()}";
      return new FileInfo(descriptorPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
          ?? throw new IOException($"The opened profile path '{sourcePath}' could not be resolved.");
    }

    return new FileInfo(sourcePath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
        ?? Path.GetFullPath(sourcePath);
  }

  private static string ResolveDirectory(string path)
  {
    if (!OperatingSystem.IsWindows())
    {
      return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
          ?? Path.GetFullPath(path);
    }

    using var handle = CreateFile(
        path,
        0,
        FileShare.ReadWrite | FileShare.Delete,
        IntPtr.Zero,
        OpenExisting,
        FileFlagBackupSemantics,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      throw new IOException(
          $"The profile root '{path}' could not be opened.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }

    return GetFinalPath(handle);
  }

  private static string GetFinalPath(SafeFileHandle handle)
  {
    var buffer = new StringBuilder(512);
    var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
    if (length == 0)
    {
      throw new IOException(
          "The final profile path could not be resolved.",
          new Win32Exception(Marshal.GetLastWin32Error()));
    }

    if (length >= buffer.Capacity)
    {
      buffer = new StringBuilder(checked((int)length + 1));
      length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
      if (length == 0 || length >= buffer.Capacity)
      {
        throw new IOException(
            "The final profile path could not be resolved.",
            new Win32Exception(Marshal.GetLastWin32Error()));
      }
    }

    return buffer.ToString();
  }

  [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern SafeFileHandle CreateFile(
      string fileName,
      uint desiredAccess,
      FileShare shareMode,
      IntPtr securityAttributes,
      uint creationDisposition,
      uint flagsAndAttributes,
      IntPtr templateFile);

  [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern uint GetFinalPathNameByHandle(
      SafeFileHandle fileHandle,
      StringBuilder filePath,
      uint filePathLength,
      uint flags);
}
