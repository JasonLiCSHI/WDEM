using Wdem.Bootstrapper;
using Wdem.Testing;
using Xunit;

namespace Wdem.Bootstrapper.Tests;

public sealed class WdemBootstrapperTests : TemporaryDirectoryTestBase
{
  [Fact]
  public void Session_WhenDependencyIsResolvedRepeatedly_ThenReturnsOneSharedInstance()
  {
    using var session = CreateSession("shared");

    Assert.Same(session.ProfileTrust, session.ProfileTrust);
    Assert.Same(session.ProfileRepository, session.ProfileRepository);
    Assert.Same(session.CreatePlan, session.CreatePlan);
    Assert.Same(session.InspectEnvironment, session.InspectEnvironment);
    Assert.Same(session.ApplyPlan, session.ApplyPlan);
    Assert.Same(session.SessionLog, session.SessionLog);
  }

  [Fact]
  public void Sessions_WhenCreatedSeparately_ThenDoNotShareDisposableState()
  {
    using var first = CreateSession("first");
    using var second = CreateSession("second");

    Assert.NotSame(first.ProfileTrust, second.ProfileTrust);
    Assert.NotSame(first.ProfileRepository, second.ProfileRepository);
    Assert.NotSame(first.CreatePlan, second.CreatePlan);
    Assert.NotSame(first.InspectEnvironment, second.InspectEnvironment);
    Assert.NotSame(first.ApplyPlan, second.ApplyPlan);
    Assert.NotSame(first.SessionLog, second.SessionLog);
  }

  [Fact]
  public void Session_WhenDisposed_ThenRejectsFurtherResolution()
  {
    var session = CreateSession("disposed");
    session.Dispose();

    Assert.Throws<ObjectDisposedException>(() => session.ApplyPlan);
  }

  private WdemSession CreateSession(string name)
  {
    var sessionRoot = TestPath(name);
    return WdemBootstrapper.StartSession(new WdemBootstrapperOptions(name)
    {
      SettingsPath = Path.Combine(sessionRoot, "settings.json"),
      CacheDirectory = Path.Combine(sessionRoot, "cache"),
      LogDirectory = Path.Combine(sessionRoot, "logs"),
      ApplicationDirectory = sessionRoot
    });
  }
}
