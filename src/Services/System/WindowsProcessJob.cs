using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinHome.Services.System;

internal sealed class WindowsProcessJob : IDisposable
{
  private const uint JobObjectLimitKillOnJobClose = 0x00002000;
  private SafeFileHandle? _handle;

  private WindowsProcessJob(SafeFileHandle handle)
  {
    _handle = handle;
  }

  public static WindowsProcessJob? TryCreateAndAssign(Process process)
  {
    if (!OperatingSystem.IsWindows())
    {
      return null;
    }

    var handle = CreateJobObject(IntPtr.Zero, null);
    if (handle.IsInvalid)
    {
      handle.Dispose();
      return null;
    }

    try
    {
      var limits = new JobObjectExtendedLimitInformation
      {
        BasicLimitInformation = new JobObjectBasicLimitInformation
        {
          LimitFlags = JobObjectLimitKillOnJobClose
        }
      };
      var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
      var pointer = Marshal.AllocHGlobal(length);
      try
      {
        Marshal.StructureToPtr(limits, pointer, false);
        if (!SetInformationJobObject(
                handle,
                JobObjectInformationClass.ExtendedLimitInformation,
                pointer,
                (uint)length))
        {
          throw new Win32Exception(Marshal.GetLastWin32Error());
        }
      }
      finally
      {
        Marshal.FreeHGlobal(pointer);
      }

      if (!AssignProcessToJobObject(handle, process.Handle))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return new WindowsProcessJob(handle);
    }
    catch (Exception error)
    {
      global::System.Diagnostics.Trace.WriteLine(
          $"[ProcessRunner] Could not assign process to a Windows Job Object: {error.Message}");
      handle.Dispose();
      return null;
    }
  }

  public async Task WaitForEmptyAsync(CancellationToken cancellationToken)
  {
    while (GetActiveProcessCount() > 0)
    {
      await Task.Delay(50, cancellationToken);
    }
  }

  public void Terminate()
  {
    _handle?.Dispose();
    _handle = null;
  }

  public void Dispose()
  {
    Terminate();
  }

  private uint GetActiveProcessCount()
  {
    var handle = _handle;
    if (handle is null || handle.IsInvalid || handle.IsClosed)
    {
      return 0;
    }

    var length = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
    var pointer = Marshal.AllocHGlobal(length);
    try
    {
      if (!QueryInformationJobObject(
              handle,
              JobObjectInformationClass.BasicAccountingInformation,
              pointer,
              (uint)length,
              out _))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(pointer)
          .ActiveProcesses;
    }
    finally
    {
      Marshal.FreeHGlobal(pointer);
    }
  }

  private enum JobObjectInformationClass
  {
    BasicAccountingInformation = 1,
    ExtendedLimitInformation = 9
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoCounters
  {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectBasicLimitInformation
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
  private struct JobObjectExtendedLimitInformation
  {
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectBasicAccountingInformation
  {
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(
      SafeFileHandle job,
      JobObjectInformationClass informationClass,
      IntPtr information,
      uint informationLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(
      SafeFileHandle job,
      IntPtr process);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryInformationJobObject(
      SafeFileHandle job,
      JobObjectInformationClass informationClass,
      IntPtr information,
      uint informationLength,
      out uint returnLength);
}
