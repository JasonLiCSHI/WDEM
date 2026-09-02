using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wdem.Core.Runs;
using Wdem.Core.Tasks;
using Wdem.Core.Workflows;

namespace Wdem.App;

public enum TaskVisualState
{
  Pending,
  Running,
  Cancelling,
  Satisfied,
  UpgradeRequired,
  NeedsAttention,
  Succeeded,
  Failed,
  Cancelled,
  Blocked
}

public sealed class TaskRow(TaskDefinition definition) : INotifyPropertyChanged
{
  private bool _isSelected = definition.Required;
  private string _status = I18n.Get("PendingStatus");
  private string _stage = string.Empty;
  private int _percent;
  private string? _detectedVersion;
  private bool _canStart;
  private bool _canCancel;
  private bool _isSelectionEnabled;
  private bool _stateCanStart;
  private bool _stateCanSelect;
  private bool _workspaceTaskActionsEnabled;
  private string _lastResult = I18n.Get("NoResult");
  private TaskVisualState _visualState = TaskVisualState.Pending;

  public string Id { get; } = definition.Id;

  public string DisplayName { get; } = definition.DisplayName;

  public string Description { get; } = ValueOrPlaceholder(definition.Description);

  public bool Required { get; } = definition.Required;

  public bool IsOptional => !Required;

  public bool IsSelected
  {
    get => _isSelected;
    set
    {
      if (!Required)
      {
        SetField(ref _isSelected, value);
      }
    }
  }

  public string Dependencies { get; } = definition.DependsOn.Count == 0
      ? "—"
      : string.Join(", ", definition.DependsOn);

  public string Source { get; } = ValueOrPlaceholder(definition.Source);

  public string VersionConstraint { get; } = ValueOrPlaceholder(definition.VersionConstraint);

  public string PreferredVersion { get; } = ValueOrPlaceholder(definition.PreferredVersion);

  public string PipelineSummary { get; } = definition.Workflow is null
      ? I18n.Format("PipelineSummary", definition.Pre.Count, definition.Post.Count)
      : I18n.Format(
          "ComposableWorkflowSummary",
          definition.Workflow.States.Count,
          definition.Workflow.ActivityCount);

  public bool HasCustomWorkflow { get; } = definition.Workflow is not null;

  public string WorkflowDetails { get; } = definition.Workflow is null
      ? "—"
      : FormatWorkflow(definition.Workflow);

  public string DetectDetails { get; } = FormatCommand(definition.Detect);

  public IReadOnlyList<TaskActivityRow> PreActivities { get; } = CreateActivityRows(
      definition.Pre,
      I18n.Get("PreActivityBadge"),
      "PreActivityFallback");

  public string PreDetails { get; } = FormatCommands(definition.Pre);

  public string ApplyDetails { get; } = definition.Apply is null
      ? I18n.Get("NoCommand")
      : FormatCommand(definition.Apply);

  public string PostDetails { get; } = FormatCommands(definition.Post);

  public IReadOnlyList<TaskActivityRow> PostActivities { get; } = CreateActivityRows(
      definition.Post,
      I18n.Get("PostActivityBadge"),
      "PostActivityFallback");

  public string VerifyDetails { get; } =
      $"{I18n.Get("VerifyUsesDetect")}: {FormatCommand(definition.Detect)}";

  public string LastResult
  {
    get => _lastResult;
    set => SetField(ref _lastResult, value);
  }

  public string DetectedVersionDisplay => ValueOrPlaceholder(_detectedVersion);

  public TaskVisualState VisualState
  {
    get => _visualState;
    set
    {
      if (SetField(ref _visualState, value))
      {
        OnPropertyChanged(nameof(StatusGlyph));
      }
    }
  }

  public string StatusGlyph => VisualState switch
  {
    TaskVisualState.Satisfied or TaskVisualState.Succeeded => "✓",
    TaskVisualState.UpgradeRequired or TaskVisualState.NeedsAttention or
        TaskVisualState.Failed or TaskVisualState.Blocked => "!",
    TaskVisualState.Running => "↻",
    TaskVisualState.Cancelling or TaskVisualState.Cancelled => "■",
    _ => "•"
  };

