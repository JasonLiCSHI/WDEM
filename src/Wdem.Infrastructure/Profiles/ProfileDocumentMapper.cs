using System.Text.RegularExpressions;
using Wdem.Domain.Execution;
using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;
using Wdem.Domain.Workflows;

namespace Wdem.Infrastructure.Profiles;

internal static class ProfileDocumentMapper
{
  public static EnvironmentProfile Map(ProfileDocument document)
  {
    ArgumentNullException.ThrowIfNull(document);

    var id = Required(document.Id, "Profile id");
    var schemaVersion = document.SchemaVersion ?? 1;
    if (schemaVersion is not (1 or 2))
    {
      throw new FormatException($"Unsupported Profile schemaVersion '{schemaVersion}'.");
    }
    var version = Required(document.Version, "Profile version");
    var displayName = Required(document.DisplayName, "Profile displayName");
    if (document.Tasks is null || document.Tasks.Count == 0)
    {
      throw new FormatException("Profile must declare at least one task.");
    }

    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
    foreach (var (taskIdValue, taskDocument) in document.Tasks)
    {
      var taskId = Required(taskIdValue, "Task id");
      if (taskDocument is null)
      {
        throw new FormatException($"Task '{taskId}' must contain an object.");
      }

      var versionExpression = Optional(taskDocument.Version);
      var versionRequirement = versionExpression is null
          ? null
          : VersionRequirement.Parse(versionExpression);

      if (schemaVersion == 1 && taskDocument.Workflow is not null)
      {
        throw new FormatException(
            $"Task '{taskId}' workflow requires Profile schemaVersion 2.");
      }

      var detect = Command(taskDocument.Detect, $"Task '{taskId}' detect");
      var pre = Commands(taskDocument.Pre, $"Task '{taskId}' pre");
      var apply = taskDocument.Apply is null
          ? null
          : Command(taskDocument.Apply, $"Task '{taskId}' apply");
      var post = Commands(taskDocument.Post, $"Task '{taskId}' post");
      var workflow = taskDocument.Workflow is null
          ? null
          : Workflow(taskDocument.Workflow, $"Task '{taskId}' workflow");
      var task = new TaskDefinition(
          taskId,
          Required(taskDocument.DisplayName, $"Task '{taskId}' displayName"),
          taskDocument.Required,
          Strings(taskDocument.DependsOn, $"Task '{taskId}' dependsOn"),
          versionRequirement,
          Optional(taskDocument.PreferredVersion),
          Optional(taskDocument.Source),
          detect,
          pre,
          apply,
          post,
          Optional(taskDocument.Description),
          workflow);

      tasks.Add(taskId, task);
    }

    return new EnvironmentProfile(
        id,
        version,
        displayName,
        Optional(document.Description),
        tasks,
        schemaVersion);
  }

  private static CommandDefinition[] Commands(
      IReadOnlyList<CommandDocument?>? commands,
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

  private static TaskWorkflowDefinition Workflow(TaskWorkflowDocument workflow, string field)
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

  private static TaskWorkflowState WorkflowState(
      TaskWorkflowStateDocument? state,
      string field)
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
      IReadOnlyList<WorkflowActivityDocument?>? activities,
      string field) =>
      activities is null
          ? Array.Empty<WorkflowActivity>()
          : activities.Select((activity, index) => WorkflowActivity(
              activity,
              $"{field}[{index}]")).ToArray();

  private static CommandWorkflowActivity WorkflowActivity(
      WorkflowActivityDocument? activity,
      string field)
  {
    if (activity is null)
    {
      throw new FormatException($"{field} must contain an Activity object.");
    }

    var id = Required(activity.Id, $"{field} id");
    var phase = Required(activity.Phase, $"{field} phase");
    var command = Command(
        new CommandDocument
        {
          DisplayName = activity.DisplayName,
          Executable = activity.Executable,
          Arguments = activity.Arguments,
          VersionPattern = activity.VersionPattern,
          MissingExitCodes = activity.MissingExitCodes
        },
        field);
    return new CommandWorkflowActivity(id, phase, command, Optional(activity.DisplayName));
  }

  private static TaskWorkflowTransition WorkflowTransition(
      TaskWorkflowTransitionDocument? transition,
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

  private static CommandDefinition Command(CommandDocument? command, string field)
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
        Optional(command.DisplayName),
        MissingExitCodes(command.MissingExitCodes, field));
  }

  private static int[] MissingExitCodes(IReadOnlyList<int>? values, string field)
  {
    if (values is null)
    {
      return [];
    }

    if (values.Any(value => value <= 0))
    {
      throw new FormatException($"{field} missingExitCodes must contain only positive exit codes.");
    }

    return values.Distinct().ToArray();
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

}
