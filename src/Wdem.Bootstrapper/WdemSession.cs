using Autofac;
using Wdem.Application.Runtime;
using Wdem.Core.Profiles;
using Wdem.Windows.Configuration;
using Wdem.Windows.Logging;

namespace Wdem.Bootstrapper;

/// <summary>
/// Owns the process-level dependency lifetime without exposing the Autofac container.
/// </summary>
public sealed class WdemSession : IDisposable
{
  private readonly IContainer _container;
  private bool _disposed;

  internal WdemSession(IContainer container)
  {
    _container = container;
    SessionLog = container.Resolve<JsonLineSessionLog>();
  }

  public JsonLineSessionLog SessionLog { get; }

  public WdemUserSettingsStore Settings => Resolve<WdemUserSettingsStore>();

  public ProfileCatalog ProfileCatalog => Resolve<ProfileCatalog>();

  public ITaskRuntime TaskRuntime => Resolve<ITaskRuntime>();

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _container.Dispose();
  }

  private T Resolve<T>() where T : notnull
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    return _container.Resolve<T>();
  }
}
