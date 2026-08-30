using System.Collections.ObjectModel;
using Wdem.Core.Execution;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class ResourceProgressViewModel : ObservableObject
{
  private ExecutionState _state;
  private ExecutionOutcome? _outcome;
  private double _percent;
  private string? _message;
  private StructuredError? _error;

  public ResourceProgressViewModel(string id, string? displayName = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    Id = id;
    DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
    Steps = new ObservableCollection<StepProgressViewModel>();
  }

  public string Id { get; }

  public string DisplayName { get; }

  public ExecutionState State
  {
    get => _state;
    internal set => SetProperty(ref _state, value);
  }

  public ExecutionOutcome? Outcome
  {
    get => _outcome;
    internal set => SetProperty(ref _outcome, value);
  }

  public double Percent
  {
    get => _percent;
    internal set => SetProperty(ref _percent, Math.Clamp(value, 0, 100));
  }

  public string? Message
  {
    get => _message;
    internal set => SetProperty(ref _message, value);
  }

  public StructuredError? Error
  {
    get => _error;
    internal set
    {
      if (SetProperty(ref _error, value))
      {
        OnPropertyChanged(nameof(ErrorDetail));
      }
    }
  }

  public string? ErrorDetail => Error?.Detail;

  public ObservableCollection<StepProgressViewModel> Steps { get; }

  internal StepProgressViewModel GetOrAddStep(string stepId)
  {
    var existing = Steps.FirstOrDefault(step =>
        string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
    {
      return existing;
    }

    var created = new StepProgressViewModel(stepId);
    Steps.Add(created);
    return created;
  }

  internal void Apply(ResourceResult result, LogRedactor redactor)
  {
    State = result.State;
    Outcome = result.Outcome;
    Percent = result.Progress * 100;
    Message = result.Message is null ? null : redactor.Redact(result.Message);
    Error = result.Error is null ? null : redactor.Redact(result.Error);
    Steps.Clear();
    foreach (StepResult stepResult in result.StepResults)
    {
      var step = GetOrAddStep(redactor.Redact(stepResult.StepId));
      step.Name = redactor.Redact(stepResult.Name);
      step.State = stepResult.State;
      step.Outcome = stepResult.Outcome;
      step.Percent = stepResult.Progress * 100;
      step.Error = stepResult.Error is null ? null : redactor.Redact(stepResult.Error);
    }
  }
}

public sealed class StepProgressViewModel : ObservableObject
{
  private string _name;
  private ExecutionState _state;
  private ExecutionOutcome? _outcome;
  private double _percent;
  private string? _message;
  private StructuredError? _error;

  internal StepProgressViewModel(string id)
  {
    Id = id;
    _name = id;
  }

  public string Id { get; }

  public string Name
  {
    get => _name;
    internal set => SetProperty(ref _name, value);
  }

  public ExecutionState State
  {
    get => _state;
    internal set => SetProperty(ref _state, value);
  }

  public ExecutionOutcome? Outcome
  {
    get => _outcome;
    internal set => SetProperty(ref _outcome, value);
  }

  public double Percent
  {
    get => _percent;
    internal set => SetProperty(ref _percent, Math.Clamp(value, 0, 100));
  }

  public string? Message
  {
    get => _message;
    internal set => SetProperty(ref _message, value);
  }

  public StructuredError? Error
  {
    get => _error;
    internal set => SetProperty(ref _error, value);
  }
}
