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
  private readonly long _baseBudgetTicks;
  private readonly CancellationToken _cancellationToken;
  private readonly Dictionary<long, int> _finalizationReservations = [];
  private readonly CancellationTokenRegistration _registration;
  private long _startedTimestamp = long.MinValue;
  private long _startedMaximumReservationTicks;
  private int _disposeState;

  public CancellationDrainDeadline(TimeSpan budget, CancellationToken cancellationToken)
      : this(budget, cancellationToken, TimeSpan.Zero)
  {
  }

  public CancellationDrainDeadline(
      TimeSpan budget,
      CancellationToken cancellationToken,
      TimeSpan maximumPotentialFinalization)
  {
    if (budget <= TimeSpan.Zero || budget > MaximumTimerDuration)
    {
      throw new ArgumentOutOfRangeException(
          nameof(budget),
          budget,
          $"The budget must be positive and no greater than {MaximumTimerDuration}.");
    }

    if (maximumPotentialFinalization < TimeSpan.Zero ||
        maximumPotentialFinalization > MaximumTimerDuration - budget)
    {
      throw new ArgumentOutOfRangeException(
          nameof(maximumPotentialFinalization),
          maximumPotentialFinalization,
          "The finalization reservation must fit within the platform timer limit.");
    }

    _baseBudgetTicks = budget.Ticks;
    _cancellationToken = cancellationToken;
    if (maximumPotentialFinalization > TimeSpan.Zero)
    {
      _finalizationReservations.Add(maximumPotentialFinalization.Ticks, 1);
    }

    _registration = cancellationToken.UnsafeRegister(
        static state => ((CancellationDrainDeadline)state!).Start(),
        this);
  }

  public bool IsStarted => Volatile.Read(ref _startedTimestamp) != long.MinValue;

  public TimeSpan Remaining
  {
    get
    {
      long started;
      TimeSpan budget;
      lock (_gate)
      {
        started = _startedTimestamp;
        var maximumReservation = started == long.MinValue
            ? CurrentMaximumReservationTicks()
            : _startedMaximumReservationTicks;
        budget = TimeSpan.FromTicks(_baseBudgetTicks + maximumReservation);
      }

      if (started == long.MinValue)
      {
        return budget;
      }

      var elapsed = Stopwatch.GetElapsedTime(started);
      return elapsed >= budget ? TimeSpan.Zero : budget - elapsed;
    }
  }

  public IDisposable RegisterPotentialFinalization(TimeSpan duration)
  {
    if (duration < TimeSpan.Zero ||
        duration > MaximumTimerDuration - TimeSpan.FromTicks(_baseBudgetTicks))
    {
      throw new ArgumentOutOfRangeException(nameof(duration));
    }

    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(
          _disposeState != 0,
          this);
      if (_startedTimestamp != long.MinValue)
      {
        throw new InvalidOperationException(
            "Finalization reservations must be registered before the cancellation deadline starts.");
      }

      if (duration == TimeSpan.Zero)
      {
        return EmptyReservation.Instance;
      }

      if (_finalizationReservations.TryGetValue(duration.Ticks, out var count))
      {
        _finalizationReservations[duration.Ticks] = count + 1;
      }
      else
      {
        _finalizationReservations.Add(duration.Ticks, 1);
      }

    }

    return new FinalizationReservation(this, duration.Ticks);
  }

  public void Dispose()
  {
    lock (_gate)
    {
      if (_disposeState != 0)
      {
        return;
      }

      _disposeState = 1;
    }

    _registration.Dispose();
  }

  internal void Start()
  {
    lock (_gate)
    {
      StartUnderLock();
    }
  }

  private void StartUnderLock()
  {
    if (_startedTimestamp == long.MinValue)
    {
      _startedMaximumReservationTicks = CurrentMaximumReservationTicks();
      _startedTimestamp = Stopwatch.GetTimestamp();
    }
  }

  private long CurrentMaximumReservationTicks() => _finalizationReservations.Count == 0
      ? 0
      : _finalizationReservations.Keys.Max();

  private void ReleaseFinalization(long durationTicks)
  {
    lock (_gate)
    {
      if (_cancellationToken.IsCancellationRequested)
      {
        StartUnderLock();
      }

      if (!_finalizationReservations.TryGetValue(durationTicks, out var count))
      {
        return;
      }

      if (count == 1)
      {
        _finalizationReservations.Remove(durationTicks);
      }
      else
      {
        _finalizationReservations[durationTicks] = count - 1;
      }
    }
  }

  private sealed class FinalizationReservation(
      CancellationDrainDeadline owner,
      long durationTicks) : IDisposable
  {
    private int _disposeState;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposeState, 1) == 0)
      {
        owner.ReleaseFinalization(durationTicks);
      }
    }
  }

  private sealed class EmptyReservation : IDisposable
  {
    public static EmptyReservation Instance { get; } = new();

    public void Dispose()
    {
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

  Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync,
      CancellationDrainDeadline? cancellationDeadline,
      Action<Task>? registerUndrainedCompletion) => ExecuteAsync(
          plan,
          executeAsync,
          capabilitiesFor,
          maximumConcurrency,
          cancellationToken,
          transitionAsync,
          cancellationDeadline);
}
