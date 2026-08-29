using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wdem.Windows.Security;

internal sealed class ElevatedHostProcessJob : IDisposable
{
  private readonly Process _process;
  private SafeFileHandle? _job;

  private ElevatedHostProcessJob(Process process, SafeFileHandle job)
  {
    _process = process;
    _job = job;
  }

  public static ElevatedHostProcessJob Attach(Process process)
  {
    ArgumentNullException.ThrowIfNull(process);
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException("Elevated host jobs require Windows.");
    }

    var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
    if (job.IsInvalid)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    try
    {
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

      if (!NativeMethods.AssignProcessToJobObject(job, process.SafeHandle))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return new ElevatedHostProcessJob(process, job);
    }
    catch
    {
      job.Dispose();
      throw;
    }
  }

  public void Terminate()
  {
    _job?.Dispose();
    _job = null;
    try
    {
      if (!_process.HasExited)
      {
        _process.Kill(entireProcessTree: true);
      }
    }
    catch (InvalidOperationException)
    {
      // The worker already exited.
    }
  }

  public void Dispose()
  {
    Terminate();
    _process.Dispose();
  }

  private static class NativeMethods
  {
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

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
        SafeProcessHandle process);

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
