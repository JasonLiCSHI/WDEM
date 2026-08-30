using Wdem.Core.Graph;
using Wdem.Core.Execution;
using Wdem.Core.Profiles;
using Wdem.Core.Reporting;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
  private readonly ResourceGraphBuilder _graphBuilder;
  private readonly IProfileCatalog _catalog;
  private readonly IReviewedPlanEnvironmentRunService? _environmentRuns;
  private readonly IRunEventSink? _runEvents;
  private readonly LogRedactor? _redactor;
  private readonly IUiDispatcher? _dispatcher;
  private readonly IRunReportExporter? _reportExporter;
  private readonly object _inspectionGate = new();
  private readonly TaskCompletionSource _disposeCompletion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
  private object _currentPage;
  private ResourceSelectionViewModel? _resourceSelection;
  private ExecutionMonitorViewModel? _executionMonitor;
  private TrackedInspectionOperation? _activeInspection;
  private string? _errorMessage;
  private bool _isInspecting;
  private bool _isDisposed;
  private bool _disposeStarted;

  public MainWindowViewModel(
      IProfileCatalog catalog,
      ResourceGraphBuilder graphBuilder,
      IReviewedPlanEnvironmentRunService? environmentRuns = null,
      IRunEventSink? runEvents = null,
      LogRedactor? redactor = null,
      IUiDispatcher? dispatcher = null,
      IRunReportExporter? reportExporter = null)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(graphBuilder);
    _catalog = catalog;
    _graphBuilder = graphBuilder;
    _environmentRuns = environmentRuns;
    _runEvents = runEvents;
    _redactor = redactor;
    _dispatcher = dispatcher;
    _reportExporter = reportExporter ?? (redactor is null ? null : new RunReportExporter(redactor));
    ProfileSelection = new ProfileSelectionViewModel(
        catalog,
        SelectProfile,
        ReportError,
        ClearErrors);
    _currentPage = ProfileSelection;
    NavigateToProfilesCommand = new AsyncRelayCommand(
        _ => NavigateToProfilesAsync(),
        _ => !HasActiveExecution && !_isDisposed,
        onError: exception => _ = ReportError(exception));
    NavigateToResourcesCommand = new AsyncRelayCommand(
        _ => NavigateToResourcesAsync(),
        _ => ResourceSelection is not null && !HasActiveExecution && !_isDisposed,
        exception => _ = ReportError(exception));
  }

  public object CurrentPage
  {
    get => _currentPage;
    private set => SetProperty(ref _currentPage, value);
  }

  public ProfileSelectionViewModel ProfileSelection { get; }

  public ResourceSelectionViewModel? ResourceSelection
  {
    get => _resourceSelection;
    private set
    {
      if (SetProperty(ref _resourceSelection, value))
      {
        OnPropertyChanged(nameof(CanNavigateToResources));
        NavigateToResourcesCommand.RaiseCanExecuteChanged();
      }
    }
  }

  public bool CanNavigateToResources => ResourceSelection is not null;

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public AsyncRelayCommand NavigateToProfilesCommand { get; }

  public AsyncRelayCommand NavigateToResourcesCommand { get; }

  public async Task InitializeAsync()
  {
    IReadOnlyList<RecoveryCandidate> candidates = [];
    Exception? discoveryFailure = null;
    if (_environmentRuns is not null)
    {
      await RunTrackedInspectionAsync(async cancellationToken =>
      {
        try
        {
          candidates = await _environmentRuns.FindRecoveryCandidatesAsync(cancellationToken);
          cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
          discoveryFailure = exception;
        }
      });
    }

    if (_isDisposed)
    {
      return;
    }

    await ProfileSelection.LoadCommand.ExecuteAsync(null);
    if (_isDisposed)
    {
      return;
    }

    if (discoveryFailure is not null)
    {
      ReportError(discoveryFailure);
    }
    else if (candidates.Count > 0)
    {
      RecoveryCandidatesViewModel? recovery = null;
      recovery = new RecoveryCandidatesViewModel(
          candidates,
          _redactor!,
          candidate => RecoverCandidateAsync(candidate, recovery!),
          candidate => AbandonCandidateAsync(candidate, recovery!));
      CurrentPage = recovery;
    }
  }

  private Task AbandonCandidateAsync(
      RecoveryCandidate candidate,
      RecoveryCandidatesViewModel recovery) =>
      RunTrackedInspectionAsync(async cancellationToken =>
      {
        ClearErrors();
        try
        {
          await _environmentRuns!.AbandonAsync(candidate.RunId, cancellationToken);
          cancellationToken.ThrowIfCancellationRequested();
          TryPresentInspectionResult(() =>
          {
            recovery.Remove(candidate);
            CurrentPage = recovery.Candidates.Count == 0
                ? ProfileSelection
                : recovery;
          });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
          TryPresentInspectionResult(() => ReportError(exception));
        }
      });

  private async Task RecoverCandidateAsync(
      RecoveryCandidate candidate,
      RecoveryCandidatesViewModel recovery)
  {
    EnsureExecutionComposition();
    if (HasActiveExecution || _isDisposed)
    {
      return;
    }

    if (_executionMonitor is not null)
    {
      _executionMonitor.PropertyChanged -= MonitorPropertyChanged;
      _executionMonitor.Dispose();
    }

    var monitor = new ExecutionMonitorViewModel(
        _environmentRuns!,
        _runEvents!,
        _redactor!,
        _dispatcher!);
    _executionMonitor = monitor;
    monitor.PropertyChanged += MonitorPropertyChanged;
    CurrentPage = monitor;
    Task operation = monitor.RecoverAsync(candidate.RunId);
    RaiseNavigationStates();
    await operation;
    RaiseNavigationStates();
    if (monitor.Run is { } terminalRun && !_isDisposed)
    {
      NavigateToCompletion(terminalRun, reviewedPlan: null);
    }
    else if (!_isDisposed)
    {
      CurrentPage = recovery;
      if (monitor.ErrorMessage is { Length: > 0 } error)
      {
        ErrorMessage = error;
      }
    }
  }

  public ValueTask DisposeAsync()
  {
    TrackedInspectionOperation? inspection;
    lock (_inspectionGate)
    {
      if (_disposeStarted)
      {
        return new ValueTask(_disposeCompletion.Task);
      }

      _disposeStarted = true;
      _isDisposed = true;
      inspection = _activeInspection;
    }

    _ = CompleteDisposeAsync(inspection);
    return new ValueTask(_disposeCompletion.Task);
  }

  private async Task CompleteDisposeAsync(TrackedInspectionOperation? inspection)
  {
    List<Exception>? failures = null;
    RaiseNavigationStates();
    if (inspection is not null)
    {
      try
      {
        await inspection.CancelAsync().ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        (failures ??= []).Add(exception);
      }

      try
      {
        await inspection.Completion.ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        (failures ??= []).Add(exception);
      }
    }

    ExecutionMonitorViewModel? monitor = _executionMonitor;
    if (monitor is not null)
    {
      try
      {
        await monitor.DisposeAsync().ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        (failures ??= []).Add(exception);
      }
      finally
      {
        monitor.PropertyChanged -= MonitorPropertyChanged;
      }
    }

    if (failures is null)
    {
      _disposeCompletion.TrySetResult();
    }
    else if (failures.Count == 1)
    {
      _disposeCompletion.TrySetException(failures[0]);
    }
    else
    {
      _disposeCompletion.TrySetException(new AggregateException(failures));
    }
  }

  private void SelectProfile(DeveloperProfile profile)
  {
    ResourceSelection = new ResourceSelectionViewModel(
        profile,
        _graphBuilder,
        NavigateToPlanAsync,
        ReportError,
        ClearErrors);
    CurrentPage = ResourceSelection;
  }

  private Task NavigateToProfilesAsync()
  {
    if (HasActiveExecution || _isDisposed)
    {
      return Task.CompletedTask;
    }

    ClearErrors();
    CurrentPage = ProfileSelection;
    return Task.CompletedTask;
  }

  private Task NavigateToResourcesAsync()
  {
    if (HasActiveExecution || _isDisposed)
    {
      return Task.CompletedTask;
    }

    ClearErrors();
    if (ResourceSelection is not null)
    {
      CurrentPage = ResourceSelection;
    }

    return Task.CompletedTask;
  }

  private async Task NavigateToPlanAsync(ResourceSelectionNavigationRequest request)
  {
    if (HasActiveExecution || _isDisposed)
    {
      return;
    }

    await RunTrackedInspectionAsync(async cancellationToken =>
    {
      ClearErrors();
      EnsureExecutionComposition();
      ProfileLoadResult loaded = await _catalog.LoadAsync(
          request.Profile.Id,
          cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      if (!loaded.IsValid)
      {
        if (loaded.Errors.FirstOrDefault() is StructuredError error)
        {
          throw new StructuredErrorException(error);
        }

        throw new InvalidOperationException("所选配置文件无法加载。");
      }

      var runRequest = new RunRequest(
          loaded.SourcePath,
          request.Selection.SelectedOptionalResourceIds ??
              new HashSet<string>(StringComparer.OrdinalIgnoreCase));
      if (request.Action == ResourceSelectionAction.CheckEnvironment)
      {
        Task<ExecutionRun> inspection = _environmentRuns!.InspectAsync(
            runRequest,
            cancellationToken);
        ExecutionRun run = await inspection;
        cancellationToken.ThrowIfCancellationRequested();
        TryPresentInspectionResult(() => NavigateToCompletion(run, reviewedPlan: null));

        return;
      }

      PlanViewModel? plan = null;
      plan = new PlanViewModel(
          _environmentRuns!,
          _redactor!,
          runRequest,
          (requestToApply, reviewedPlanFingerprint) => NavigateToExecutionAsync(
              requestToApply,
              reviewedPlanFingerprint,
              plan!),
          RunTrackedInspectionAsync,
          TryPresentInspectionResult);
      await plan.InitializeWithinTrackedOperationAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      TryPresentInspectionResult(() => CurrentPage = plan);
    });
  }

  private async Task RunTrackedInspectionAsync(
      Func<CancellationToken, Task> operation)
  {
    ArgumentNullException.ThrowIfNull(operation);
    TrackedInspectionOperation tracked;
    lock (_inspectionGate)
    {
      if (_isDisposed || _activeInspection is not null)
      {
        return;
      }

      tracked = new TrackedInspectionOperation();
      _activeInspection = tracked;
      _isInspecting = true;
    }

    RaiseNavigationStates();
    try
    {
      await operation(tracked.Token);
    }
    finally
    {
      tracked.Complete();
      lock (_inspectionGate)
      {
        if (ReferenceEquals(_activeInspection, tracked))
        {
          _activeInspection = null;
          _isInspecting = false;
        }
      }

      RaiseNavigationStates();
    }
  }

  private bool TryPresentInspectionResult(Action presentation)
  {
    ArgumentNullException.ThrowIfNull(presentation);
    lock (_inspectionGate)
    {
      if (_isDisposed || _activeInspection is null)
      {
        return false;
      }

      presentation();
      return true;
    }
  }

  private async Task NavigateToExecutionAsync(
      RunRequest request,
      string reviewedPlanFingerprint,
      PlanViewModel reviewedPlan)
  {
    EnsureExecutionComposition();
    if (HasActiveExecution || _isDisposed)
    {
      throw new InvalidOperationException(
          "当前执行仍在清理，请等待其完成后再开始新的执行。");
    }

    if (_executionMonitor is not null)
    {
      _executionMonitor.PropertyChanged -= MonitorPropertyChanged;
      _executionMonitor.Dispose();
    }

    _executionMonitor = new ExecutionMonitorViewModel(
        _environmentRuns!,
        _runEvents!,
         _redactor!,
         _dispatcher!,
         request,
         reviewedPlanFingerprint);
    _executionMonitor.PropertyChanged += MonitorPropertyChanged;
    CurrentPage = _executionMonitor;
    Task operation = _executionMonitor.StartAsync();
    RaiseNavigationStates();
    await operation;
    RaiseNavigationStates();
    if (_executionMonitor.Run is { } completed &&
        reviewedPlan.TryPresentApprovalRejection(completed))
    {
      CurrentPage = reviewedPlan;
    }
    else if (_executionMonitor.Run is { } terminalRun && !_isDisposed)
    {
      NavigateToCompletion(terminalRun, reviewedPlan);
    }
  }

  private void NavigateToCompletion(ExecutionRun run, PlanViewModel? reviewedPlan)
  {
    CurrentPage = new CompletionViewModel(
        run,
        _reportExporter!,
        _redactor!,
        reviewedPlan is null
            ? NavigateToResourcesAsync
            : () => NavigateToReviewedPlanAsync(reviewedPlan),
        NavigateToProfilesAsync,
        reviewedPlan is null ? null : () => RetryFailedAsync(reviewedPlan));
  }

  private async Task RetryFailedAsync(PlanViewModel reviewedPlan)
  {
    if (_executionMonitor is null || _isDisposed)
    {
      return;
    }

    CurrentPage = _executionMonitor;
    Task operation = _executionMonitor.RetryFailedAsync(CancellationToken.None);
    RaiseNavigationStates();
    await operation;
    RaiseNavigationStates();
    if (_executionMonitor.Run is { } terminalRun && !_isDisposed)
    {
      NavigateToCompletion(terminalRun, reviewedPlan);
    }
  }

  private Task NavigateToReviewedPlanAsync(PlanViewModel reviewedPlan)
  {
    if (!HasActiveExecution && !_isDisposed)
    {
      ClearErrors();
      CurrentPage = reviewedPlan;
    }

    return Task.CompletedTask;
  }

  private bool HasActiveExecution => _isInspecting || _executionMonitor?.IsRunning == true;

  private void MonitorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
  {
    if (args.PropertyName == nameof(ExecutionMonitorViewModel.IsRunning))
    {
      RaiseNavigationStates();
    }
  }

  private void RaiseNavigationStates()
  {
    NavigateToProfilesCommand.RaiseCanExecuteChanged();
    NavigateToResourcesCommand.RaiseCanExecuteChanged();
  }

  private void EnsureExecutionComposition()
  {
    if (_environmentRuns is null ||
        _runEvents is null ||
        _redactor is null ||
        _dispatcher is null ||
        _reportExporter is null)
    {
      throw new InvalidOperationException("桌面执行服务尚未初始化。");
    }
  }

  private string ReportError(Exception exception)
  {
    ArgumentNullException.ThrowIfNull(exception);
    string message = UserErrorMessageFormatter.Format(exception);
    message = _redactor?.Redact(message) ?? message;
    ErrorMessage = message;
    return message;
  }

  private void ClearErrors()
  {
    ProfileSelection.ClearError();
    ResourceSelection?.ClearError();
    ErrorMessage = null;
  }

  private sealed class TrackedInspectionOperation
  {
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken Token => _cancellation.Token;

    public Task Completion => _completion.Task;

    public Task CancelAsync()
    {
      try
      {
        return _cancellation.CancelAsync();
      }
      catch (ObjectDisposedException)
      {
        return Task.CompletedTask;
      }
    }

    public void Complete()
    {
      _completion.TrySetResult();
      _cancellation.Dispose();
    }
  }
}
