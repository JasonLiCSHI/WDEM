using Wdem.Bootstrapper;
using Xunit;

namespace Wdem.Bootstrapper.Tests;

public sealed class WdemBootstrapperTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      "wdem-bootstrapper-tests",
      Guid.NewGuid().ToString("N"));

  [Fact]
  public void SessionProvidesOneSharedInstanceOfEachProcessLevelDependency()
  {
    using var session = CreateSession("shared");

    Assert.Same(session.Settings, session.Settings);
    Assert.Same(session.ProfileCatalog, session.ProfileCatalog);
    Assert.Same(session.TaskRuntime, session.TaskRuntime);
    Assert.Same(session.SessionLog, session.SessionLog);
  }

  [Fact]
  public void SeparateSessionsDoNotShareDisposableState()
  {
    using var first = CreateSession("first");
    using var second = CreateSession("second");

    Assert.NotSame(first.Settings, second.Settings);
    Assert.NotSame(first.ProfileCatalog, second.ProfileCatalog);
    Assert.NotSame(first.TaskRuntime, second.TaskRuntime);
    Assert.NotSame(first.SessionLog, second.SessionLog);
  }

  [Fact]
  public void DisposedSessionRejectsFurtherResolution()
  {
    var session = CreateSession("disposed");
    session.Dispose();

    Assert.Throws<ObjectDisposedException>(() => session.TaskRuntime);
  }

  private WdemSession CreateSession(string name)
  {
    var sessionRoot = Path.Combine(_root, name);
    return WdemBootstrapper.StartSession(new WdemBootstrapperOptions(name)
    {
      SettingsPath = Path.Combine(sessionRoot, "settings.json"),
      CacheDirectory = Path.Combine(sessionRoot, "cache"),
      LogDirectory = Path.Combine(sessionRoot, "logs"),
      ApplicationDirectory = sessionRoot
    });
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
