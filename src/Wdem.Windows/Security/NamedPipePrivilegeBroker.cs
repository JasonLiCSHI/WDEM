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
  private readonly SemaphoreSlim _sessionsGate = new(1, 1);
  private readonly Dictionary<Guid, HostSession> _sessions = [];
  private bool _disposed;

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

    HostSession session;
    try
    {
      session = await GetOrStartAsync(request.RunId, cancellationToken).ConfigureAwait(false);
    }
    catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
      return Failure(
          request.ResourceId,
          ApplyOutcome.Cancelled,
          WdemErrorCode.PermissionError,
          "Administrator approval was declined.",
          "The elevated resource was cancelled because the UAC prompt was declined.",
          exception);
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
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    Guid[] runIds;
    await _sessionsGate.WaitAsync().ConfigureAwait(false);
    try
    {
      runIds = _sessions.Keys.ToArray();
    }
    finally
    {
      _sessionsGate.Release();
    }

    foreach (var runId in runIds)
    {
      await CompleteRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
    }

    _sessionsGate.Dispose();
  }

  private async Task<HostSession> GetOrStartAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _sessionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      if (_sessions.TryGetValue(runId, out var existing))
      {
        return existing;
      }

      var pipeName = $"wdem-elevated-{runId:N}-{Guid.NewGuid():N}";
      var launched = await _launcher.StartAsync(runId, pipeName, cancellationToken)
          .ConfigureAwait(false);
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
}
