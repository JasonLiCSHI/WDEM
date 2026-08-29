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
  private TaskCompletionSource? _operationsDrained;
  private Task? _disposeTask;
  private int _activeOperations;
  private bool _disposeStarted;

  public NamedPipePrivilegeBroker(IElevatedHostLauncher launcher)
  {
    _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
  }

  public async Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    Validate(request);
    using var operation = EnterOperation();

    HostSession session;
    try
    {
      session = await GetOrStartAsync(request.RunId, cancellationToken).ConfigureAwait(false);
    }
    catch (CachedLaunchFailureException exception)
    {
      return exception.Failure.ForResource(request.ResourceId);
    }

    await session.RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var scopedRequest = request with { PipeName = session.PipeName };
      return await session.Session.ApplyAsync(scopedRequest, progress, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await TerminateRunAsync(request.RunId, CancellationToken.None).ConfigureAwait(false);
      throw;
    }
    finally
    {
      session.RequestGate.Release();
    }
  }

  public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken)
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

    if (session is not null)
    {
      await session.Session.TerminateAsync(cancellationToken).ConfigureAwait(false);
      await session.Session.DisposeAsync().ConfigureAwait(false);
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
    HostSession[] sessions;
    await _sessionsGate.WaitAsync().ConfigureAwait(false);
    try
    {
      sessions = _sessions.Values.ToArray();
      _sessions.Clear();
      _terminalLaunchFailures.Clear();
    }
    finally
    {
      _sessionsGate.Release();
    }

    Exception? cleanupFailure = null;
    foreach (var session in sessions)
    {
      try
      {
        await session.Session.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        await session.Session.DisposeAsync().ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        cleanupFailure ??= exception;
      }
    }

    await WaitForOperationsAsync().ConfigureAwait(false);
    foreach (var session in sessions)
    {
      session.RequestGate.Dispose();
    }

    _sessionsGate.Dispose();
    if (cleanupFailure is not null)
    {
      throw cleanupFailure;
    }
  }

  private async Task<HostSession> GetOrStartAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    await _sessionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      ThrowIfDisposing();
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
        var failure = LaunchFailure.From(exception);
        _terminalLaunchFailures.Add(runId, failure);
        throw new CachedLaunchFailureException(failure);
      }
      var session = new HostSession(pipeName, launched, new SemaphoreSlim(1, 1));
      _sessions.Add(runId, session);
      return session;
    }
    finally
    {
      _sessionsGate.Release();
    }
  }

  private Task TerminateRunAsync(Guid runId, CancellationToken cancellationToken) =>
      CompleteRunAsync(runId, cancellationToken);

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

  private sealed record HostSession(
      string PipeName,
      IElevatedHostSession Session,
      SemaphoreSlim RequestGate);

  private sealed class OperationLease(NamedPipePrivilegeBroker owner) : IDisposable
  {
    private NamedPipePrivilegeBroker? _owner = owner;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitOperation();
  }

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
}
