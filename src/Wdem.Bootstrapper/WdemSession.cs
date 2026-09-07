using Autofac;
using Wdem.Application.Execution;
using Wdem.Application.Inspection;
using Wdem.Application.Logging;
using Wdem.Application.Planning;
using Wdem.Application.Profiles;

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
    SessionLog = container.Resolve<ISessionLog>();
  }

  public ISessionLog SessionLog { get; }

  public IProfileTrustStore ProfileTrust => Resolve<IProfileTrustStore>();

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
