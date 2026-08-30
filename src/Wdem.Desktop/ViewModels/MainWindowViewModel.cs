using Wdem.Core.Graph;
using Wdem.Core.Execution;
using Wdem.Core.Profiles;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
  private readonly ResourceGraphBuilder _graphBuilder;
  private readonly IProfileCatalog _catalog;
  private readonly IEnvironmentRunService? _environmentRuns;
  private readonly IRunEventSink? _runEvents;
  private readonly LogRedactor? _redactor;
  private readonly IUiDispatcher? _dispatcher;
  private object _currentPage;
  private ResourceSelectionViewModel? _resourceSelection;
  private ExecutionMonitorViewModel? _executionMonitor;
  private string? _errorMessage;

  public MainWindowViewModel(
      IProfileCatalog catalog,
      ResourceGraphBuilder graphBuilder,
      IEnvironmentRunService? environmentRuns = null,
      IRunEventSink? runEvents = null,
      LogRedactor? redactor = null,
      IUiDispatcher? dispatcher = null)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(graphBuilder);
    _catalog = catalog;
    _graphBuilder = graphBuilder;
    _environmentRuns = environmentRuns;
    _runEvents = runEvents;
    _redactor = redactor;
    _dispatcher = dispatcher;
    ProfileSelection = new ProfileSelectionViewModel(
        catalog,
        SelectProfile,
        ReportError,
        ClearErrors);
    _currentPage = ProfileSelection;
    NavigateToProfilesCommand = new AsyncRelayCommand(
        _ => NavigateToProfilesAsync(),
        onError: exception => _ = ReportError(exception));
    NavigateToResourcesCommand = new AsyncRelayCommand(
        _ => NavigateToResourcesAsync(),
        _ => ResourceSelection is not null,
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
    ClearErrors();
    CurrentPage = ProfileSelection;
    return Task.CompletedTask;
  }

  private Task NavigateToResourcesAsync()
  {
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
    var plan = new PlanViewModel(
        _environmentRuns!,
        _redactor!,
        runRequest,
        NavigateToExecutionAsync);
    CurrentPage = plan;
    await plan.InitializeAsync();
  }

  private async Task NavigateToExecutionAsync(RunRequest request)
  {
    EnsureExecutionComposition();
    _executionMonitor?.Dispose();
    _executionMonitor = new ExecutionMonitorViewModel(
        _environmentRuns!,
        _runEvents!,
        _redactor!,
        _dispatcher!,
        request);
    CurrentPage = _executionMonitor;
    await _executionMonitor.StartAsync();
  }

  private void EnsureExecutionComposition()
  {
    if (_environmentRuns is null ||
        _runEvents is null ||
        _redactor is null ||
        _dispatcher is null)
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
