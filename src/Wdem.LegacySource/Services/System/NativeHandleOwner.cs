using Microsoft.Win32.SafeHandles;

namespace Wdem.LegacySource.Services.System;

internal sealed class NativeHandleOwner : IDisposable
{
  private readonly Action? _onRelease;
  private SafeFileHandle? _handle;
  private int _releaseCount;

  public NativeHandleOwner(SafeFileHandle handle, Action? onRelease = null)
  {
    _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    _onRelease = onRelease;
  }

  public SafeFileHandle Handle => _handle ??
      throw new ObjectDisposedException(nameof(NativeHandleOwner));

  public bool IsDisposed => _handle is null;

  public int ReleaseCount => Volatile.Read(ref _releaseCount);

  public SafeFileHandle Detach() => Interlocked.Exchange(ref _handle, null) ??
      throw new ObjectDisposedException(nameof(NativeHandleOwner));

  public void Dispose()
  {
    var handle = Interlocked.Exchange(ref _handle, null);
    if (handle is null)
    {
      return;
    }

    handle.Dispose();
    Interlocked.Increment(ref _releaseCount);
    _onRelease?.Invoke();
  }
}

internal enum WindowsProcessJobConstructionStage
{
  StandardInput,
  OutputPipe,
  ErrorPipe,
  Job,
  AttributeList,
  ManagedProcess
}

internal sealed class WindowsProcessJobPostStartException(Exception innerException) :
    Exception("Process setup failed after native process creation.", innerException);

internal interface IWindowsProcessJobNativeResources
{
  void BeforeStage(WindowsProcessJobConstructionStage stage);

  NativeHandleOwner OpenNullInput();

  (NativeHandleOwner Read, NativeHandleOwner Write) CreateOutputPipe();

  NativeHandleOwner CreateConfiguredJob();
}
