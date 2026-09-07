using Autofac;
using Wdem.Application.Execution;
using Wdem.Application.Inspection;
using Wdem.Application.Planning;
using Wdem.Application.Profiles;
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

  public IProfileRepository ProfileRepository => Resolve<IProfileRepository>();

  public CreatePlanHandler CreatePlan => Resolve<CreatePlanHandler>();

  public InspectEnvironmentHandler InspectEnvironment =>
      Resolve<InspectEnvironmentHandler>();

  public ApplyPlanHandler ApplyPlan => Resolve<ApplyPlanHandler>();

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
