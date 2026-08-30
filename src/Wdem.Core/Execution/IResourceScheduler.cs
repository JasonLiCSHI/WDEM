using System.Diagnostics;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class CancellationDrainDeadline : IDisposable
{
  private static readonly TimeSpan MaximumTimerDuration =
      TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
  private readonly object _gate = new();
  private readonly CancellationTokenRegistration _registration;
  private long _budgetTicks;
  private long _startedTimestamp = long.MinValue;

  public CancellationDrainDeadline(TimeSpan budget, CancellationToken cancellationToken)
  {
    if (budget <= TimeSpan.Zero || budget > MaximumTimerDuration)
    {
      throw new ArgumentOutOfRangeException(
          nameof(budget),
          budget,
          $"The budget must be positive and no greater than {MaximumTimerDuration}.");
    }

    _budgetTicks = budget.Ticks;
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
      var budget = TimeSpan.FromTicks(Volatile.Read(ref _budgetTicks));
      if (started == long.MinValue)
      {
        return budget;
      }

      var elapsed = Stopwatch.GetElapsedTime(started);
      return elapsed >= budget ? TimeSpan.Zero : budget - elapsed;
    }
  }

  public void Dispose() => _registration.Dispose();

  internal bool TryReserveAdditional(TimeSpan duration)
  {
    if (duration < TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(duration));
    }

    lock (_gate)
    {
      if (_startedTimestamp != long.MinValue)
      {
        return false;
      }

      var budget = TimeSpan.FromTicks(_budgetTicks);
      if (duration > MaximumTimerDuration - budget)
      {
        throw new ArgumentOutOfRangeException(nameof(duration));
      }

      _budgetTicks = (budget + duration).Ticks;
      return true;
    }
  }

  internal void Start()
  {
    lock (_gate)
    {
      if (_startedTimestamp == long.MinValue)
      {
        _startedTimestamp = Stopwatch.GetTimestamp();
      }
    }
  }
}

public interface IResourceScheduler
{
  TimeSpan CancellationDrainTimeout => TimeSpan.FromSeconds(2);

  Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync = null);

  Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync,
      CancellationDrainDeadline? cancellationDeadline) => ExecuteAsync(
          plan,
          executeAsync,
          capabilitiesFor,
          maximumConcurrency,
          cancellationToken,
          transitionAsync);
}
