using System.Collections.ObjectModel;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class PlanViewModel : ObservableObject
{
  private readonly IEnvironmentRunService _runService;
  private readonly LogRedactor _redactor;
  private readonly RunRequest _request;
  private readonly Func<RunRequest, string, Task> _startExecution;
  private readonly Func<Func<CancellationToken, Task>, Task>? _runInspection;
  private readonly Func<Action, bool>? _presentInspection;
  private bool _isLoading;
  private bool _canApply;
  private string? _approvedPlanFingerprint;
  private string? _errorMessage;

  public PlanViewModel(
      IEnvironmentRunService runService,
      LogRedactor redactor,
      RunRequest request,
      Func<RunRequest, string, Task> startExecution,
      Func<Func<CancellationToken, Task>, Task>? runInspection = null,
      Func<Action, bool>? presentInspection = null)
  {
    ArgumentNullException.ThrowIfNull(runService);
    ArgumentNullException.ThrowIfNull(redactor);
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(startExecution);
    _runService = runService;
    _redactor = redactor;
    _request = request;
    _startExecution = startExecution;
    _runInspection = runInspection;
    _presentInspection = presentInspection;
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

  public Task InitializeAsync(CancellationToken cancellationToken = default) =>
      _runInspection is null
          ? InitializeCoreAsync(cancellationToken)
          : _runInspection(async windowCancellationToken =>
          {
            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    windowCancellationToken);
            await InitializeCoreAsync(linkedCancellation.Token);
          });

  internal Task InitializeWithinTrackedOperationAsync(CancellationToken cancellationToken) =>
      InitializeCoreAsync(cancellationToken);

  private async Task InitializeCoreAsync(CancellationToken cancellationToken)
  {
    ErrorMessage = null;
    IsLoading = true;
    CanApply = false;
    _approvedPlanFingerprint = null;
    try
    {
      ExecutionRun inspection = await _runService.InspectAsync(
          _request,
          cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      ExecutionPlan plan = inspection.Plan ?? throw new InvalidOperationException(
          "环境检查未生成执行计划。");
      if (_presentInspection is null)
      {
        Present(plan);
      }
      else if (!_presentInspection(() => Present(plan)))
      {
        cancellationToken.ThrowIfCancellationRequested();
      }
    }
    finally
    {
      IsLoading = false;
    }
  }

  internal bool TryPresentApprovalRejection(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    ExecutionPlan? plan = run.Plan;
    if (plan is null || !plan.Errors.Any(error =>
            error.Code == WdemErrorCode.ConfigurationError))
    {
      return false;
    }

    Present(plan);
    ErrorMessage = string.Join(
        Environment.NewLine,
        plan.Errors
            .Where(error => error.Code == WdemErrorCode.ConfigurationError)
            .Select(error =>
            {
              StructuredError redacted = _redactor.Redact(error);
              return $"{redacted.Summary} {redacted.Detail}";
            }));
    return true;
  }

  private Task ApplyAsync()
  {
    if (!CanApply)
    {
      return Task.CompletedTask;
    }

    return _startExecution(
        _request,
        _approvedPlanFingerprint ?? throw new InvalidOperationException(
            "The reviewed plan fingerprint is unavailable."));
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
        plan.Resources.All(resource =>
            resource.Status == PlannedResourceStatus.Deferred ||
            resource.ResourcePlan.IsExecutable);
    _approvedPlanFingerprint = CanApply ? plan.Fingerprint : null;
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
    var definition = ResourceDefinitionPresentationRedactor.Redact(
        resource.Definition,
        redactor);
    Id = redactor.Redact(definition.Id);
    DisplayName = definition.DisplayName ?? Id;
    Description = definition.Description ?? string.Empty;
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

  public string Description { get; }

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
