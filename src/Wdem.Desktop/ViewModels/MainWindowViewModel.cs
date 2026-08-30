using Wdem.Core.Graph;
using Wdem.Core.Profiles;

namespace Wdem.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
  private readonly ResourceGraphBuilder _graphBuilder;
  private object _currentPage;
  private ResourceSelectionViewModel? _resourceSelection;
  private string? _errorMessage;

  public MainWindowViewModel(IProfileCatalog catalog, ResourceGraphBuilder graphBuilder)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(graphBuilder);
    _graphBuilder = graphBuilder;
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
        NavigateToPlan,
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

  private void NavigateToPlan(ResourceSelectionNavigationRequest request)
  {
    CurrentPage = new PlanPagePlaceholderViewModel(
        request.Action == ResourceSelectionAction.CheckEnvironment ? "检查环境" : "开始配置",
        "执行计划页面将在下一阶段提供。当前尚未执行任何系统更改。",
        request);
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

public sealed record PlanPagePlaceholderViewModel(
    string Title,
    string Message,
    ResourceSelectionNavigationRequest Request);
