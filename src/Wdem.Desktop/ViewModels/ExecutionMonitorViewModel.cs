using System.Collections.ObjectModel;
using Wdem.Core.Execution;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class ExecutionMonitorViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
  private const int MaximumLogEntries = 5_000;
  private readonly object _eventGate = new();
  private readonly object _operationGate = new();
  private readonly IReviewedPlanEnvironmentRunService _runService;
  private readonly IRunEventSink _eventSink;
  private readonly LogRedactor _redactor;
  private readonly IUiDispatcher _dispatcher;
  private readonly RunRequest _request;
  private readonly string _reviewedPlanFingerprint;
  private readonly ExecutionEventProjection _projection = new();
  private IDisposable? _subscription;
  private CancellationTokenSource? _runCancellation;
  private Task? _operationTask;
  private ExecutionRun? _run;
  private Guid? _activeRunId;
  private long _lastSequence;
  private bool _isRunning;
  private bool _isTerminal;
  private double _totalProgress;
  private TimeSpan _elapsedDuration;
  private string? _currentResource;
  private LogEntryViewModel? _selectedLogEntry;
  private string? _errorMessage;
  private string _restartRequirement = "NoRestart";
  private DateTimeOffset _startedAt;
  private bool _disposed;
  private bool _disposeRequested;
  private int _dispatcherUnavailable;

  public ExecutionMonitorViewModel(
      IReviewedPlanEnvironmentRunService runService,
      IRunEventSink eventSink,
      LogRedactor redactor,
      IUiDispatcher dispatcher,
      RunRequest request,
      string reviewedPlanFingerprint)
  {
    ArgumentNullException.ThrowIfNull(runService);
    ArgumentNullException.ThrowIfNull(eventSink);
    ArgumentNullException.ThrowIfNull(redactor);
    ArgumentNullException.ThrowIfNull(dispatcher);
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewedPlanFingerprint);
    _runService = runService;
    _eventSink = eventSink;
    _redactor = redactor;
    _dispatcher = dispatcher;
    _request = request;
    _reviewedPlanFingerprint = reviewedPlanFingerprint;
    Resources = new ObservableCollection<ResourceProgressViewModel>();
    Logs = new BoundedObservableCollection<LogEntryViewModel>(MaximumLogEntries);
    CancelCommand = new AsyncRelayCommand(
        _ => CancelAsync(),
        _ => IsRunning && !IsTerminal);
    RetryFailedCommand = new AsyncRelayCommand(
        _ => RetryFailedAsync(CancellationToken.None),
        _ => CanRetryFailed,
        ReportError);
  }

  public ObservableCollection<ResourceProgressViewModel> Resources { get; }

  public BoundedObservableCollection<LogEntryViewModel> Logs { get; }

  public ExecutionRun? Run => _run;

  public Guid? RunId => _run?.RunId ?? _activeRunId;

  public bool IsRunning
  {
    get => _isRunning;
    private set
    {
      if (SetProperty(ref _isRunning, value))
      {
        RaiseCommandStates();
      }
    }
  }

  public bool IsTerminal
  {
    get => _isTerminal;
    private set
    {
      if (SetProperty(ref _isTerminal, value))
      {
        RaiseCommandStates();
      }
    }
  }

  public bool CanRetryFailed =>
      IsTerminal &&
      _run?.ResourceResults.Values.Any(result =>
          result.Outcome == ExecutionOutcome.Failed) == true;

  public double TotalProgress
  {
    get => _totalProgress;
    private set
    {
      if (SetProperty(ref _totalProgress, Math.Clamp(value, 0, 100)))
      {
        OnPropertyChanged(nameof(TotalProgressDisplay));
      }
    }
  }

  public string TotalProgressDisplay => $"总进度：{TotalProgress:F0}%";

  public TimeSpan ElapsedDuration
  {
    get => _elapsedDuration;
    private set
    {
      if (SetProperty(ref _elapsedDuration, value))
      {
        OnPropertyChanged(nameof(ElapsedDisplay));
      }
    }
  }

  public string ElapsedDisplay => $"用时：{ElapsedDuration:hh\\:mm\\:ss}";

  public string? CurrentResource
  {
    get => _currentResource;
    private set
    {
      if (SetProperty(ref _currentResource, value))
      {
        OnPropertyChanged(nameof(CurrentResourceDisplay));
      }
    }
  }

  public string CurrentResourceDisplay => $"当前资源：{CurrentResource ?? "—"}";

  public LogEntryViewModel? SelectedLogEntry
  {
    get => _selectedLogEntry;
    set
    {
      if (SetProperty(ref _selectedLogEntry, value))
      {
        OnPropertyChanged(nameof(SelectedErrorDetail));
      }
    }
  }

  public string? SelectedErrorDetail => SelectedLogEntry?.ErrorDetail;

  public string RestartRequirement
  {
    get => _restartRequirement;
    private set
    {
      if (SetProperty(ref _restartRequirement, value))
      {
        OnPropertyChanged(nameof(RestartRequirementDisplay));
      }
    }
  }

  public string RestartRequirementDisplay => $"重启：{RestartRequirement}";

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public AsyncRelayCommand CancelCommand { get; }

  public AsyncRelayCommand RetryFailedCommand { get; }

  public Task StartAsync(CancellationToken cancellationToken = default) =>
      StartOperation(
          token => _runService.ApplyAsync(
              _request,
              _reviewedPlanFingerprint,
              token),
          cancellationToken);

  public async Task StopAsync()
  {
    CancellationTokenSource? cancellation;
    Task? operation;
    lock (_operationGate)
    {
      if (_disposed)
      {
        return;
      }

      cancellation = Volatile.Read(ref _runCancellation);
      operation = _operationTask;
    }

    RequestCancellation(cancellation);
    if (operation is not null)
    {
      await operation.ConfigureAwait(false);
    }
  }

  public async ValueTask DisposeAsync()
  {
    CancellationTokenSource? cancellation;
    Task? operation;
    lock (_operationGate)
    {
      if (_disposed)
      {
        return;
      }

      _disposeRequested = true;
      cancellation = Volatile.Read(ref _runCancellation);
      operation = _operationTask;
    }

    try
    {
      RequestCancellation(cancellation);
      if (operation is not null)
      {
        await operation.ConfigureAwait(false);
      }
    }
    finally
    {
      Dispose();
    }
  }

  public Task RetryFailedAsync(CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ExecutionRun prior = _run ?? throw new InvalidOperationException(
        "There is no completed execution run to retry.");
    var failedIds = prior.ResourceResults.Values
        .Where(result => result.Outcome == ExecutionOutcome.Failed)
        .Select(result => result.ResourceId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (failedIds.Count == 0)
    {
      throw new InvalidOperationException("The execution run has no failed resources to retry.");
    }

    return StartOperation(
        token => _runService.RetryAsync(prior.RunId, failedIds, token),
        cancellationToken);
  }

  public void Dispose()
  {
    CancellationTokenSource? cancellation;
    IDisposable? subscription;
    lock (_operationGate)
    {
      if (_disposed)
      {
        return;
      }

      _disposeRequested = true;
      _disposed = true;
      subscription = Interlocked.Exchange(ref _subscription, null);
      cancellation = Volatile.Read(ref _runCancellation);
    }

    subscription?.Dispose();
    RequestCancellation(cancellation);
  }

  private Task StartOperation(
      Func<CancellationToken, Task<ExecutionRun>> operation,
      CancellationToken cancellationToken)
  {
    CancellationTokenSource runCancellation;
    TaskCompletionSource completion;
    lock (_operationGate)
    {
      ObjectDisposedException.ThrowIf(_disposed || _disposeRequested, this);
      if (IsRunning || _operationTask is { IsCompleted: false })
      {
        throw new InvalidOperationException("An execution run is already active.");
      }

      runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      completion = new TaskCompletionSource(
          TaskCreationOptions.RunContinuationsAsynchronously);
      _runCancellation = runCancellation;
      _operationTask = completion.Task;
    }

    _ = CompleteOperationAsync(operation, runCancellation, completion);
    return completion.Task;
  }

  private async Task CompleteOperationAsync(
      Func<CancellationToken, Task<ExecutionRun>> operation,
      CancellationTokenSource runCancellation,
      TaskCompletionSource completion)
  {
    Exception? failure = null;
    try
    {
      await RunOperationAsync(operation, runCancellation).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      failure = exception;
    }

    try
    {
      Interlocked.CompareExchange(ref _runCancellation, null, runCancellation);
      runCancellation.Dispose();
    }
    catch (Exception exception)
    {
      failure ??= exception;
    }

    if (failure is OperationCanceledException cancellation)
    {
      completion.TrySetCanceled(cancellation.CancellationToken);
    }
    else if (failure is not null)
    {
      completion.TrySetException(failure);
    }
    else
    {
      completion.TrySetResult();
    }
  }

  private async Task RunOperationAsync(
      Func<CancellationToken, Task<ExecutionRun>> operation,
      CancellationTokenSource runCancellation)
  {
    CancellationTokenSource? elapsedRefreshCancellation = null;
    Task? elapsedRefresh = null;
    try
    {
      ResetEventFilter();
      _run = null;
      OnPropertyChanged(nameof(Run));
      OnPropertyChanged(nameof(RunId));
      Resources.Clear();
      ResetProjection();
      TotalProgress = 0;
      ErrorMessage = null;
      _startedAt = DateTimeOffset.UtcNow;
      IsTerminal = false;
      IsRunning = true;
      runCancellation.Token.ThrowIfCancellationRequested();
      _subscription = _eventSink.SubscribeScoped(HandleEventAsync);
      elapsedRefreshCancellation = new CancellationTokenSource();
      elapsedRefresh = RefreshElapsedAsync(elapsedRefreshCancellation.Token);
      ExecutionRun completed = await Task.Run(
          () => operation(runCancellation.Token),
          CancellationToken.None).ConfigureAwait(false);
      await DispatchAsync(
          () => ApplyDurableSnapshot(completed),
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
    {
      await DispatchAsync(
          () => ErrorMessage = "运行已取消。",
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      string message = _redactor.Redact(UserErrorMessageFormatter.Format(exception));
      await DispatchAsync(
          () => ErrorMessage = message,
          CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
      if (elapsedRefreshCancellation is not null)
      {
        elapsedRefreshCancellation.Cancel();
        if (elapsedRefresh is not null)
        {
          try
          {
            await elapsedRefresh.ConfigureAwait(false);
          }
          catch (InvalidOperationException)
          {
            // Dispatcher shutdown must not retain a run-event subscription.
          }
        }

        elapsedRefreshCancellation.Dispose();
      }

      Interlocked.Exchange(ref _subscription, null)?.Dispose();
      await DispatchAsync(() =>
          {
            ElapsedDuration = DateTimeOffset.UtcNow - _startedAt;
            _projection.Complete();
            ApplyProjection();
            IsRunning = false;
            IsTerminal = true;
            RaiseCommandStates();
          }, CancellationToken.None).ConfigureAwait(false);
    }
  }

  private async Task RefreshElapsedAsync(CancellationToken cancellationToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
      {
        await DispatchAsync(
            () => ElapsedDuration = DateTimeOffset.UtcNow - _startedAt,
            CancellationToken.None).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
  }

  private async Task HandleEventAsync(
      RunEvent runEvent,
      CancellationToken cancellationToken)
  {
    lock (_eventGate)
    {
      if (_activeRunId is null)
      {
        _activeRunId = runEvent.RunId;
      }

      if (_activeRunId != runEvent.RunId || runEvent.Sequence <= _lastSequence)
      {
        return;
      }

      _lastSequence = runEvent.Sequence;
    }

    RunEvent redacted = _redactor.Redact(runEvent);
    await DispatchAsync(
        () => ApplyEvent(redacted),
        CancellationToken.None).ConfigureAwait(false);
  }

  private async Task<bool> DispatchAsync(
      Action action,
      CancellationToken cancellationToken)
  {
    if (Volatile.Read(ref _dispatcherUnavailable) != 0)
    {
      return false;
    }

    try
    {
      await _dispatcher.EnqueueAsync(action, cancellationToken).ConfigureAwait(false);
      return true;
    }
    catch (UiDispatcherUnavailableException)
    {
      if (Interlocked.Exchange(ref _dispatcherUnavailable, 1) == 0)
      {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        RequestCancellation();
      }

      return false;
    }
  }

  private void ApplyEvent(RunEvent runEvent)
  {
    ElapsedDuration = DateTimeOffset.UtcNow - _startedAt;
    _projection.Apply(runEvent);
    if (runEvent.ResourceId is not null)
    {
      ResourceProgressViewModel resource = GetOrAddResource(runEvent.ResourceId);
      if (runEvent.Progress is double progress)
      {
        resource.Percent = progress * 100;
      }

      resource.Message = runEvent.Message;
      resource.Error = runEvent.Error;
      if (runEvent.Kind == RunEventKind.ResourceStateChanged)
      {
        if (runEvent.State is ExecutionState state)
        {
          resource.State = state;
        }

        resource.Outcome = runEvent.Outcome;
      }

      if (runEvent.StepId is not null)
      {
        StepProgressViewModel step = resource.GetOrAddStep(runEvent.StepId);
        if (runEvent.Progress is double stepProgress)
        {
          step.Percent = stepProgress * 100;
        }

        step.Message = runEvent.Message;
        step.Error = runEvent.Error;
        if (runEvent.Kind == RunEventKind.StepProgress)
        {
          if (runEvent.State is ExecutionState state)
          {
            step.State = state;
          }

          step.Outcome = runEvent.Outcome;
        }
      }

    }

    ApplyProjection();
    AddLog(new LogEntryViewModel(runEvent, _redactor));
    UpdateTotalProgress();
  }

  private void ApplyDurableSnapshot(ExecutionRun run)
  {
    _projection.ApplySnapshot(run);
    ApplyProjection();
    _run = run;
    OnPropertyChanged(nameof(Run));
    OnPropertyChanged(nameof(RunId));
    Resources.Clear();
    foreach (ResourceResult result in run.ResourceResults.Values.OrderBy(result => result.ResourceId))
    {
      var resource = new ResourceProgressViewModel(
          _redactor.Redact(result.ResourceId));
      resource.Apply(result, _redactor);
      Resources.Add(resource);
    }

    UpdateTotalProgress();
    OnPropertyChanged(nameof(CanRetryFailed));
  }

  private ResourceProgressViewModel GetOrAddResource(string resourceId)
  {
    var existing = Resources.FirstOrDefault(resource =>
        string.Equals(resource.Id, resourceId, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      return existing;
    }

    var created = new ResourceProgressViewModel(resourceId);
    Resources.Add(created);
    return created;
  }

  private void ResetProjection()
  {
    _projection.Reset();
    ApplyProjection();
  }

  private void ApplyProjection()
  {
    CurrentResource = _projection.CurrentResource;
    RestartRequirement = _projection.RestartRequirement.ToString();
  }

  private void AddLog(LogEntryViewModel entry)
  {
    Logs.Add(entry);
  }

  private void UpdateTotalProgress() => TotalProgress = Resources.Count == 0
      ? 0
      : Resources.Average(resource => resource.Percent);

  private Task CancelAsync()
  {
    RequestCancellation();
    CancelCommand.RaiseCanExecuteChanged();
    return Task.CompletedTask;
  }

  private void RequestCancellation()
      => RequestCancellation(Volatile.Read(ref _runCancellation));

  private static void RequestCancellation(CancellationTokenSource? cancellation)
  {
    try
    {
      cancellation?.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // A terminal transition won the race with the command invocation.
    }
  }

  private void ResetEventFilter()
  {
    lock (_eventGate)
    {
      _activeRunId = null;
      _lastSequence = 0;
    }
  }

  private void RaiseCommandStates()
  {
    CancelCommand.RaiseCanExecuteChanged();
    RetryFailedCommand.RaiseCanExecuteChanged();
    OnPropertyChanged(nameof(CanRetryFailed));
  }

  private void ReportError(Exception exception) =>
      ErrorMessage = UserErrorMessageFormatter.Format(exception);
}
