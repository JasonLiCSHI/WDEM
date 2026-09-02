using System.Text.Json;
using System.Text.RegularExpressions;
using Wdem.Core.Runs;
using Wdem.Core.Tasks;
using Wdem.Core.Versions;
using Wdem.Core.Workflows;

namespace Wdem.Core.Profiles;

public static class ProfileParser
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    AllowTrailingCommas = true
  };

  public static EnvironmentProfile Parse(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);

    ProfileDto dto;
    try
    {
      dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions)
          ?? throw new FormatException("Profile JSON must contain an object.");
    }
    catch (JsonException exception)
    {
      throw new FormatException("Profile JSON is invalid.", exception);
    }

    var id = Required(dto.Id, "Profile id");
    var schemaVersion = dto.SchemaVersion ?? 1;
    if (schemaVersion is not (1 or 2))
    {
      throw new FormatException($"Unsupported Profile schemaVersion '{schemaVersion}'.");
    }
    var version = Required(dto.Version, "Profile version");
    var displayName = Required(dto.DisplayName, "Profile displayName");
    if (dto.Tasks is null || dto.Tasks.Count == 0)
    {
      throw new FormatException("Profile must declare at least one task.");
    }

    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
    foreach (var (taskIdValue, taskDto) in dto.Tasks)
    {
      var taskId = Required(taskIdValue, "Task id");
      if (taskDto is null)
      {
        throw new FormatException($"Task '{taskId}' must contain an object.");
      }

      var versionConstraint = Optional(taskDto.Version);
      if (versionConstraint is not null)
      {
        VersionConstraint.Parse(versionConstraint);
      }

      if (schemaVersion == 1 && taskDto.Workflow is not null)
      {
        throw new FormatException(
            $"Task '{taskId}' workflow requires Profile schemaVersion 2.");
      }

      var detect = Command(taskDto.Detect, $"Task '{taskId}' detect");
      var pre = Commands(taskDto.Pre, $"Task '{taskId}' pre");
      var apply = taskDto.Apply is null
          ? null
          : Command(taskDto.Apply, $"Task '{taskId}' apply");
      var post = Commands(taskDto.Post, $"Task '{taskId}' post");
      var workflow = taskDto.Workflow is null
          ? null
          : Workflow(taskDto.Workflow, $"Task '{taskId}' workflow");
      var task = new TaskDefinition(
          taskId,
          Required(taskDto.DisplayName, $"Task '{taskId}' displayName"),
          taskDto.Required,
          Strings(taskDto.DependsOn, $"Task '{taskId}' dependsOn"),
          versionConstraint,
          Optional(taskDto.PreferredVersion),
          Optional(taskDto.Source),
          detect,
          pre,
          apply,
          post,
          Optional(taskDto.Description),
          workflow);

      tasks.Add(taskId, task);
    }

    foreach (var task in tasks.Values)
    {
      foreach (var dependencyId in task.DependsOn)
      {
        if (!tasks.ContainsKey(dependencyId))
        {
          throw new FormatException(
              $"Task '{task.Id}' depends on undeclared task '{dependencyId}'.");
        }
      }
    }

    return new EnvironmentProfile(
        id,
        version,
        displayName,
        Optional(dto.Description),
        tasks,
        schemaVersion);
  }

  private static CommandDefinition[] Commands(
      IReadOnlyList<CommandDto?>? commands,
      string field)
  {
    if (commands is null)
    {
      return Array.Empty<CommandDefinition>();
    }

    return commands
        .Select((command, index) => Command(command, $"{field}[{index}]"))
        .ToArray();
  }

  private static TaskWorkflowDefinition Workflow(TaskWorkflowDto workflow, string field)
  {
    var initialState = Required(workflow.InitialState, $"{field} initialState");
    if (workflow.States is null || workflow.States.Count == 0)
    {
      throw new FormatException($"{field} must declare at least one state.");
    }

    try
    {
      var states = workflow.States.Select((state, index) =>
          WorkflowState(state, $"{field} states[{index}]")).ToArray();
      return new TaskWorkflowDefinition(
          initialState,
          states,
          workflow.MaxTransitions ?? 1024);
    }
    catch (ArgumentException exception)
    {
      throw new FormatException($"{field} is invalid: {exception.Message}", exception);
    }
  }

  private static TaskWorkflowState WorkflowState(TaskWorkflowStateDto? state, string field)
  {
    if (state is null)
    {
      throw new FormatException($"{field} must contain a state object.");
    }

    var id = Required(state.Id, $"{field} id");
    var taskStateText = Required(state.TaskState, $"{field} taskState");
    if (!Enum.TryParse<TaskExecutionState>(taskStateText, ignoreCase: true, out var taskState) ||
        taskState is TaskExecutionState.NotSelected or
            TaskExecutionState.Pending or
            TaskExecutionState.Ready or
            TaskExecutionState.Cancelling)
    {
      throw new FormatException($"{field} taskState '{taskStateText}' is not a runtime projection.");
    }

    TaskOutcome? terminalOutcome = null;
    if (Optional(state.Outcome) is { } outcomeText)
    {
      if (!Enum.TryParse<TaskOutcome>(outcomeText, ignoreCase: true, out var outcome))
      {
        throw new FormatException($"{field} outcome '{outcomeText}' is invalid.");
      }
      terminalOutcome = outcome;
    }

    var transitions = (state.Transitions ?? [])
        .Select((transition, index) => WorkflowTransition(
            transition,
            $"{field} transitions[{index}]"))
        .ToArray();
    return new TaskWorkflowState(
        id,
        taskState,
        entryActivities: WorkflowActivities(state.Entry, $"{field} entry"),
        residenceActivities: WorkflowActivities(state.Residence, $"{field} residence"),
        exitActivities: WorkflowActivities(state.Exit, $"{field} exit"),
        transitions: transitions,
        terminalOutcome: terminalOutcome,
        displayName: Optional(state.DisplayName),
        terminalError: Optional(state.Error));
  }

  private static WorkflowActivity[] WorkflowActivities(
      IReadOnlyList<WorkflowActivityDto?>? activities,
      string field) =>
      activities is null
          ? Array.Empty<WorkflowActivity>()
          : activities.Select((activity, index) => WorkflowActivity(
              activity,
              $"{field}[{index}]")).ToArray();

  private static CommandWorkflowActivity WorkflowActivity(
      WorkflowActivityDto? activity,
      string field)
  {
    if (activity is null)
    {
      throw new FormatException($"{field} must contain an Activity object.");
    }

    var id = Required(activity.Id, $"{field} id");
    var phase = Required(activity.Phase, $"{field} phase");
    var command = Command(
        new CommandDto
        {
          DisplayName = activity.DisplayName,
          Executable = activity.Executable,
          Arguments = activity.Arguments,
          VersionPattern = activity.VersionPattern
        },
        field);
    return new CommandWorkflowActivity(id, phase, command, Optional(activity.DisplayName));
  }

  private static TaskWorkflowTransition WorkflowTransition(
      TaskWorkflowTransitionDto? transition,
      string field)
  {
    if (transition is null)
    {
      throw new FormatException($"{field} must contain a transition object.");
    }

    var target = Required(transition.Target, $"{field} target");
    var condition = Required(transition.Condition, $"{field} condition");
    return condition.ToLowerInvariant() switch
    {
      "always" => TaskWorkflowTransition.Always(target),
      "activitiessucceeded" => TaskWorkflowTransition.WhenActivitiesSucceeded(target),
      "activitiesfailed" => TaskWorkflowTransition.WhenActivitiesFailed(target),
      "tasksatisfied" => TaskWorkflowTransition.WhenTaskSatisfied(target),
      "tasknotsatisfied" => TaskWorkflowTransition.WhenTaskNotSatisfied(target),
      _ => throw new FormatException($"{field} condition '{condition}' is invalid.")
    };
  }

  private static CommandDefinition Command(CommandDto? command, string field)
  {
    if (command is null)
    {
      throw new FormatException($"{field} must contain a command object.");
    }

    var executable = Required(command.Executable, $"{field} executable");
    var arguments = Strings(command.Arguments, $"{field} arguments");
    var versionPattern = Optional(command.VersionPattern);

    if (versionPattern is not null)
    {
      try
      {
        var regex = new Regex(versionPattern, RegexOptions.CultureInvariant);
        if (!regex.GetGroupNames().Contains("version", StringComparer.Ordinal))
        {
          throw new FormatException(
              $"{field} versionPattern must contain a named 'version' group.");
        }
      }
      catch (ArgumentException exception)
      {
        throw new FormatException($"{field} versionPattern is invalid.", exception);
      }
    }

    return new CommandDefinition(
        executable,
        arguments,
        versionPattern,
        Optional(command.DisplayName));
  }

  private static string[] Strings(IReadOnlyList<string?>? values, string field)
  {
    if (values is null)
    {
      return Array.Empty<string>();
    }

    if (values.Any(value => value is null))
    {
      throw new FormatException($"{field} cannot contain null values.");
    }

    return values.Cast<string>().ToArray();
  }

  private static string Required(string? value, string field) =>
      string.IsNullOrWhiteSpace(value)
          ? throw new FormatException($"{field} is required.")
          : value;

  private static string? Optional(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value;

  private sealed class ProfileDto
  {
    public int? SchemaVersion { get; init; }

    public string? Id { get; init; }

    public string? Version { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, TaskDto?>? Tasks { get; init; }
  }

  private sealed class TaskDto
  {
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }

    public List<string?>? DependsOn { get; init; }

    public string? Version { get; init; }

    public string? PreferredVersion { get; init; }

    public string? Source { get; init; }

    public CommandDto? Detect { get; init; }

    public List<CommandDto?>? Pre { get; init; }

    public CommandDto? Apply { get; init; }

    public List<CommandDto?>? Post { get; init; }

    public TaskWorkflowDto? Workflow { get; init; }
  }

  private sealed class CommandDto
  {
    public string? DisplayName { get; init; }

    public string? Executable { get; init; }

    public List<string?>? Arguments { get; init; }

    public string? VersionPattern { get; init; }
  }

  private sealed class TaskWorkflowDto
  {
    public string? InitialState { get; init; }

    public int? MaxTransitions { get; init; }

    public List<TaskWorkflowStateDto?>? States { get; init; }
  }

  private sealed class TaskWorkflowStateDto
  {
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? TaskState { get; init; }

    public List<WorkflowActivityDto?>? Entry { get; init; }

    public List<WorkflowActivityDto?>? Residence { get; init; }

    public List<WorkflowActivityDto?>? Exit { get; init; }

    public List<TaskWorkflowTransitionDto?>? Transitions { get; init; }

    public string? Outcome { get; init; }

    public string? Error { get; init; }
  }

  private sealed class WorkflowActivityDto
  {
    public string? Id { get; init; }

    public string? Phase { get; init; }

    public string? DisplayName { get; init; }

    public string? Executable { get; init; }

    public List<string?>? Arguments { get; init; }

    public string? VersionPattern { get; init; }
  }

  private sealed class TaskWorkflowTransitionDto
  {
    public string? Target { get; init; }

    public string? Condition { get; init; }
  }
}
