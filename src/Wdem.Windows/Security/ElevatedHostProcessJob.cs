using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wdem.Windows.Security;

internal sealed class ElevatedHostProcessJob : IDisposable
{
  private const string NamePrefix = @"Local\Wdem.ElevatedHost.";
  private Process? _process;
  private SafeFileHandle? _job;

  private ElevatedHostProcessJob(SafeFileHandle job)
  {
    _job = job;
  }

  public static string NameForPipe(string pipeName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
    return $"{NamePrefix}{pipeName}";
  }

  public static ElevatedHostProcessJob Create(string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException("Elevated host jobs require Windows.");
    }

    var job = NativeMethods.CreateJobObject(IntPtr.Zero, name);
    if (job.IsInvalid)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    var alreadyExists = Marshal.GetLastWin32Error() == NativeMethods.ErrorAlreadyExists;

    try
    {
      if (alreadyExists)
      {
        throw new InvalidOperationException("The elevated host job name is already in use.");
      }

      var information = new NativeMethods.JobObjectExtendedLimitInformation
      {
        BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
        {
          LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
        }
      };
      if (!NativeMethods.SetInformationJobObject(
              job,
              NativeMethods.JobObjectExtendedLimitInformationClass,
              ref information,
              (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return new ElevatedHostProcessJob(job);
    }
    catch
    {
      job.Dispose();
      throw;
    }
  }

  public static void JoinCurrentProcess(string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException("Elevated host jobs require Windows.");
    }

    using var job = NativeMethods.OpenJobObject(
        NativeMethods.JobObjectAssignProcess,
        inheritHandle: false,
        name);
    if (job.IsInvalid)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    if (!NativeMethods.AssignProcessToJobObject(job, NativeMethods.GetCurrentProcess()))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
  }

  public void Track(Process process)
  {
    ArgumentNullException.ThrowIfNull(process);
    if (Interlocked.CompareExchange(ref _process, process, null) is not null)
    {
      throw new InvalidOperationException("The elevated host job already tracks a process.");
    }
  }

  public void Terminate()
  {
    Interlocked.Exchange(ref _job, null)?.Dispose();
  }

  public void Dispose()
  {
    Terminate();
    Interlocked.Exchange(ref _process, null)?.Dispose();
  }

  private static class NativeMethods
  {
    internal const int ErrorAlreadyExists = 183;
    internal const uint JobObjectAssignProcess = 0x0001;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle OpenJobObject(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
      public long PerProcessUserTimeLimit;
      public long PerJobUserTimeLimit;
      public uint LimitFlags;
      public UIntPtr MinimumWorkingSetSize;
      public UIntPtr MaximumWorkingSetSize;
      public uint ActiveProcessLimit;
      public UIntPtr Affinity;
      public uint PriorityClass;
      public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
      public ulong ReadOperationCount;
      public ulong WriteOperationCount;
      public ulong OtherOperationCount;
      public ulong ReadTransferCount;
      public ulong WriteTransferCount;
      public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
      public JobObjectBasicLimitInformation BasicLimitInformation;
      public IoCounters IoInfo;
      public UIntPtr ProcessMemoryLimit;
      public UIntPtr JobMemoryLimit;
      public UIntPtr PeakProcessMemoryUsed;
      public UIntPtr PeakJobMemoryUsed;
    }
  }
}
