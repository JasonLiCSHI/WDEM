using System.Collections.ObjectModel;
using System.Windows.Input;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;

namespace Wdem.Desktop.ViewModels;

public enum ResourceSelectionAction
{
  CheckEnvironment,
  StartConfiguration
}

public sealed record ResourceSelectionNavigationRequest(
    ResourceSelectionAction Action,
    DeveloperProfile Profile,
    ProfileSelection Selection,
    ResourceGraph Graph);

public sealed class ResourceSelectionViewModel : ObservableObject
{
  private readonly DeveloperProfile _profile;
  private readonly ResourceGraphBuilder _graphBuilder;
  private readonly HashSet<string> _requiredIds;
  private readonly HashSet<string> _optionalIds;
  private readonly HashSet<string> _selectedOptionalIds;
  private readonly Func<Exception, string> _reportError;
  private readonly Action _clearError;
  private ResourceGraph? _resolvedGraph;
  private string? _errorMessage;

  public ResourceSelectionViewModel(
      DeveloperProfile profile,
      ResourceGraphBuilder graphBuilder,
      Action<ResourceSelectionNavigationRequest>? navigateToPlan = null,
      Func<Exception, string>? reportError = null,
      Action? clearError = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(graphBuilder);
    _profile = profile;
    _graphBuilder = graphBuilder;
    _reportError = reportError ?? UserErrorMessageFormatter.Format;
    _clearError = clearError ?? (() => { });
    _requiredIds = new HashSet<string>(
        profile.RequiredResources.Select(resource => resource.Id),
        StringComparer.OrdinalIgnoreCase);
    _optionalIds = new HashSet<string>(
        profile.OptionalResources.Select(resource => resource.Id),
        StringComparer.OrdinalIgnoreCase);
    _selectedOptionalIds = new HashSet<string>(
        profile.OptionalResources
            .Where(resource => resource.DefaultSelected)
            .Select(resource => resource.Id),
        StringComparer.OrdinalIgnoreCase);

    var orderedIds = profile.RequiredResources
        .Concat(profile.OptionalResources)
        .Select(reference => reference.Id)
        .Concat(profile.Resources.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase);
    Resources = new ReadOnlyObservableCollection<ResourceSelectionItemViewModel>(
        new ObservableCollection<ResourceSelectionItemViewModel>(orderedIds.Select(CreateItem)));
    RequiredResources = new ObservableCollection<ResourceSelectionItemViewModel>();
    OptionalResources = new ObservableCollection<ResourceSelectionItemViewModel>();
    AutoDependencies = new ObservableCollection<ResourceSelectionItemViewModel>();

    CheckEnvironmentCommand = new AsyncRelayCommand(
        _ => NavigateAsync(ResourceSelectionAction.CheckEnvironment, navigateToPlan),
        onError: ReportError);
    StartConfigurationCommand = new AsyncRelayCommand(
        _ => NavigateAsync(ResourceSelectionAction.StartConfiguration, navigateToPlan),
        onError: ReportError);

    RecalculateSelection();
  }

  public string ProfileName => _profile.DisplayName;

  public string ProfileNameDisplay => $"配置文件：{ProfileName}";

  public ReadOnlyObservableCollection<ResourceSelectionItemViewModel> Resources { get; }

  public ObservableCollection<ResourceSelectionItemViewModel> RequiredResources { get; }

  public ObservableCollection<ResourceSelectionItemViewModel> OptionalResources { get; }

  public ObservableCollection<ResourceSelectionItemViewModel> AutoDependencies { get; }

  public ProfileSelection Selection => new(new HashSet<string>(
      _selectedOptionalIds,
      StringComparer.OrdinalIgnoreCase));

  public ResourceGraph ResolvedGraph => _resolvedGraph ??
      throw new InvalidOperationException("The resource graph has not been resolved.");

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public ICommand CheckEnvironmentCommand { get; }

  public ICommand StartConfigurationCommand { get; }

  private ResourceSelectionItemViewModel CreateItem(string id)
  {
    var resource = _profile.Resources[id];
    return new ResourceSelectionItemViewModel(
        resource.Id,
        resource.DisplayName ?? resource.Id,
        $"{resource.Type} · {resource.Provider}",
        ChangeSelection);
  }

  private void ChangeSelection(ResourceSelectionItemViewModel item, bool isSelected)
  {
    if (!_optionalIds.Contains(item.Id))
    {
      return;
    }

    ClearErrors();

    if (isSelected)
    {
      _selectedOptionalIds.Add(item.Id);
    }
    else
    {
      _selectedOptionalIds.Remove(item.Id);
    }

    try
    {
      RecalculateSelection();
    }
    catch (Exception exception)
    {
      if (isSelected)
      {
        _selectedOptionalIds.Remove(item.Id);
      }
      else
      {
        _selectedOptionalIds.Add(item.Id);
      }

      RecalculateSelection();
      ReportError(exception);
    }
  }

  private void RecalculateSelection()
  {
    ResourceGraphBuildResult result = _graphBuilder.TryBuild(_profile, Selection);
    if (result.Errors.Count > 0)
    {
      throw new StructuredErrorException(result.Errors[0]);
    }

    ResourceGraph graph = result.Graph!;
    _resolvedGraph = graph;

    foreach (ResourceSelectionItemViewModel item in Resources)
    {
      if (graph.Nodes.TryGetValue(item.Id, out ResolvedResource? resolved))
      {
        bool canChange = resolved.Origin == ResourceOrigin.SelectedOptional;
        item.ApplyState(resolved.Origin, isSelected: true, canChange);
      }
      else
      {
        item.ApplyState(
            _requiredIds.Contains(item.Id)
                ? ResourceOrigin.Required
                : ResourceOrigin.SelectedOptional,
            isSelected: false,
            canChangeSelection: _optionalIds.Contains(item.Id));
      }
    }

    ReplaceContents(
        RequiredResources,
        Resources.Where(item => item.Origin == ResourceOrigin.Required));
    ReplaceContents(
        OptionalResources,
        Resources.Where(item =>
            _optionalIds.Contains(item.Id) && item.Origin == ResourceOrigin.SelectedOptional));
    ReplaceContents(
        AutoDependencies,
        Resources.Where(item => item.Origin == ResourceOrigin.AutoDependency && item.IsSelected));
  }

  private static void ReplaceContents(
      ObservableCollection<ResourceSelectionItemViewModel> target,
      IEnumerable<ResourceSelectionItemViewModel> items)
  {
    target.Clear();
    foreach (ResourceSelectionItemViewModel item in items)
    {
      target.Add(item);
    }
  }

  private Task NavigateAsync(
      ResourceSelectionAction action,
      Action<ResourceSelectionNavigationRequest>? navigateToPlan)
  {
    ClearErrors();
    navigateToPlan?.Invoke(new ResourceSelectionNavigationRequest(
        action,
        _profile,
        Selection,
        ResolvedGraph));
    return Task.CompletedTask;
  }

  private void ClearErrors()
  {
    ClearError();
    _clearError();
  }

  private void ReportError(Exception exception) => ErrorMessage = _reportError(exception);

  internal void ClearError() => ErrorMessage = null;
}