  public bool CanStart
  {
    get => _canStart;
    private set => SetField(ref _canStart, value);
  }

  public bool CanCancel
  {
    get => _canCancel;
    private set => SetField(ref _canCancel, value);
  }

  public bool IsSelectionEnabled
  {
    get => _isSelectionEnabled;
    private set => SetField(ref _isSelectionEnabled, value);
  }

  public string Status
  {
    get => _status;
    set => SetField(ref _status, value);
  }

  public string Stage
  {
    get => _stage;
    set => SetField(ref _stage, value);
  }

  public int Percent
  {
    get => _percent;
    set => SetField(ref _percent, value);
  }

  public string? DetectedVersion
  {
    get => _detectedVersion;
    set
    {
      if (SetField(ref _detectedVersion, value))
      {
        OnPropertyChanged(nameof(DetectedVersionDisplay));
      }
    }
  }

  public void ApplyCapabilities(TaskCapabilities capabilities)
  {
    _stateCanStart = capabilities.CanStart;
    _stateCanSelect = capabilities.CanSelect;
    CanCancel = capabilities.CanCancel;
    RefreshEffectiveCapabilities();
  }

  public void SetWorkspaceTaskActionsEnabled(bool enabled)
  {
    _workspaceTaskActionsEnabled = enabled;
    RefreshEffectiveCapabilities();
  }

  private void RefreshEffectiveCapabilities()
  {
    CanStart = _workspaceTaskActionsEnabled && _stateCanStart;
    IsSelectionEnabled = _workspaceTaskActionsEnabled && _stateCanSelect && IsOptional;
  }

  private static string ValueOrPlaceholder(string? value) =>
      string.IsNullOrWhiteSpace(value) ? "—" : value;

  private static string FormatWorkflow(TaskWorkflowDefinition workflow) =>
      string.Join(
          Environment.NewLine,
          workflow.States.Values.Select(state => I18n.Format(
              "WorkflowStateDetail",
              state.DisplayName,
              state.TaskState,
              FormatActivityNames(state.EntryActivities),
              FormatActivityNames(state.ResidenceActivities),
              FormatActivityNames(state.ExitActivities),
              state.IsTerminal
                  ? state.TerminalOutcome
                  : string.Join(", ", state.Transitions.Select(
                      transition => $"{transition.Name} → {transition.TargetStateId}")))));

  private static string FormatActivityNames(IReadOnlyList<WorkflowActivity> activities) =>
      activities.Count == 0
          ? I18n.Get("NoSteps")
          : string.Join(", ", activities.Select(activity => activity.DisplayName));

  private static string FormatCommands(IReadOnlyList<CommandDefinition> commands) =>
      commands.Count == 0
          ? I18n.Get("NoSteps")
          : string.Join(
              Environment.NewLine,
              commands.Select((command, index) => $"{index + 1}. {FormatCommand(command)}"));

  private static TaskActivityRow[] CreateActivityRows(
      IReadOnlyList<CommandDefinition> commands,
      string phaseLabel,
      string fallbackResourceKey) =>
      commands
          .Select((command, index) => new TaskActivityRow(
              phaseLabel,
              string.IsNullOrWhiteSpace(command.DisplayName)
                  ? I18n.Format(fallbackResourceKey, index + 1)
                  : command.DisplayName))
          .ToArray();

  private static string FormatCommand(CommandDefinition command)
  {
    var invocation = string.Join(
        " ",
        command.Arguments.Select(QuoteArgument).Prepend(command.Executable));
    return string.IsNullOrWhiteSpace(command.DisplayName)
        ? invocation
        : $"{command.DisplayName} — {invocation}";
  }

  private static string QuoteArgument(string value) =>
      value.Any(char.IsWhiteSpace)
          ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
          : value;

  private bool SetField<T>(
      ref T field,
      T value,
      [CallerMemberName] string? propertyName = null)
  {
    if (EqualityComparer<T>.Default.Equals(field, value))
    {
      return false;
    }

    field = value;
    OnPropertyChanged(propertyName);
    return true;
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TaskActivityRow(string PhaseLabel, string DisplayName);
