using System.Collections.ObjectModel;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class PlanViewModel : ObservableObject
{
  private readonly IEnvironmentRunService _runService;
  private readonly LogRedactor _redactor;
  private readonly RunRequest _request;
  private readonly Func<RunRequest, Task> _startExecution;
  private bool _isLoading;
  private bool _canApply;
  private string? _errorMessage;

  public PlanViewModel(
      IEnvironmentRunService runService,
      LogRedactor redactor,
      RunRequest request,
      Func<RunRequest, Task> startExecution)
  {
    ArgumentNullException.ThrowIfNull(runService);
    ArgumentNullException.ThrowIfNull(redactor);
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(startExecution);
    _runService = runService;
    _redactor = redactor;
    _request = request;
    _startExecution = startExecution;
    Layers = new ObservableCollection<PlanLayerViewModel>();
    Resources = new ObservableCollection<PlanResourceViewModel>();
    Errors = new ObservableCollection<string>();
    InspectCommand = new AsyncRelayCommand(
        _ => InitializeAsync(),
        _ => !IsLoading,
        ReportError);
    ApplyCommand = new AsyncRelayCommand(
        _ => ApplyAsync(),
        _ => CanApply && !IsLoading,
        ReportError);
  }

  public ObservableCollection<PlanLayerViewModel> Layers { get; }

  public ObservableCollection<PlanResourceViewModel> Resources { get; }

  public ObservableCollection<string> Errors { get; }

  public bool IsLoading
  {
    get => _isLoading;
    private set
    {
      if (SetProperty(ref _isLoading, value))
      {
        InspectCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
      }
    }
  }

  public bool CanApply
  {
    get => _canApply;
    private set
    {
      if (SetProperty(ref _canApply, value))
      {
        ApplyCommand.RaiseCanExecuteChanged();
      }
    }
  }

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public AsyncRelayCommand InspectCommand { get; }

  public AsyncRelayCommand ApplyCommand { get; }

  public async Task InitializeAsync()
  {
    ErrorMessage = null;
    IsLoading = true;
    CanApply = false;
    try
    {
      ExecutionRun inspection = await _runService.InspectAsync(
          _request,
          CancellationToken.None);
      ExecutionPlan plan = inspection.Plan ?? throw new InvalidOperationException(
          "环境检查未生成执行计划。");
      Present(plan);
    }
    finally
    {
      IsLoading = false;
    }
  }

  private Task ApplyAsync()
  {
    if (!CanApply)
    {
      return Task.CompletedTask;
    }

    return _startExecution(_request);
  }

  private void Present(ExecutionPlan plan)
  {
    Layers.Clear();
    foreach (var layer in plan.Layers.OrderBy(layer => layer.Index))
    {
      Layers.Add(new PlanLayerViewModel(
          layer.Index,
          layer.ResourceIds.Select(_redactor.Redact).ToArray()));
    }

    Resources.Clear();
    foreach (PlannedResource resource in plan.Resources)
    {
      Resources.Add(new PlanResourceViewModel(resource, _redactor));
    }

    Errors.Clear();
    foreach (var error in plan.Errors
                 .Concat(plan.Resources.SelectMany(resource => resource.Diagnostics))
                 .Distinct())
    {
      StructuredError redacted = _redactor.Redact(error);
      Errors.Add($"{redacted.Summary} {redacted.Detail}");
    }

    CanApply = plan.IsExecutable &&
        Errors.Count == 0 &&
        plan.Resources.All(resource => resource.ResourcePlan.IsExecutable);
  }

  private void ReportError(Exception exception)
  {
    ErrorMessage = UserErrorMessageFormatter.Format(exception);
    CanApply = false;
  }
}

public sealed record PlanLayerViewModel(int Index, IReadOnlyList<string> ResourceIds)
{
  public string Title => $"层 {Index + 1}";

  public string ResourcesDisplay => string.Join("、", ResourceIds);
}

public sealed class PlanResourceViewModel
{
  internal PlanResourceViewModel(PlannedResource resource, LogRedactor redactor)
  {
    Id = redactor.Redact(resource.Definition.Id);
    DisplayName = redactor.Redact(resource.Definition.DisplayName ?? resource.Definition.Id);
    Provider = redactor.Redact(resource.ResourcePlan.ProviderName);
    Action = string.Join(
        ", ",
        resource.ResourcePlan.Steps
            .Select(step => step.Action.ToString())
            .Distinct(StringComparer.Ordinal));
    if (string.IsNullOrEmpty(Action))
    {
      Action = "None";
    }

    Privilege = resource.RequiresElevation ? "Administrator" : "CurrentUser";
    RestartPolicy = resource.RestartPolicy.ToString();
    Dependencies = resource.Dependencies.Select(redactor.Redact).ToArray();
    DependenciesDisplay = Dependencies.Count == 0 ? "无" : string.Join("、", Dependencies);
    Status = resource.Status.ToString();
    Error = resource.Diagnostics.Count == 0
        ? resource.Reason is null ? null : redactor.Redact(resource.Reason)
        : string.Join(
            Environment.NewLine,
            resource.Diagnostics.Select(error =>
            {
              StructuredError redacted = redactor.Redact(error);
              return $"{redacted.Summary} {redacted.Detail}";
            }));
  }

  public string Id { get; }

  public string DisplayName { get; }

  public string Provider { get; }

  public string ProviderDisplay => $"提供程序：{Provider}";

  public string Action { get; }

  public string ActionDisplay => $"操作：{Action}";

  public string Privilege { get; }

  public string PrivilegeDisplay => $"权限：{Privilege}";

  public string RestartPolicy { get; }

  public string RestartPolicyDisplay => $"重启策略：{RestartPolicy}";

  public IReadOnlyList<string> Dependencies { get; }

  public string DependenciesDisplay { get; }

  public string DependenciesLabel => $"依赖：{DependenciesDisplay}";

  public string Status { get; }

  public string StatusDisplay => $"状态：{Status}";

  public string? Error { get; }
}
