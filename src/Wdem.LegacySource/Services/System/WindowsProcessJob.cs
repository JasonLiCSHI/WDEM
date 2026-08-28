using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wdem.LegacySource.Services.System;

internal sealed class WindowsProcessJob : IDisposable
{
  private const uint JobObjectLimitKillOnJobClose = 0x00002000;
  private const uint ExtendedStartupInfoPresent = 0x00080000;
  private const uint CreateNoWindow = 0x08000000;
  private const uint StartfUseStdHandles = 0x00000100;
  private const uint HandleFlagInherit = 0x00000001;
  private const nuint ProcThreadAttributeHandleList = 0x00020002;
  private const nuint ProcThreadAttributeJobList = 0x0002000D;

  private SafeFileHandle? _jobHandle;

  private WindowsProcessJob(
      SafeFileHandle jobHandle,
      Process process,
      StreamReader standardOutput,
      StreamReader standardError)
  {
    _jobHandle = jobHandle;
    Process = process;
    StandardOutput = standardOutput;
    StandardError = standardError;
  }

  public Process Process { get; }

  public StreamReader StandardOutput { get; }

  public StreamReader StandardError { get; }

  public static WindowsProcessJob Start(
      string fileName,
      IEnumerable<string> arguments)
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException();
    }

    using var standardInput = OpenNullInput();
    CreateOutputPipe(out var outputRead, out var outputWrite);
    CreateOutputPipe(out var errorRead, out var errorWrite);
    var outputReadTransferred = false;
    var errorReadTransferred = false;
    var jobHandle = CreateConfiguredJob();
    IntPtr attributeList = IntPtr.Zero;
    IntPtr jobHandleList = IntPtr.Zero;
    IntPtr inheritedHandleList = IntPtr.Zero;
    ProcessInformation processInformation = default;
    Process? managedProcess = null;
    StreamReader? outputReader = null;
    StreamReader? errorReader = null;

    try
    {
      attributeList = CreateAttributeList(2);
      jobHandleList = Marshal.AllocHGlobal(IntPtr.Size);
      Marshal.WriteIntPtr(jobHandleList, jobHandle.DangerousGetHandle());
      UpdateAttribute(
          attributeList,
          ProcThreadAttributeJobList,
          jobHandleList,
          (nuint)IntPtr.Size);

      inheritedHandleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
      Marshal.WriteIntPtr(inheritedHandleList, 0, standardInput.DangerousGetHandle());
      Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size, outputWrite.DangerousGetHandle());
      Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
      UpdateAttribute(
          attributeList,
          ProcThreadAttributeHandleList,
          inheritedHandleList,
          (nuint)(IntPtr.Size * 3));

      var startupInfo = new StartupInfoEx
      {
        StartupInfo = new StartupInfo
        {
          Size = Marshal.SizeOf<StartupInfoEx>(),
          Flags = StartfUseStdHandles,
          StandardInput = standardInput.DangerousGetHandle(),
          StandardOutput = outputWrite.DangerousGetHandle(),
          StandardError = errorWrite.DangerousGetHandle()
        },
        AttributeList = attributeList
      };
      var commandLine = new StringBuilder(BuildCommandLine(fileName, arguments));

      if (!CreateProcess(
              null,
              commandLine,
              IntPtr.Zero,
              IntPtr.Zero,
              inheritHandles: true,
              ExtendedStartupInfoPresent | CreateNoWindow,
              IntPtr.Zero,
              null,
              ref startupInfo,
              out processInformation))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      outputWrite.Dispose();
      errorWrite.Dispose();

      managedProcess = Process.GetProcessById((int)processInformation.ProcessId);
      outputReader = new StreamReader(new FileStream(outputRead, FileAccess.Read));
      outputReadTransferred = true;
      errorReader = new StreamReader(new FileStream(errorRead, FileAccess.Read));
      errorReadTransferred = true;

      return new WindowsProcessJob(jobHandle, managedProcess, outputReader, errorReader);
    }
    catch
    {
      outputReader?.Dispose();
      errorReader?.Dispose();
      managedProcess?.Dispose();
      jobHandle.Dispose();
      throw;
    }
    finally
    {
      if (!outputReadTransferred) outputRead.Dispose();
      outputWrite.Dispose();
      if (!errorReadTransferred) errorRead.Dispose();
      errorWrite.Dispose();
      if (processInformation.ProcessHandle != IntPtr.Zero)
      {
        CloseHandle(processInformation.ProcessHandle);
      }
      if (processInformation.ThreadHandle != IntPtr.Zero)
      {
        CloseHandle(processInformation.ThreadHandle);
      }
      if (attributeList != IntPtr.Zero)
      {
        DeleteProcThreadAttributeList(attributeList);
        Marshal.FreeHGlobal(attributeList);
      }
      if (jobHandleList != IntPtr.Zero)
      {
        Marshal.FreeHGlobal(jobHandleList);
      }
      if (inheritedHandleList != IntPtr.Zero)
      {
        Marshal.FreeHGlobal(inheritedHandleList);
      }
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
    _jobHandle?.Dispose();
    _jobHandle = null;
  }

  public void Dispose()
  {
    Terminate();
    StandardOutput.Dispose();
    StandardError.Dispose();
    Process.Dispose();
  }

  private static SafeFileHandle CreateConfiguredJob()
  {
    var handle = CreateJobObject(IntPtr.Zero, null);
    if (handle.IsInvalid)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

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

      return handle;
    }
    catch
    {
      handle.Dispose();
      throw;
    }
    finally
    {
      Marshal.FreeHGlobal(pointer);
    }
  }

  private static SafeFileHandle OpenNullInput()
  {
    var securityAttributes = CreateInheritableSecurityAttributes();
    var handle = CreateFile(
        "NUL",
        0x80000000,
        0x00000001 | 0x00000002,
        ref securityAttributes,
        3,
        0,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    return handle;
  }

  private static void CreateOutputPipe(
      out SafeFileHandle readHandle,
      out SafeFileHandle writeHandle)
  {
    var securityAttributes = CreateInheritableSecurityAttributes();
    if (!CreatePipe(out var read, out writeHandle, ref securityAttributes, 0))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    if (!SetHandleInformation(read, HandleFlagInherit, 0))
    {
      read.Dispose();
      writeHandle.Dispose();
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    readHandle = read;
  }

  private static SecurityAttributes CreateInheritableSecurityAttributes() =>
      new()
      {
        Length = Marshal.SizeOf<SecurityAttributes>(),
        InheritHandle = true
      };

  private static IntPtr CreateAttributeList(int attributeCount)
  {
    nuint size = 0;
    InitializeProcThreadAttributeList(
        IntPtr.Zero,
        attributeCount,
        0,
        ref size);
    var attributeList = Marshal.AllocHGlobal((nint)size);
    if (!InitializeProcThreadAttributeList(
            attributeList,
            attributeCount,
            0,
            ref size))
    {
      Marshal.FreeHGlobal(attributeList);
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    return attributeList;
  }

  private static void UpdateAttribute(
      IntPtr attributeList,
      nuint attribute,
      IntPtr value,
      nuint size)
  {
    if (!UpdateProcThreadAttribute(
            attributeList,
            0,
            attribute,
            value,
            size,
            IntPtr.Zero,
            IntPtr.Zero))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
  }

  private static string BuildCommandLine(
      string fileName,
      IEnumerable<string> arguments) =>
      string.Join(
          " ",
          new[] { QuoteArgument(fileName) }.Concat(arguments.Select(QuoteArgument)));

  private static string QuoteArgument(string argument)
  {
    if (argument.Length > 0 &&
        !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
    {
      return argument;
    }

    var quoted = new StringBuilder("\"");
    var backslashes = 0;
    foreach (var character in argument)
    {
      if (character == '\\')
      {
        backslashes++;
        continue;
      }

      if (character == '"')
      {
        quoted.Append('\\', backslashes * 2 + 1);
        quoted.Append('"');
        backslashes = 0;
        continue;
      }

      quoted.Append('\\', backslashes);
      backslashes = 0;
      quoted.Append(character);
    }

    quoted.Append('\\', backslashes * 2);
    quoted.Append('"');
    return quoted.ToString();
  }

  private uint GetActiveProcessCount()
  {
    var handle = _jobHandle;
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
  private struct SecurityAttributes
  {
    public int Length;
    public IntPtr SecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)]
    public bool InheritHandle;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct StartupInfo
  {
    public int Size;
    public string? Reserved;
    public string? Desktop;
    public string? Title;
    public int X;
    public int Y;
    public int XSize;
    public int YSize;
    public int XCountChars;
    public int YCountChars;
    public int FillAttribute;
    public uint Flags;
    public short ShowWindow;
    public short Reserved2Size;
    public IntPtr Reserved2;
    public IntPtr StandardInput;
    public IntPtr StandardOutput;
    public IntPtr StandardError;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct StartupInfoEx
  {
    public StartupInfo StartupInfo;
    public IntPtr AttributeList;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessInformation
  {
    public IntPtr ProcessHandle;
    public IntPtr ThreadHandle;
    public uint ProcessId;
    public uint ThreadId;
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
  private static extern bool QueryInformationJobObject(
      SafeFileHandle job,
      JobObjectInformationClass informationClass,
      IntPtr information,
      uint informationLength,
      out uint returnLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreatePipe(
      out SafeFileHandle readPipe,
      out SafeFileHandle writePipe,
      ref SecurityAttributes pipeAttributes,
      uint size);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetHandleInformation(
      SafeFileHandle handle,
      uint mask,
      uint flags);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFile(
      string fileName,
      uint desiredAccess,
      uint shareMode,
      ref SecurityAttributes securityAttributes,
      uint creationDisposition,
      uint flagsAndAttributes,
      IntPtr templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool InitializeProcThreadAttributeList(
      IntPtr attributeList,
      int attributeCount,
      int flags,
      ref nuint size);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UpdateProcThreadAttribute(
      IntPtr attributeList,
      uint flags,
      nuint attribute,
      IntPtr value,
      nuint size,
      IntPtr previousValue,
      IntPtr returnSize);

  [DllImport("kernel32.dll")]
  private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateProcess(
      string? applicationName,
      StringBuilder commandLine,
      IntPtr processAttributes,
      IntPtr threadAttributes,
      [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
      uint creationFlags,
      IntPtr environment,
      string? currentDirectory,
      ref StartupInfoEx startupInfo,
      out ProcessInformation processInformation);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);
}
