using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Wdem.Core.Execution;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public interface IUiDispatcher
{
  Task EnqueueAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
  public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(action);
    cancellationToken.ThrowIfCancellationRequested();
    if (queue.HasThreadAccess)
    {
      action();
      return Task.CompletedTask;
    }

    var completion = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    if (!queue.TryEnqueue(() =>
        {
          try
          {
            action();
            completion.TrySetResult();
          }
          catch (Exception exception)
          {
            completion.TrySetException(exception);
          }
        }))
    {
      completion.TrySetException(new InvalidOperationException(
          "The desktop dispatcher is shutting down."));
    }

    return completion.Task;
  }
}

public sealed class ExecutionMonitorViewModel : ObservableObject, IDisposable
{
  private const int MaximumLogEntries = 5_000;
  private readonly object _eventGate = new();
  private readonly IEnvironmentRunService _runService;
  private readonly IRunEventSink _eventSink;
  private readonly LogRedactor _redactor;
  private readonly IUiDispatcher _dispatcher;
  private readonly RunRequest _request;
  private IDisposable? _subscription;
  private CancellationTokenSource? _runCancellation;
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

  public ExecutionMonitorViewModel(
      IEnvironmentRunService runService,
      IRunEventSink eventSink,
      LogRedactor redactor,
      IUiDispatcher dispatcher,
      RunRequest request)
  {
    ArgumentNullException.ThrowIfNull(runService);
    ArgumentNullException.ThrowIfNull(eventSink);
    ArgumentNullException.ThrowIfNull(redactor);
    ArgumentNullException.ThrowIfNull(dispatcher);
    ArgumentNullException.ThrowIfNull(request);
    _runService = runService;
    _eventSink = eventSink;
    _redactor = redactor;
    _dispatcher = dispatcher;
    _request = request;
    Resources = new ObservableCollection<ResourceProgressViewModel>();
    Logs = new ObservableCollection<LogEntryViewModel>();
    CancelCommand = new AsyncRelayCommand(
        _ => CancelAsync(),
        _ => IsRunning && !IsTerminal);
    RetryFailedCommand = new AsyncRelayCommand(
        _ => RetryFailedAsync(CancellationToken.None),
        _ => CanRetryFailed,
        ReportError);
  }

  public ObservableCollection<ResourceProgressViewModel> Resources { get; }

  public ObservableCollection<LogEntryViewModel> Logs { get; }

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
      RunOperationAsync(
          token => _runService.ApplyAsync(_request, token),
          cancellationToken);

  public async Task RetryFailedAsync(CancellationToken cancellationToken)
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

    await RunOperationAsync(
        token => _runService.RetryAsync(prior.RunId, failedIds, token),
        cancellationToken).ConfigureAwait(false);
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    Interlocked.Exchange(ref _subscription, null)?.Dispose();
    var cancellation = Interlocked.Exchange(ref _runCancellation, null);
    if (cancellation is not null)
    {
      cancellation.Cancel();
      cancellation.Dispose();
    }
  }

  private async Task RunOperationAsync(
      Func<CancellationToken, Task<ExecutionRun>> operation,
      CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (IsRunning)
    {
      throw new InvalidOperationException("An execution run is already active.");
    }

    ResetEventFilter();
    _run = null;
    OnPropertyChanged(nameof(Run));
    OnPropertyChanged(nameof(RunId));
    Resources.Clear();
    CurrentResource = null;
    TotalProgress = 0;
    ErrorMessage = null;
    _startedAt = DateTimeOffset.UtcNow;
    IsTerminal = false;
    IsRunning = true;
    var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _runCancellation = runCancellation;
    _subscription = _eventSink.SubscribeRequiredScoped(HandleEventAsync);
    using var elapsedRefreshCancellation = new CancellationTokenSource();
    Task elapsedRefresh = RefreshElapsedAsync(elapsedRefreshCancellation.Token);
    try
    {
      ExecutionRun completed = await Task.Run(
          () => operation(runCancellation.Token),
          CancellationToken.None).ConfigureAwait(false);
      await _dispatcher.EnqueueAsync(
          () => ApplyDurableSnapshot(completed),
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
    {
      await _dispatcher.EnqueueAsync(
          () => ErrorMessage = "运行已取消。",
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      string message = _redactor.Redact(UserErrorMessageFormatter.Format(exception));
      await _dispatcher.EnqueueAsync(
          () => ErrorMessage = message,
          CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
      elapsedRefreshCancellation.Cancel();
      try
      {
        await elapsedRefresh.ConfigureAwait(false);
      }
      catch (InvalidOperationException)
      {
        // Dispatcher shutdown must not retain a run-event subscription.
      }
      Interlocked.Exchange(ref _subscription, null)?.Dispose();
      try
      {
        await _dispatcher.EnqueueAsync(() =>
            {
              ElapsedDuration = DateTimeOffset.UtcNow - _startedAt;
              IsRunning = false;
              IsTerminal = true;
              RaiseCommandStates();
            }, CancellationToken.None).ConfigureAwait(false);
      }
      finally
      {
        Interlocked.CompareExchange(ref _runCancellation, null, runCancellation);
        runCancellation.Dispose();
      }
    }
  }

  private async Task RefreshElapsedAsync(CancellationToken cancellationToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
      {
        await _dispatcher.EnqueueAsync(
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
    await _dispatcher.EnqueueAsync(
        () => ApplyEvent(redacted),
        CancellationToken.None).ConfigureAwait(false);
  }

  private void ApplyEvent(RunEvent runEvent)
  {
    ElapsedDuration = DateTimeOffset.UtcNow - _startedAt;
    if (runEvent.ResourceId is not null)
    {
      CurrentResource = runEvent.ResourceId;
      ResourceProgressViewModel resource = GetOrAddResource(runEvent.ResourceId);
      if (runEvent.Progress is double progress)
      {
        resource.Percent = progress * 100;
      }

      resource.Message = runEvent.Message;
      resource.Error = runEvent.Error;
      if (runEvent.Kind == RunEventKind.ResourceStateChanged)
      {
        resource.State = ExecutionState.Running;
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
        step.State = ExecutionState.Running;
      }
    }

    AddLog(new LogEntryViewModel(runEvent, _redactor));
    UpdateTotalProgress();
  }

  private void ApplyDurableSnapshot(ExecutionRun run)
  {
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

    RestartRequirement = run.RestartRequirements
        .DefaultIfEmpty(RestartPolicy.NoRestart)
        .Max()
        .ToString();
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

  private void AddLog(LogEntryViewModel entry)
  {
    Logs.Add(entry);
    while (Logs.Count > MaximumLogEntries)
    {
      Logs.RemoveAt(0);
    }
  }

  private void UpdateTotalProgress() => TotalProgress = Resources.Count == 0
      ? 0
      : Resources.Average(resource => resource.Percent);

  private Task CancelAsync()
  {
    try
    {
      Volatile.Read(ref _runCancellation)?.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // A terminal transition won the race with the command invocation.
    }

    CancelCommand.RaiseCanExecuteChanged();
    return Task.CompletedTask;
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
