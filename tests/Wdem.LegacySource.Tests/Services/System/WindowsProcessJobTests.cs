using Microsoft.Win32.SafeHandles;
using Wdem.LegacySource.Services.System;
using Xunit;

namespace Wdem.LegacySource.Tests.Services.System;

public sealed class WindowsProcessJobTests
{
  [Theory]
  [InlineData(2, 3)]
  [InlineData(3, 5)]
  [InlineData(4, 6)]
  public void Start_WhenConstructionStageFails_DisposesEveryAcquiredHandleExactlyOnce(
      int failingStageValue,
      int expectedAcquiredHandles)
  {
    var failingStage = (WindowsProcessJobConstructionStage)failingStageValue;
    var native = new FaultingNativeResources(failingStage);

    Assert.Throws<InvalidOperationException>(() =>
        WindowsProcessJob.Start("unused.exe", [], null, native));

    Assert.Equal(expectedAcquiredHandles, native.Owners.Count);
    Assert.All(native.Owners, owner =>
    {
      Assert.True(owner.IsDisposed);
      Assert.Equal(1, owner.ReleaseCount);
    });
  }

  [Fact]
  public void NativeHandleOwner_DisposeIsIdempotent()
  {
    var releases = 0;
    var owner = new NativeHandleOwner(
        new SafeFileHandle(new IntPtr(123), ownsHandle: false),
        () => releases++);

    owner.Dispose();
    owner.Dispose();

    Assert.True(owner.IsDisposed);
    Assert.Equal(1, releases);
  }

  [Fact]
  public void Start_FailureAfterCreateProcessIsClassifiedAsPostStart()
  {
    var native = new FaultingNativeResources(
        WindowsProcessJobConstructionStage.ManagedProcess,
        WindowsProcessJob.DefaultNativeResources.Instance);

    Assert.Throws<WindowsProcessJobPostStartException>(() =>
        WindowsProcessJob.Start(
            "cmd.exe",
            ["/d", "/c", "exit /b 0"],
            null,
            native));
  }

  private sealed class FaultingNativeResources(
      WindowsProcessJobConstructionStage failingStage,
      IWindowsProcessJobNativeResources? inner = null) :
      IWindowsProcessJobNativeResources
  {
    public List<NativeHandleOwner> Owners { get; } = [];

    public void BeforeStage(WindowsProcessJobConstructionStage stage)
    {
      if (stage == failingStage)
      {
        throw new InvalidOperationException($"Injected failure at {stage}.");
      }
    }

    public NativeHandleOwner OpenNullInput() => inner?.OpenNullInput() ?? CreateOwner();

    public (NativeHandleOwner Read, NativeHandleOwner Write) CreateOutputPipe() =>
        inner?.CreateOutputPipe() ?? (CreateOwner(), CreateOwner());

    public NativeHandleOwner CreateConfiguredJob() =>
        inner?.CreateConfiguredJob() ?? CreateOwner();

    private NativeHandleOwner CreateOwner()
    {
      var owner = new NativeHandleOwner(
          new SafeFileHandle(new IntPtr(100 + Owners.Count), ownsHandle: false));
      Owners.Add(owner);
      return owner;
    }
  }
}
