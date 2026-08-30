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
  private readonly IEnvironmentRunService? _environmentRuns;
  private readonly IRunEventSink? _runEvents;
  private readonly LogRedactor? _redactor;
  private readonly IUiDispatcher? _dispatcher;
  private readonly IRunReportExporter? _reportExporter;
  private object _currentPage;
  private ResourceSelectionViewModel? _resourceSelection;
  private ExecutionMonitorViewModel? _executionMonitor;
  private string? _errorMessage;
  private bool _isDisposed;

  public MainWindowViewModel(
      IProfileCatalog catalog,
      ResourceGraphBuilder graphBuilder,
      IEnvironmentRunService? environmentRuns = null,
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

  public Task InitializeAsync() => ProfileSelection.LoadCommand.ExecuteAsync(null);

  public async ValueTask DisposeAsync()
  {
    if (_isDisposed)
    {
      return;
    }

    _isDisposed = true;
    RaiseNavigationStates();
    ExecutionMonitorViewModel? monitor = _executionMonitor;
    if (monitor is not null)
    {
      await monitor.DisposeAsync().ConfigureAwait(false);
      monitor.PropertyChanged -= MonitorPropertyChanged;
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
    ClearErrors();
    EnsureExecutionComposition();
    ProfileLoadResult loaded = await _catalog.LoadAsync(request.Profile.Id);
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
    PlanViewModel? plan = null;
    plan = new PlanViewModel(
        _environmentRuns!,
        _redactor!,
        runRequest,
        requestToApply => NavigateToExecutionAsync(requestToApply, plan!));
    CurrentPage = plan;
    await plan.InitializeAsync();
  }

  private async Task NavigateToExecutionAsync(
      RunRequest request,
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
        request);
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
      CurrentPage = new CompletionViewModel(
          terminalRun,
          _reportExporter!,
          _redactor,
          () => NavigateToReviewedPlanAsync(reviewedPlan),
          NavigateToProfilesAsync);
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

  private bool HasActiveExecution => _executionMonitor?.IsRunning == true;

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
    ErrorMessage = message;
    return message;
  }

  private void ClearErrors()
  {
    ProfileSelection.ClearError();
    ResourceSelection?.ClearError();
    ErrorMessage = null;
  }
}
