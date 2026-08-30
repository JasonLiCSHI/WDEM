using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wdem.Windows.Configuration;

internal sealed partial class ConfigurationDirectoryLease : IDisposable
{
  private readonly List<SafeFileHandle> _handles;

  private ConfigurationDirectoryLease(string directoryPath, List<SafeFileHandle> handles)
  {
    DirectoryPath = directoryPath;
    _handles = handles;
  }

  internal string DirectoryPath { get; }

  internal static ConfigurationDirectoryLease Acquire(string directoryPath)
  {
    var fullDirectory = Path.GetFullPath(directoryPath);
    var root = Path.GetPathRoot(fullDirectory);
    if (string.IsNullOrWhiteSpace(root))
    {
      throw new IOException("The configuration destination has no trusted filesystem root.");
    }

    var handles = new List<SafeFileHandle>();
    try
    {
      var current = root;
      handles.Add(OpenValidatedDirectory(current));
      var relative = Path.GetRelativePath(root, fullDirectory);
      foreach (var segment in relative.Split(
                   [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                   StringSplitOptions.RemoveEmptyEntries))
      {
        if (segment is "." or "..")
        {
          throw new IOException("The configuration destination hierarchy is invalid.");
        }

        current = Path.Combine(current, segment);
        Directory.CreateDirectory(current);
        handles.Add(OpenValidatedDirectory(current));
      }

      return new ConfigurationDirectoryLease(fullDirectory, handles);
    }
    catch
    {
      foreach (var handle in handles)
      {
        handle.Dispose();
      }

      throw;
    }
  }

  public void Dispose()
  {
    for (var index = _handles.Count - 1; index >= 0; index--)
    {
      _handles[index].Dispose();
    }

    _handles.Clear();
  }

  private static SafeFileHandle OpenValidatedDirectory(string path)
  {
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
    {
      throw new UnsafeConfigurationDirectoryException(
          $"The configuration destination directory contains an unsafe reparse point: '{path}'.");
    }

    var handle = NativeMethods.CreateFile(
        path,
        NativeMethods.GenericRead,
        NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
        0,
        NativeMethods.OpenExisting,
        NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
        0);
    if (handle.IsInvalid)
    {
      var error = Marshal.GetLastPInvokeError();
      handle.Dispose();
      throw new IOException(
          $"The configuration destination directory could not be leased: '{path}'.",
          new Win32Exception(error));
    }

    if (NativeMethods.GetFileInformationByHandleEx(
            handle,
            NativeMethods.FileAttributeTagInfo,
            out var information,
            (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>()) == 0)
    {
      var error = Marshal.GetLastPInvokeError();
      handle.Dispose();
      throw new IOException(
          $"The configuration destination directory could not be inspected: '{path}'.",
          new Win32Exception(error));
    }

    if ((information.FileAttributes & NativeMethods.FileAttributeDirectory) == 0 ||
        (information.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
    {
      handle.Dispose();
      throw new UnsafeConfigurationDirectoryException(
          $"The configuration destination directory contains an unsafe reparse point: '{path}'.");
    }

    return handle;
  }

  private static partial class NativeMethods
  {
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint GenericRead = 0x80000000;
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInformation
    {
      internal uint FileAttributes;
      internal uint ReparseTag;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    internal static partial int GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);
  }
}

internal sealed class UnsafeConfigurationDirectoryException(string message) : IOException(message);
