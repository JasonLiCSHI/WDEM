using System.Collections.Concurrent;
using System.ComponentModel;
using Wdem.Core.Execution;
using Wdem.Core.Providers;

namespace Wdem.Windows.Security;

public sealed class NamedPipePrivilegeBroker :
    IPrivilegeBroker,
    IPrivilegeBrokerRunLifecycle,
    IAsyncDisposable
{
  private readonly IElevatedHostLauncher _launcher;
  private readonly object _lifecycleGate = new();
  private readonly SemaphoreSlim _sessionsGate = new(1, 1);
  private readonly Dictionary<Guid, HostSession> _sessions = [];
  private readonly Dictionary<Guid, LaunchFailure> _terminalLaunchFailures = [];
  // Closed entries remain until broker disposal so a completed run ID cannot reopen.
  private readonly ConcurrentDictionary<Guid, RunLifecycle> _runLifecycles = [];
  private TaskCompletionSource? _operationsDrained;
  private Task? _disposeTask;
  private int _activeOperations;
  private bool _disposeStarted;

  public NamedPipePrivilegeBroker(IElevatedHostLauncher launcher)
  {
    _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
  }

  public Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken) => ApplyAsync(
          request,
          progress,
          cancellationToken,
          null);

  public async Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken,
      CancellationDrainDeadline? cancellationDeadline)
  {
    ArgumentNullException.ThrowIfNull(request);
    Validate(request);
    using var operation = EnterOperation();

    try
    {
      return await ApplyWithinRunAsync(request, progress, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await CompleteCancelledRunAsync(request.RunId, cancellationDeadline)
          .ConfigureAwait(false);
      throw;
    }
  }

  public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken)
  {
    using var operation = EnterOperation();
    await CompleteRunCoreAsync(runId, cancellationToken).ConfigureAwait(false);
  }

  private async Task<ResourceApplyResult> ApplyWithinRunAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    var lifecycle = _runLifecycles.GetOrAdd(request.RunId, static _ => new RunLifecycle());
    using var runOperation = lifecycle.TryEnter();
    if (runOperation is null)
    {
      return ClosedRunFailure(request.ResourceId);
    }

    HostSession session;
    try
    {
      session = await GetOrStartAsync(request.RunId, lifecycle, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (CachedLaunchFailureException exception)
    {
      return exception.Failure.ForResource(request.ResourceId);
    }
    catch (ClosedRunException)
    {
      return ClosedRunFailure(request.ResourceId);
    }

    await session.RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      return await session.Session.ApplyAsync(request, progress, cancellationToken)
          .ConfigureAwait(false);
    }
    finally
    {
      session.RequestGate.Release();
    }
  }

  private async Task CompleteRunCoreAsync(Guid runId, CancellationToken cancellationToken)
  {
    var lifecycle = _runLifecycles.GetOrAdd(runId, static _ => new RunLifecycle());
    var operationsDrained = lifecycle.Close();
    cancellationToken.ThrowIfCancellationRequested();
    await operationsDrained.WaitAsync(cancellationToken).ConfigureAwait(false);
    await EnsureRunCleanupAsync(
        runId,
        lifecycle,
        operationsDrained,
        cancellationToken).ConfigureAwait(false);
  }

  private async Task CompleteCancelledRunAsync(
      Guid runId,
      CancellationDrainDeadline? cancellationDeadline)
  {
    if (cancellationDeadline is null)
    {
      await CompleteRunCoreAsync(runId, CancellationToken.None).ConfigureAwait(false);
      return;
    }

    try
    {
      using var cleanupCancellation = new CancellationTokenSource(
          cancellationDeadline.Remaining);
      await CompleteRunCoreAsync(runId, cleanupCancellation.Token).ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Cleanup diagnostics are reported by the outer run lifecycle; preserve cancellation here.
    }
  }

  private async Task EnsureRunCleanupAsync(
      Guid runId,
      RunLifecycle lifecycle,
      Task operationsDrained,
      CancellationToken cancellationToken)
  {
    var cleanup = lifecycle.BeginCleanup();
    if (cleanup.IsOwner)
    {
      try
      {
        await CleanupRunAsync(runId, operationsDrained, cancellationToken)
            .ConfigureAwait(false);
        lifecycle.CompleteCleanup();
      }
      catch (Exception exception)
      {
        lifecycle.FailCleanup(exception);
      }
    }

    await cleanup.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  private async Task CleanupRunAsync(
      Guid runId,
      Task operationsDrained,
      CancellationToken cancellationToken)
  {
    HostSession? session = null;
    await _sessionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      if (_sessions.Remove(runId, out var removed))
      {
        session = removed;
      }

      _terminalLaunchFailures.Remove(runId);
    }
    finally
    {
      _sessionsGate.Release();
    }

    Exception? cleanupFailure = null;
    if (session is not null)
    {
      try
      {
        var terminateTask = session.Session.TerminateAsync(cancellationToken);
        try
        {
          await terminateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
          ObserveFault(terminateTask);
          throw;
        }
      }
      catch (Exception exception)
      {
        cleanupFailure = exception;
      }

      try
      {
        var disposeTask = session.Session.DisposeAsync().AsTask();
        try
        {
          await disposeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
          ObserveFault(disposeTask);
          throw;
        }
      }
      catch (Exception exception)
      {
        cleanupFailure = cleanupFailure is null
            ? exception
            : new AggregateException(cleanupFailure, exception);
      }
    }

    await operationsDrained.WaitAsync(cancellationToken).ConfigureAwait(false);
    session?.RequestGate.Dispose();
    if (cleanupFailure is not null)
    {
      throw cleanupFailure;
    }
  }

  public async ValueTask DisposeAsync()
  {
    Task disposeTask;
    TaskCompletionSource? completion = null;
    lock (_lifecycleGate)
    {
      if (_disposeTask is not null)
      {
        disposeTask = _disposeTask;
      }
      else
      {
        _disposeStarted = true;
        completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _disposeTask = completion.Task;
        disposeTask = completion.Task;
      }
    }

    if (completion is not null)
    {
      _ = DisposeCoreAndSignalAsync(completion);
    }

    await disposeTask.ConfigureAwait(false);
  }

  private async Task DisposeCoreAndSignalAsync(TaskCompletionSource completion)
  {
    try
    {
      await DisposeCoreAsync().ConfigureAwait(false);
      completion.TrySetResult();
    }
    catch (Exception exception)
    {
      completion.TrySetException(exception);
    }
  }

  private async Task DisposeCoreAsync()
  {
    KeyValuePair<Guid, RunLifecycle>[] runs;
    await _sessionsGate.WaitAsync().ConfigureAwait(false);
    try
    {
      runs = _runLifecycles.Keys
          .Concat(_sessions.Keys)
          .Concat(_terminalLaunchFailures.Keys)
          .Distinct()
          .Select(runId => new KeyValuePair<Guid, RunLifecycle>(
              runId,
              _runLifecycles.GetOrAdd(runId, static _ => new RunLifecycle())))
          .ToArray();
    }
    finally
    {
      _sessionsGate.Release();
    }

    var cleanupTasks = runs.Select(run =>
    {
      var operationsDrained = run.Value.Close();
      return EnsureRunCleanupAsync(
          run.Key,
          run.Value,
          operationsDrained,
          CancellationToken.None);
    }).ToArray();
    Exception? cleanupFailure = null;
    try
    {
      await Task.WhenAll(cleanupTasks).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      cleanupFailure = exception;
    }

    await WaitForOperationsAsync().ConfigureAwait(false);
    _runLifecycles.Clear();
    _sessionsGate.Dispose();
    if (cleanupFailure is not null)
    {
      throw cleanupFailure;
    }
  }

  private async Task<HostSession> GetOrStartAsync(
      Guid runId,
      RunLifecycle lifecycle,
      CancellationToken cancellationToken)
  {
    await _sessionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      ThrowIfDisposing();
      if (lifecycle.IsClosed)
      {
        throw new ClosedRunException();
      }

      if (_sessions.TryGetValue(runId, out var existing))
      {
        return existing;
      }

      if (_terminalLaunchFailures.TryGetValue(runId, out var terminalFailure))
      {
        throw new CachedLaunchFailureException(terminalFailure);
      }

      var pipeName = $"wdem-elevated-{runId:N}-{Guid.NewGuid():N}";
      IElevatedHostSession launched;
      try
      {
        launched = await _launcher.StartAsync(runId, pipeName, cancellationToken)
            .ConfigureAwait(false);
      }
      catch (Exception exception) when (
          exception is not OperationCanceledException ||
          !cancellationToken.IsCancellationRequested)
      {
        if (lifecycle.IsClosed)
        {
          throw new ClosedRunException();
        }

        var failure = LaunchFailure.From(exception);
        _terminalLaunchFailures.Add(runId, failure);
        throw new CachedLaunchFailureException(failure);
      }

      if (lifecycle.IsClosed)
      {
        try
        {
          await launched.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
          await launched.DisposeAsync().ConfigureAwait(false);
        }

        throw new ClosedRunException();
      }

      var session = new HostSession(launched, new SemaphoreSlim(1, 1));
      _sessions.Add(runId, session);
      return session;
    }
    finally
    {
      _sessionsGate.Release();
    }
  }

  private OperationLease EnterOperation()
  {
    lock (_lifecycleGate)
    {
      ObjectDisposedException.ThrowIf(_disposeStarted, this);
      _activeOperations++;
      return new OperationLease(this);
    }
  }

  private void ExitOperation()
  {
    TaskCompletionSource? drained = null;
    lock (_lifecycleGate)
    {
      _activeOperations--;
      if (_activeOperations == 0)
      {
        drained = _operationsDrained;
        _operationsDrained = null;
      }
    }

    drained?.TrySetResult();
  }

  private Task WaitForOperationsAsync()
  {
    lock (_lifecycleGate)
    {
      if (_activeOperations == 0)
      {
        return Task.CompletedTask;
      }

      _operationsDrained ??= new TaskCompletionSource(
          TaskCreationOptions.RunContinuationsAsynchronously);
      return _operationsDrained.Task;
    }
  }

  private void ThrowIfDisposing()
  {
    lock (_lifecycleGate)
    {
      ObjectDisposedException.ThrowIf(_disposeStarted, this);
    }
  }

  private static void Validate(ElevatedResourceRequest request)
  {
    if (request.RunId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(request));
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceId);
    if (request.PlanFingerprint is null ||
        request.PlanFingerprint.Length != 64 ||
        !request.PlanFingerprint.All(Uri.IsHexDigit))
    {
      throw new ArgumentException(
          "The approved plan fingerprint must be a 64-character hexadecimal value.",
          nameof(request));
    }
  }

  private static void ObserveFault(Task task) => _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

  private static ResourceApplyResult Failure(
      string resourceId,
      ApplyOutcome outcome,
      WdemErrorCode code,
      string summary,
      string detail,
      Exception exception) => new()
      {
        ResourceId = resourceId,
        Outcome = outcome,
        Error = new StructuredError(code, summary, detail)
        {
          ResourceId = resourceId,
          UnderlyingException = exception
        }
      };

  private static ResourceApplyResult ClosedRunFailure(string resourceId) => new()
  {
    ResourceId = resourceId,
    Outcome = ApplyOutcome.Failed,
    Error = new StructuredError(
        WdemErrorCode.PermissionError,
        "Execution run is closed.",
        "The elevated resource cannot run because administrator cleanup has begun for its execution run.")
    {
      ResourceId = resourceId
    }
  };

  private sealed record HostSession(
      IElevatedHostSession Session,
      SemaphoreSlim RequestGate);

  private sealed class OperationLease(NamedPipePrivilegeBroker owner) : IDisposable
  {
    private NamedPipePrivilegeBroker? _owner = owner;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitOperation();
  }

  private sealed class RunLifecycle
  {
    private readonly object _gate = new();
    private readonly TaskCompletionSource _cleanupCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private bool _cleanupStarted;
    private bool _closed;

    public bool IsClosed
    {
      get
      {
        lock (_gate)
        {
          return _closed;
        }
      }
    }

    public RunOperationLease? TryEnter()
    {
      lock (_gate)
      {
        if (_closed)
        {
          return null;
        }

        _activeOperations++;
        return new RunOperationLease(this);
      }
    }

    public Task Close()
    {
      lock (_gate)
      {
        if (!_closed)
        {
          _closed = true;
          if (_activeOperations > 0)
          {
            _operationsDrained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
          }
        }

        return _operationsDrained?.Task ?? Task.CompletedTask;
      }
    }

    public RunCleanup BeginCleanup()
    {
      lock (_gate)
      {
        var isOwner = !_cleanupStarted;
        _cleanupStarted = true;
        return new RunCleanup(isOwner, _cleanupCompletion.Task);
      }
    }

    public void CompleteCleanup() => _cleanupCompletion.TrySetResult();

    public void FailCleanup(Exception exception) =>
        _cleanupCompletion.TrySetException(exception);

    private void Exit()
    {
      TaskCompletionSource? drained = null;
      lock (_gate)
      {
        _activeOperations--;
        if (_activeOperations == 0)
        {
          drained = _operationsDrained;
          _operationsDrained = null;
        }
      }

      drained?.TrySetResult();
    }

    public sealed class RunOperationLease(RunLifecycle owner) : IDisposable
    {
      private RunLifecycle? _owner = owner;

      public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
  }

  private sealed record RunCleanup(
      bool IsOwner,
      Task Completion);

  private sealed record LaunchFailure(
      ApplyOutcome Outcome,
      string Summary,
      string Detail,
      Exception Exception)
  {
    public static LaunchFailure From(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: 1223 }
            ? new LaunchFailure(
                ApplyOutcome.Cancelled,
                "Administrator approval was declined.",
                "The elevated resource was cancelled because the UAC prompt was declined.",
                exception)
            : new LaunchFailure(
                ApplyOutcome.Failed,
                "Elevated host could not be started.",
                "The elevated resource could not run because the administrator host failed to start.",
                exception);

    public ResourceApplyResult ForResource(string resourceId) => Failure(
        resourceId,
        Outcome,
        WdemErrorCode.PermissionError,
        Summary,
        Detail,
        Exception);
  }

  private sealed class CachedLaunchFailureException(LaunchFailure failure) : Exception
  {
    public LaunchFailure Failure { get; } = failure;
  }

  private sealed class ClosedRunException : Exception;
}
