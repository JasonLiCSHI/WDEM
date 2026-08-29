using System.Diagnostics;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class CancellationDrainDeadline : IDisposable
{
  private readonly TimeSpan _budget;
  private readonly CancellationTokenRegistration _registration;
  private long _startedTimestamp = long.MinValue;

  public CancellationDrainDeadline(TimeSpan budget, CancellationToken cancellationToken)
  {
    if (budget <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(budget));
    }

    _budget = budget;
    _registration = cancellationToken.UnsafeRegister(
        static state => ((CancellationDrainDeadline)state!).Start(),
        this);
  }

  public bool IsStarted => Volatile.Read(ref _startedTimestamp) != long.MinValue;

  public TimeSpan Remaining
  {
    get
    {
      var started = Volatile.Read(ref _startedTimestamp);
      if (started == long.MinValue)
      {
        return _budget;
      }

      var elapsed = Stopwatch.GetElapsedTime(started);
      return elapsed >= _budget ? TimeSpan.Zero : _budget - elapsed;
    }
  }

  public void Dispose() => _registration.Dispose();

  internal void Start() => Interlocked.CompareExchange(
      ref _startedTimestamp,
      Stopwatch.GetTimestamp(),
      long.MinValue);
}

public interface IResourceScheduler
{
  TimeSpan CancellationDrainTimeout { get; }

  Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync = null,
      CancellationDrainDeadline? cancellationDeadline = null);
}
